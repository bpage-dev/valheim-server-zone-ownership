using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ServerZoneOwnership
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.benpage.valheim.serverzoneownership";
        public const string PluginName = "ServerZoneOwnership";
        public const string PluginVersion = "0.3.1";
        private const float StatsLogIntervalSeconds = 20f;
        private float _statsTimer;

        internal static ManualLogSource Log;
        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            if (!IsDedicatedServer())
            {
                Log.LogInfo($"{PluginName} not running as dedicated server, patches skipped.");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            Log.LogInfo($"{PluginName} v{PluginVersion} patches applied.");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (_harmony == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            _statsTimer += Time.deltaTime;
            if (_statsTimer < StatsLogIntervalSeconds) return;
            _statsTimer = 0f;

            LogCoverageStats();
        }

        private static void LogCoverageStats()
        {
            var peers = ZNet.instance.GetPeers();
            int peerCount = peers?.Count ?? 0;
            if (peerCount == 0)
            {
                Log.LogInfo("[Coverage] 0 peers connected — no zones active.");
                return;
            }

            var activeRadius = ZoneSystem.instance != null ? ZoneSystem.instance.m_activeArea : 2;
            var sectors = ServerCoverage.GetActiveSectors(activeRadius);

            // Bounding box in sector coords
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var s in sectors)
            {
                if (s.x < minX) minX = s.x;
                if (s.x > maxX) maxX = s.x;
                if (s.y < minY) minY = s.y;
                if (s.y > maxY) maxY = s.y;
            }

            // World bbox: each sector is 64m; sector (i,j) center is at world (i*64, j*64)
            // Bounding box covers from (minX-0.5)*64 to (maxX+0.5)*64
            const float ZoneSize = 64f;
            float worldMinX = (minX - 0.5f) * ZoneSize;
            float worldMaxX = (maxX + 0.5f) * ZoneSize;
            float worldMinZ = (minY - 0.5f) * ZoneSize;
            float worldMaxZ = (maxY + 0.5f) * ZoneSize;

            Log.LogInfo(
                $"[Coverage] {sectors.Count} sectors owned by server across {peerCount} peer(s). " +
                $"Sector bbox: x[{minX}..{maxX}] z[{minY}..{maxY}]. " +
                $"World bbox: x[{worldMinX:F0}..{worldMaxX:F0}] z[{worldMinZ:F0}..{worldMaxZ:F0}] meters.");

            // Also log peer positions so it's easy to correlate.
            var sb = new System.Text.StringBuilder("[Coverage] Peer positions: ");
            bool first = true;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                if (!first) sb.Append(", ");
                first = false;
                var p = peer.GetRefPos();
                var sector = ZoneSystem.GetZone(p);
                sb.Append($"world({p.x:F0},{p.z:F0}) sector({sector.x},{sector.y})");
            }
            Log.LogInfo(sb.ToString());
        }

        private static bool IsDedicatedServer()
        {
            var cmd = System.Environment.CommandLine;
            return cmd.Contains("-batchmode") || cmd.Contains("-nographics");
        }
    }

    /// <summary>
    /// Cached reflection handles and reusable buffers so that per-tick work
    /// does not allocate or re-resolve reflection every frame.
    /// </summary>
    internal static class ReflectionCache
    {
        public static readonly MethodInfo ZoneSystem_PokeLocalZone =
            AccessTools.Method(typeof(ZoneSystem), "PokeLocalZone");
        public static readonly MethodInfo ZoneSystem_CreateGhostZones =
            AccessTools.Method(typeof(ZoneSystem), "CreateGhostZones");
        public static readonly MethodInfo ZoneSystem_UpdateTTL =
            AccessTools.Method(typeof(ZoneSystem), "UpdateTTL");
        public static readonly MethodInfo ZoneSystem_UpdatePrefabLifetimes =
            AccessTools.Method(typeof(ZoneSystem), "UpdatePrefabLifetimes");
        public static readonly FieldInfo ZoneSystem_updateTimer =
            AccessTools.Field(typeof(ZoneSystem), "m_updateTimer");

        public static readonly MethodInfo ZDOMan_FindObjects =
            AccessTools.Method(typeof(ZDOMan), "FindObjects");
        public static readonly FieldInfo ZDOMan_sessionID =
            AccessTools.Field(typeof(ZDOMan), "m_sessionID");
        public static readonly FieldInfo ZDOMan_releaseZDOTimer =
            AccessTools.Field(typeof(ZDOMan), "m_releaseZDOTimer");

        public static readonly MethodInfo ZNetScene_CreateObjects =
            AccessTools.Method(typeof(ZNetScene), "CreateObjects");
        public static readonly MethodInfo ZNetScene_RemoveObjects =
            AccessTools.Method(typeof(ZNetScene), "RemoveObjects");
        public static readonly FieldInfo ZNetScene_tempCurrentObjects =
            AccessTools.Field(typeof(ZNetScene), "m_tempCurrentObjects");
        public static readonly FieldInfo ZNetScene_tempCurrentDistantObjects =
            AccessTools.Field(typeof(ZNetScene), "m_tempCurrentDistantObjects");
    }

    /// <summary>
    /// Coverage helpers. Every tick, computes the set of sectors covered by
    /// the active area around each connected peer, deduplicated. Overlap
    /// between peers gets collapsed here so downstream systems only process
    /// each sector once.
    /// </summary>
    internal static class ServerCoverage
    {
        // Reusable buffers — do not allocate per-tick.
        private static readonly HashSet<Vector2i> s_activeSectors = new HashSet<Vector2i>();
        private static readonly HashSet<Vector2i> s_distantSectors = new HashSet<Vector2i>();
        private static readonly List<Vector3> s_uniquePeerPositions = new List<Vector3>();
        private static readonly HashSet<Vector2i> s_seenPeerSectors = new HashSet<Vector2i>();

        /// <summary>
        /// All sectors that fall within any peer's active area (Chebyshev distance
        /// of activeRadius sectors around the peer's zone).
        /// </summary>
        public static HashSet<Vector2i> GetActiveSectors(int activeRadius)
        {
            s_activeSectors.Clear();
            if (ZNet.instance == null) return s_activeSectors;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var center = ZoneSystem.GetZone(peer.GetRefPos());
                for (int y = -activeRadius; y <= activeRadius; y++)
                {
                    for (int x = -activeRadius; x <= activeRadius; x++)
                    {
                        s_activeSectors.Add(new Vector2i(center.x + x, center.y + y));
                    }
                }
            }
            return s_activeSectors;
        }

        /// <summary>
        /// Sectors in the distant ring (activeRadius+1 .. activeRadius+distantRadius)
        /// around each peer, minus anything already in the active set.
        /// </summary>
        public static HashSet<Vector2i> GetDistantSectors(int activeRadius, int distantRadius, HashSet<Vector2i> activeSectors)
        {
            s_distantSectors.Clear();
            if (ZNet.instance == null || distantRadius <= 0) return s_distantSectors;
            int outer = activeRadius + distantRadius;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var center = ZoneSystem.GetZone(peer.GetRefPos());
                for (int y = -outer; y <= outer; y++)
                {
                    for (int x = -outer; x <= outer; x++)
                    {
                        var s = new Vector2i(center.x + x, center.y + y);
                        if (!activeSectors.Contains(s)) s_distantSectors.Add(s);
                    }
                }
            }
            return s_distantSectors;
        }

        /// <summary>
        /// One representative Vector3 per unique peer sector — useful for
        /// APIs that take a world-space point rather than a sector.
        /// Peers standing in the same sector collapse to one entry.
        /// </summary>
        public static List<Vector3> GetUniquePeerPositions()
        {
            s_uniquePeerPositions.Clear();
            s_seenPeerSectors.Clear();
            if (ZNet.instance == null) return s_uniquePeerPositions;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var pos = peer.GetRefPos();
                var sector = ZoneSystem.GetZone(pos);
                if (s_seenPeerSectors.Add(sector))
                {
                    s_uniquePeerPositions.Add(pos);
                }
            }
            return s_uniquePeerPositions;
        }
    }

    /// <summary>
    /// Keep the centroid fallback for any vanilla system that reads
    /// GetReferencePosition without going through our own patches
    /// (RandEventSystem, EnvMan, etc.). Individual hot-path systems bypass
    /// this and iterate peers directly.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.GetReferencePosition))]
    internal static class ZNet_GetReferencePosition_Patch
    {
        static bool Prefix(ZNet __instance, ref Vector3 __result)
        {
            if (__instance == null || !__instance.IsServer())
                return true;

            var peers = __instance.GetPeers();
            if (peers == null || peers.Count == 0) return true;

            var sum = Vector3.zero;
            var count = 0;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                sum += peer.GetRefPos();
                count++;
            }
            if (count == 0) return true;

            __result = sum / count;
            return false;
        }
    }

    /// <summary>
    /// Server considers itself "in active area" for a sector if ANY connected
    /// peer's active area covers that sector. Prevents peers from stealing back
    /// server-owned ZDOs even when their positions diverge from the centroid.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "IsInPeerActiveArea")]
    internal static class ZDOMan_IsInPeerActiveArea_Patch
    {
        static bool Prefix(Vector2i sector, long uid, ref bool __result)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
                return true;
            var sessionId = ZDOMan.GetSessionID();
            if (uid != sessionId) return true;

            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (ZNetScene.InActiveArea(sector, peer.GetRefPos()))
                {
                    __result = true;
                    return false;
                }
            }
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// SpawnSystem (the biome repopulator) has a client-side null-check on
    /// Player.m_localPlayer that would prevent it from ever running on the
    /// dedicated server. Reimplement the method without that guard when
    /// running server-side with no local player.
    /// </summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawning")]
    internal static class SpawnSystem_UpdateSpawning_Patch
    {
        private static readonly MethodInfo UpdateSpawnListMethod =
            AccessTools.Method(typeof(SpawnSystem), "UpdateSpawnList");
        private static readonly MethodInfo GetPlayersInZoneMethod =
            AccessTools.Method(typeof(SpawnSystem), "GetPlayersInZone");
        private static readonly FieldInfo NViewField =
            AccessTools.Field(typeof(SpawnSystem), "m_nview");
        private static readonly FieldInfo SpawnListsField =
            AccessTools.Field(typeof(SpawnSystem), "m_spawnLists");
        private static readonly FieldInfo TempNearPlayersField =
            AccessTools.Field(typeof(SpawnSystem), "m_tempNearPlayers");

        static bool Prefix(SpawnSystem __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            if (Player.m_localPlayer != null) return true;

            var nview = (ZNetView)NViewField.GetValue(__instance);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;

            var tempNearPlayers = (List<Player>)TempNearPlayersField.GetValue(null);
            tempNearPlayers.Clear();
            GetPlayersInZoneMethod.Invoke(__instance, new object[] { tempNearPlayers });
            if (tempNearPlayers.Count == 0) return false;

            DateTime time = ZNet.instance.GetTime();
            var spawnLists = (List<SpawnSystemList>)SpawnListsField.GetValue(__instance);
            foreach (var spawnList in spawnLists)
            {
                UpdateSpawnListMethod.Invoke(__instance, new object[] { spawnList.m_spawners, time, false });
            }

            var currentSpawners = RandEventSystem.instance.GetCurrentSpawners();
            if (currentSpawners != null)
            {
                UpdateSpawnListMethod.Invoke(__instance, new object[] { currentSpawners, time, true });
            }
            return false;
        }
    }

    /// <summary>
    /// Replace ZoneSystem.Update on the server with a per-peer, sector-deduped
    /// version. Each unique sector across all peers' active areas is loaded
    /// exactly once per tick; overlapping peers collapse into shared work.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    internal static class ZoneSystem_Update_Patch
    {
        static bool Prefix(ZoneSystem __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            // Preserve vanilla throttle: run every 0.1s.
            float timer = (float)ReflectionCache.ZoneSystem_updateTimer.GetValue(__instance) + Time.deltaTime;
            if (timer <= 0.1f)
            {
                ReflectionCache.ZoneSystem_updateTimer.SetValue(__instance, timer);
                return false;
            }
            ReflectionCache.ZoneSystem_updateTimer.SetValue(__instance, 0f);

            var activeRadius = __instance.m_activeArea;
            var activeSectors = ServerCoverage.GetActiveSectors(activeRadius);

            // Load one zone per tick as vanilla does (PokeLocalZone returns true when it spawned).
            // Iterate unique sectors and stop after the first that spawned something.
            bool spawnedOne = false;
            foreach (var sector in activeSectors)
            {
                var result = (bool)ReflectionCache.ZoneSystem_PokeLocalZone.Invoke(__instance, new object[] { sector });
                if (result)
                {
                    spawnedOne = true;
                    break;
                }
            }

            ReflectionCache.ZoneSystem_UpdateTTL.Invoke(__instance, new object[] { 0.1f });

            // Ghost zones for extended (active + distant) area if we didn't spawn a local zone this tick.
            if (!spawnedOne)
            {
                foreach (var pos in ServerCoverage.GetUniquePeerPositions())
                {
                    ReflectionCache.ZoneSystem_CreateGhostZones.Invoke(__instance, new object[] { pos });
                }
            }

            ReflectionCache.ZoneSystem_UpdatePrefabLifetimes.Invoke(__instance, null);
            return false;
        }
    }

    /// <summary>
    /// Replace ZNetScene.CreateDestroyObjects on the server. Instead of scanning
    /// one area around a single reference position, walk every unique sector
    /// covered by any peer once (via ZDOMan.FindObjects) and merge results into
    /// the same temp lists CreateObjects/RemoveObjects use.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    internal static class ZNetScene_CreateDestroyObjects_Patch
    {
        // Local scratch objects — reused across ticks.
        private static readonly List<ZDO> s_sectorBuffer = new List<ZDO>();

        static bool Prefix(ZNetScene __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            var activeRadius = ZoneSystem.instance.m_activeArea;
            var distantRadius = ZoneSystem.instance.m_activeDistantArea;
            var activeSectors = ServerCoverage.GetActiveSectors(activeRadius);
            var distantSectors = ServerCoverage.GetDistantSectors(activeRadius, distantRadius, activeSectors);

            var currentObjects = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentObjects.GetValue(__instance);
            var distantObjects = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentDistantObjects.GetValue(__instance);
            currentObjects.Clear();
            distantObjects.Clear();

            var findObjects = ReflectionCache.ZDOMan_FindObjects;
            var zdoMan = ZDOMan.instance;

            foreach (var sector in activeSectors)
            {
                s_sectorBuffer.Clear();
                findObjects.Invoke(zdoMan, new object[] { sector, s_sectorBuffer });
                currentObjects.AddRange(s_sectorBuffer);
            }
            foreach (var sector in distantSectors)
            {
                s_sectorBuffer.Clear();
                findObjects.Invoke(zdoMan, new object[] { sector, s_sectorBuffer });
                distantObjects.AddRange(s_sectorBuffer);
            }

            ReflectionCache.ZNetScene_CreateObjects.Invoke(__instance, new object[] { currentObjects, distantObjects });
            ReflectionCache.ZNetScene_RemoveObjects.Invoke(__instance, new object[] { currentObjects, distantObjects });
            return false;
        }
    }

    /// <summary>
    /// Replace ZDOMan.ReleaseZDOS on the server. Instead of one claim pass around
    /// the server's own reference position plus per-peer release passes, iterate
    /// the unique-sector coverage set once and claim any unowned or absent-owner
    /// ZDOs for the server. Runs at the vanilla 2-second cadence.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
    internal static class ZDOMan_ReleaseZDOS_Patch
    {
        private static readonly List<ZDO> s_sectorBuffer = new List<ZDO>();

        static bool Prefix(ZDOMan __instance, float dt)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            float timer = (float)ReflectionCache.ZDOMan_releaseZDOTimer.GetValue(__instance) + dt;
            if (timer <= 2f)
            {
                ReflectionCache.ZDOMan_releaseZDOTimer.SetValue(__instance, timer);
                return false;
            }
            ReflectionCache.ZDOMan_releaseZDOTimer.SetValue(__instance, 0f);

            long sessionId = (long)ReflectionCache.ZDOMan_sessionID.GetValue(__instance);
            var activeRadius = ZoneSystem.instance.m_activeArea;
            var activeSectors = ServerCoverage.GetActiveSectors(activeRadius);
            var findObjects = ReflectionCache.ZDOMan_FindObjects;

            foreach (var sector in activeSectors)
            {
                s_sectorBuffer.Clear();
                findObjects.Invoke(__instance, new object[] { sector, s_sectorBuffer });
                foreach (var zdo in s_sectorBuffer)
                {
                    if (!zdo.Persistent) continue;
                    if (zdo.GetOwner() == sessionId) continue;
                    // Take ownership. Peers can't steal back because our
                    // IsInPeerActiveArea patch reports the server as in-area
                    // for any sector where any peer is active.
                    zdo.SetOwner(sessionId);
                }
            }
            return false;
        }
    }
}
