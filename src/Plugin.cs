using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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
        // 0.6.0: migration to Valheim build 25185644. The zone/active-area
        // subsystem was refactored (Vector2i→Vector2s zones, m_activeArea →
        // SimulationDistance, private Find*Objects → public FindSectorObjects),
        // so this is a breaking-compat release: it will NOT run on older builds.
        public const string PluginVersion = "0.6.5";

        // Debug knob: when true, emits the periodic [Coverage] lines (sector
        // ownership + per-peer positions) every StatsLogIntervalSeconds.
        // Useful when investigating peer/sector coverage bugs; noise otherwise.
        internal const bool DebugCoverageLog = false;

        // Debug knob: when true, suppresses random raids entirely so they can't
        // interfere with whatever is being tested. Works by setting
        // m_eventChance negative, which no roll can ever pass.
        //
        // (Originally this forced *frequent* raids — hence the old
        // "DebugFastRaids" name — while reproducing the Wake-the-Forest
        // no-spawn bug. It was repurposed to disable them in v0.5.12 and the
        // name/comment were left stale until v0.6.3.)
        //
        // Flip to false and rebuild for the vanilla cadence (~46 min interval,
        // 25% chance, roughly one raid every 3 hours).
        internal const bool DebugDisableRaids = false;
        private const float StatsLogIntervalSeconds = 20f;
        private const float PopulationLogIntervalSeconds = 90f;
        private const float NRESummaryIntervalSeconds = 30f;
        private const float PeerAwareSummaryIntervalSeconds = 60f;
        private const float TerrainCompSnapshotIntervalSeconds = 20f;
        private float _statsTimer;
        private float _populationTimer;
        private float _nreTimer;
        private float _peerAwareTimer;
        private float _terrainCompSnapshotTimer;

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
            var dirtyMarker = BuildInfo.GitDirty ? " (dirty)" : "";
            Log.LogInfo($"Build: git {BuildInfo.GitShortSha}{dirtyMarker} · {BuildInfo.BuildTimestamp} UTC");

            // Session boundary marker. Kept simple and grep-friendly so future
            // sessions can be located with `grep '===\[SESSION' LogOutput.log`.
            // Format: === [SESSION START] <iso local time> · v<version> · git <sha>[<dirty>] ===
            var sessionStamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
            Log.LogMessage(
                $"===[SESSION START]=== {sessionStamp} · v{PluginVersion} · " +
                $"git {BuildInfo.GitShortSha}{dirtyMarker}");
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
            _populationTimer += Time.deltaTime;
            _nreTimer += Time.deltaTime;
            _peerAwareTimer += Time.deltaTime;
            _terrainCompSnapshotTimer += Time.deltaTime;

            if (_statsTimer >= StatsLogIntervalSeconds)
            {
                _statsTimer = 0f;
                if (DebugCoverageLog) LogCoverageStats();
            }
            if (_populationTimer >= PopulationLogIntervalSeconds)
            {
                _populationTimer = 0f;
                LogMobPopulation();
            }
            if (_nreTimer >= NRESummaryIntervalSeconds)
            {
                _nreTimer = 0f;
                FlushSuppressedNRECount();
            }
            if (_peerAwareTimer >= PeerAwareSummaryIntervalSeconds)
            {
                _peerAwareTimer = 0f;
                FlushPeerAwareCounters();
            }
            if (_terrainCompSnapshotTimer >= TerrainCompSnapshotIntervalSeconds)
            {
                _terrainCompSnapshotTimer = 0f;
                LogTerrainCompSnapshot();
            }
        }

        /// <summary>
        /// v0.5.21 diagnostic: every 20s, dump owner + DataRevision for every
        /// TerrainComp ZDO in the server's active sectors. Purpose: catch owner
        /// churn (a TerrainComp bouncing between sessionIDs) or a stale owner
        /// (sessionID that no longer matches any connected peer, which would
        /// make a client's InvokeRPC silently go nowhere).
        /// </summary>
        private static void LogTerrainCompSnapshot()
        {
            if (ZoneSystem.instance == null || ZDOMan.instance == null) return;
            var simDist = ServerCoverage.GetSimulationDistance();
            var buffer = new List<ZDO>();
            var seen = new HashSet<ZDO>();
            int total = 0;
            var sb = new System.Text.StringBuilder("[Diag] TerrainComp snapshot: ");
            foreach (var peerPos in ServerCoverage.GetUniquePeerPositions())
            {
                buffer.Clear();
                ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(peerPos), simDist, buffer);
                foreach (var zdo in buffer)
                {
                    if (ZNetScene.instance == null) continue;
                    if (!seen.Add(zdo)) continue;
                    var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                    if (prefab == null || prefab.GetComponent<TerrainComp>() == null) continue;
                    total++;
                    long owner = zdo.GetOwner();
                    bool ownerLive = owner != 0 && ServerCoverage.IsLivePeer(owner);
                    var pos = zdo.GetPosition();
                    sb.Append($"[{ZoneSystem.GetZone(pos)} owner={owner} live={ownerLive} rev={zdo.DataRevision}] ");
                }
            }
            if (total > 0) Plugin.Log.LogInfo(sb.ToString());
        }

        // Periodic diagnostic flush. Drains counters accumulated by our
        // various patches and emits log lines only when the numbers matter.
        // Named PeerAware for historical reasons — retained to avoid churn;
        // it's just "the diagnostic flush timer" now.
        private static void FlushPeerAwareCounters()
        {
            // Drain ItemDrop reclaim counters (split by prevOwner class).
            int idUnowned = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_itemDropReclaimUnowned, 0);
            int idLive = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_itemDropReclaimLivePeer, 0);
            int idAbsent = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_itemDropReclaimAbsentPeer, 0);
            if (idLive > 0)
            {
                var lastName = ZDOMan_ReleaseZDOS_Patch.s_lastLivePeerReclaimName ?? "?";
                var lastPrev = ZDOMan_ReleaseZDOS_Patch.s_lastLivePeerReclaimPrevOwner;
                Log.LogWarning(
                    $"[Diag] ItemDrop RACE reclaims (livePeer→server) in last {PeerAwareSummaryIntervalSeconds:F0}s: " +
                    $"{idLive} (last: {lastName}, prevOwner={lastPrev}). " +
                    $"Also this window: unowned→server={idUnowned}, absentPeer→server={idAbsent}. " +
                    "Each livePeer reclaim is a candidate 'pickup silently failed' event.");
            }
            else if (idUnowned + idAbsent > 0)
            {
                Log.LogInfo(
                    $"[Diag] ItemDrop reclaims (no race in this window): " +
                    $"unowned→server={idUnowned}, absentPeer→server={idAbsent}.");
            }

            // Drain Container reclaim counters (same three-bucket split).
            int cUnowned = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimUnowned, 0);
            int cLive = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimLivePeer, 0);
            int cAbsent = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimAbsentPeer, 0);
            if (cLive > 0)
            {
                var lastName = ZDOMan_ReleaseZDOS_Patch.s_lastContainerLivePeerReclaimName ?? "?";
                var lastPrev = ZDOMan_ReleaseZDOS_Patch.s_lastContainerLivePeerReclaimPrevOwner;
                Log.LogWarning(
                    $"[Diag] Container RACE reclaims (livePeer→server) in last {PeerAwareSummaryIntervalSeconds:F0}s: " +
                    $"{cLive} (last: {lastName}, prevOwner={lastPrev}). " +
                    $"Also this window: unowned→server={cUnowned}, absentPeer→server={cAbsent}. " +
                    "Each livePeer reclaim is a candidate 'stuck chest' cause.");
            }
            else if (cUnowned + cAbsent > 0)
            {
                Log.LogInfo(
                    $"[Diag] Container reclaims (no race in this window): " +
                    $"unowned→server={cUnowned}, absentPeer→server={cAbsent}.");
            }

            // Drain SpawnSystem invocation counters.
            int biomeInv = System.Threading.Interlocked.Exchange(
                ref SpawnDiag.s_biomeSpawnInvocations, 0);
            int eventInv = System.Threading.Interlocked.Exchange(
                ref SpawnDiag.s_eventSpawnInvocations, 0);
            // Only log if event branch fired (i.e. an event is/was active).
            // If eventInv > 0 while player reports "no event mobs", the event
            // spawn path IS being reached — bug is inside UpdateSpawnList's
            // biome/geometry rejection (or spawner config), not our prefix.
            // If eventInv == 0 while an event is active, RandEventSystem's
            // GetCurrentSpawners is returning null — investigate that side.
            if (eventInv > 0)
            {
                Log.LogInfo(
                    $"[Diag] SpawnSystem invocations in last {PeerAwareSummaryIntervalSeconds:F0}s: " +
                    $"biome={biomeInv} event={eventInv}.");
            }
        }

        private static void FlushSuppressedNRECount()
        {
            int caught = System.Threading.Interlocked.Exchange(
                ref SpawnSystem_UpdateSpawning_Patch.s_suppressedNRECount, 0);
            if (caught > 0)
            {
                Log.LogWarning($"[SpawnSystem] Suppressed {caught} NRE(s) from vanilla UpdateSpawnList " +
                    $"in last {NRESummaryIntervalSeconds:F0}s (likely a race we haven't covered yet — investigate if sustained).");
            }
        }

        private static void LogMobPopulation()
        {
            var characters = Character.GetAllCharacters();
            if (characters == null || characters.Count == 0)
            {
                Log.LogInfo("[Population] No characters active.");
                return;
            }

            var counts = new Dictionary<string, int>();
            int playerCount = 0;
            foreach (var c in characters)
            {
                if (c == null) continue;
                if (c is Player) { playerCount++; continue; }
                var name = c.gameObject.name;
                int i = name.IndexOf("(Clone)");
                if (i > 0) name = name.Substring(0, i);
                counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
            }

            int mobTotal = counts.Values.Sum();
            var sb = new System.Text.StringBuilder(
                $"[Population] {mobTotal} mob(s) across {counts.Count} type(s)");
            if (playerCount > 0) sb.Append($" (+{playerCount} player(s))");
            if (mobTotal > 0)
            {
                sb.Append(": ");
                bool first = true;
                foreach (var kv in counts.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
                {
                    if (!first) sb.Append(", ");
                    first = false;
                    sb.Append(kv.Key).Append('×').Append(kv.Value);
                }
            }
            Log.LogInfo(sb.ToString());
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

            var sectors = ServerCoverage.GetActiveSectors(ServerCoverage.GetSimulationDistance());

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

            // Log each peer's position AND active-area box. Each ±m_activeArea
            // box is what the server treats as an independent reference for
            // gameplay control (owning ZDOs, running physics, allowing spawns
            // near that peer). NOT related to the vanilla centroid — server
            // does not gate on a single centroid box any more (see
            // ZoneSystem_IsActiveAreaLoaded_Patch and the two
            // ZNetScene_OutsideActiveArea_*_Patches).
            var sb = new System.Text.StringBuilder("[Coverage] Peer positions: ");
            int near = ServerCoverage.GetSimulationDistance().NearSimulationDistance;
            bool first = true;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                if (!first) sb.Append(", ");
                first = false;
                var p = peer.GetRefPos();
                var sector = ZoneSystem.GetZone(p);
                sb.Append(
                    $"world({p.x:F0},{p.z:F0}) sector({sector.x},{sector.y}) " +
                    $"near-box[{sector.x - near}..{sector.x + near}, " +
                    $"{sector.y - near}..{sector.y + near}]");
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

        // NOTE (build 25185644 migration): ZDOMan.FindObjects/FindDistantObjects
        // are now private AND take an extra `HashSet<ZoneSystem.SectorIndex>
        // visitedSectorIndices` argument, so the old per-sector reflection calls
        // would fail at runtime. Vanilla now exposes a public
        // `FindSectorObjects(Vector2s, SimulationDistance, near, distant)` that
        // performs the whole near+distant sector walk (including the new
        // radius-vs-classic zone selection) for a given center zone. We call
        // that once per peer and merge, which is both simpler and keeps the
        // near/distant split correct — the distant ring still only yields
        // Distant-flagged ZDOs, so Character ZDOs can't bypass terrain gating.
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
        private static readonly HashSet<Vector2s> s_activeSectors = new HashSet<Vector2s>();
        private static readonly List<Vector3> s_uniquePeerPositions = new List<Vector3>();
        private static readonly HashSet<Vector2s> s_seenPeerSectors = new HashSet<Vector2s>();

        /// <summary>
        /// The simulation distance the server is running with. Replaces the
        /// pre-25185644 `ZoneSystem.m_activeArea` / `m_activeDistantArea` int
        /// pair, which no longer exist.
        /// </summary>
        public static SimulationDistance GetSimulationDistance()
        {
            return ZNet.instance != null
                ? ZNet.instance.GetSyncedSimulationDistance()
                : SimulationDistance.OriginalDistance;
        }

        /// <summary>
        /// All sectors that fall within any peer's near simulation area.
        ///
        /// Build 25185644 changed the shape of "active area" from a plain
        /// Chebyshev box to a radius test (`ZonesWithinRadius`), except when
        /// `IsClassic` is set — in which case the old square box applies. We
        /// mirror vanilla's own selection here so our coverage set matches what
        /// vanilla systems expect, just unioned across every peer instead of
        /// taken around a single reference position.
        /// </summary>
        public static HashSet<Vector2s> GetActiveSectors(SimulationDistance simDist)
        {
            s_activeSectors.Clear();
            if (ZNet.instance == null || ZoneSystem.instance == null) return s_activeSectors;
            int near = simDist.NearSimulationDistance;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var center = ZoneSystem.GetZone(peer.GetRefPos());
                for (int y = -near; y <= near; y++)
                {
                    for (int x = -near; x <= near; x++)
                    {
                        var s = new Vector2s(center.x + x, center.y + y);
                        if (simDist.IsClassic ||
                            ZoneSystem.instance.ZonesWithinRadius(center, s, near))
                        {
                            s_activeSectors.Add(s);
                        }
                    }
                }
            }
            return s_activeSectors;
        }

        // NOTE: there is deliberately no GetDistantSectors here any more.
        // ZDOMan.FindSectorObjects walks the distant ring itself (and keeps it
        // restricted to Distant-flagged ZDOs), so the callers that used to need
        // a separate distant-sector set now get it for free.

        /// <summary>
        /// True if `point` falls inside any connected peer's active box —
        /// equivalent to vanilla's `!ZNetScene.OutsideActiveArea(point)` but
        /// computed against every peer's own reference position instead of
        /// the centroid. This is the "gameplay control area" for the server:
        /// wherever any peer is, the server treats itself as being in-area
        /// for owning ZDOs, running physics, allowing spawns, etc.
        /// </summary>
        public static bool IsPointInsideAnyPeerActiveArea(Vector3 point)
        {
            if (ZNet.instance == null) return false;
            var peers = ZNet.instance.GetPeers();
            if (peers == null || peers.Count == 0) return false;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                // (Vector3 point, Vector3 centerPosition) overload — vanilla
                // resolves the center zone and runs the radius test itself.
                if (ZNetScene.InActiveArea(point, peer.GetRefPos())) return true;
            }
            return false;
        }

        /// <summary>
        /// True if the given session ID belongs to a currently-connected peer.
        /// The server's own session ID is never in ZNet.GetPeers() and returns
        /// false here — callers must handle the "owned by us" case separately
        /// (typically with an early `zdo.GetOwner() == sessionId` filter).
        /// </summary>
        public static bool IsLivePeer(long uid)
        {
            if (ZNet.instance == null) return false;
            var peers = ZNet.instance.GetPeers();
            if (peers == null) return false;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                if (peer.m_uid == uid) return true;
            }
            return false;
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

    // REMOVED in build-25185644 migration: ZDOMan_IsInPeerActiveArea_Patch.
    //
    // It was inert long before this update. `IsInPeerActiveArea` is private with
    // exactly one caller (`ReleaseNearbyZDOS`), which is only reached from
    // vanilla `ZDOMan.ReleaseZDOS` — and ZDOMan_ReleaseZDOS_Patch replaces that
    // method wholesale on the server, so the patched method was never invoked.
    // Its stated purpose ("stop peers stealing back server-owned ZDOs") is
    // actually served by vanilla itself: ZDOMan.Update only calls ReleaseZDOS
    // when IsServer(), so clients never run reclaim logic at all.
    //
    // The update renamed its first parameter (Vector2i sector → Vector3 point),
    // which made Harmony fail to bind by name and throw out of PatchAll —
    // aborting every subsequent patch and disabling the whole plugin.

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
        // SpawnSystem.Awake caches the Heightmap once; for Client-mode zones
        // recreated from a persisted ZoneCtrl ZDO, the ZoneCtrl can be
        // instantiated by ZNetScene BEFORE ZoneSystem.Update pokes the local
        // zone and instantiates the Heightmap. That leaves m_heightmap null
        // and every UpdateSpawnList call NREs. We heal it below.
        private static readonly FieldInfo HeightmapField =
            AccessTools.Field(typeof(SpawnSystem), "m_heightmap");

        // Drained on a timer by Plugin.FlushSuppressedNRECount so we never
        // silently bury vanilla NREs — one summary line per interval.
        internal static int s_suppressedNRECount;

        static bool Prefix(SpawnSystem __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            if (Player.m_localPlayer != null) return true;

            var nview = (ZNetView)NViewField.GetValue(__instance);
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return false;

            // Race-heal: if the Heightmap wasn't found at Awake time, retry now.
            // Bails cleanly (returns next tick) if it still isn't there.
            var heightmap = (Heightmap)HeightmapField.GetValue(__instance);
            if (heightmap == null)
            {
                heightmap = Heightmap.FindHeightmap(__instance.transform.position);
                if (heightmap == null) return false;
                HeightmapField.SetValue(__instance, heightmap);
            }

            var tempNearPlayers = (List<Player>)TempNearPlayersField.GetValue(null);
            tempNearPlayers.Clear();
            GetPlayersInZoneMethod.Invoke(__instance, new object[] { tempNearPlayers });
            if (tempNearPlayers.Count == 0) return false;

            DateTime time = ZNet.instance.GetTime();
            var spawnLists = (List<SpawnSystemList>)SpawnListsField.GetValue(__instance);
            foreach (var spawnList in spawnLists)
            {
                System.Threading.Interlocked.Increment(ref SpawnDiag.s_biomeSpawnInvocations);
                InvokeUpdateSpawnList(__instance, spawnList.m_spawners, time, eventSpawners: false, "b_");
            }

            var currentSpawners = RandEventSystem.instance?.GetCurrentSpawners();
            if (currentSpawners != null)
            {
                System.Threading.Interlocked.Increment(ref SpawnDiag.s_eventSpawnInvocations);
                InvokeUpdateSpawnList(__instance, currentSpawners, time, eventSpawners: true, "e_");
            }

            // Alt-biome spawners — added in build 25185644. Vanilla walks the
            // heightmap's corner alt-biomes and runs their spawn lists with a
            // per-index group salt. Omitting this would silently drop a whole
            // category of spawns on our server only.
            var altBiomes = heightmap.m_cornerAltBiomes;
            if (altBiomes != null)
            {
                for (int i = 0; i < altBiomes.Count; i++)
                {
                    var altBiome = altBiomes[i];
                    if (altBiome == null || altBiome.m_spawn.Count == 0) continue;
                    System.Threading.Interlocked.Increment(ref SpawnDiag.s_biomeSpawnInvocations);
                    InvokeUpdateSpawnList(__instance, altBiome.m_spawn, time, eventSpawners: false, $"m{i}");
                }
            }
            return false;
        }

        // Narrowly catches vanilla NREs so one bad iteration doesn't abort
        // the whole tick. Only TargetInvocationException wrapping NRE — real
        // errors (arg mismatch, logic bugs) still propagate.
        //
        // `groupSalt` was added to UpdateSpawnList in build 25185644; vanilla
        // passes "b_" for biome spawners, "e_" for event spawners, and $"m{i}"
        // for the i-th alt biome.
        private static void InvokeUpdateSpawnList(
            SpawnSystem instance, List<SpawnSystem.SpawnData> spawners, DateTime time,
            bool eventSpawners, string groupSalt)
        {
            try
            {
                UpdateSpawnListMethod.Invoke(
                    instance, new object[] { spawners, time, eventSpawners, groupSalt });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException)
            {
                System.Threading.Interlocked.Increment(ref s_suppressedNRECount);
            }
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

            var activeSectors = ServerCoverage.GetActiveSectors(ServerCoverage.GetSimulationDistance());

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
        private static readonly List<ZDO> s_nearBuffer = new List<ZDO>();
        private static readonly List<ZDO> s_distantBuffer = new List<ZDO>();
        private static readonly HashSet<ZDO> s_seenNear = new HashSet<ZDO>();
        private static readonly HashSet<ZDO> s_seenDistant = new HashSet<ZDO>();

        static bool Prefix(ZNetScene __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            var currentObjects = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentObjects.GetValue(__instance);
            var distantObjects = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentDistantObjects.GetValue(__instance);
            currentObjects.Clear();
            distantObjects.Clear();
            s_seenNear.Clear();
            s_seenDistant.Clear();

            var simDist = ServerCoverage.GetSimulationDistance();
            var zdoMan = ZDOMan.instance;

            // Run vanilla's own near+distant sector walk once per peer zone and
            // merge. FindSectorObjects handles the radius-vs-classic selection
            // and keeps the distant ring restricted to Distant-flagged ZDOs, so
            // Character ZDOs still can't reach the distant list and bypass
            // terrain gating (the v0.4.1 mid-air-spawn bug). Peers overlap, so
            // dedup on the way in.
            foreach (var pos in ServerCoverage.GetUniquePeerPositions())
            {
                s_nearBuffer.Clear();
                s_distantBuffer.Clear();
                zdoMan.FindSectorObjects(ZoneSystem.GetZone(pos), simDist, s_nearBuffer, s_distantBuffer);
                foreach (var zdo in s_nearBuffer)
                    if (s_seenNear.Add(zdo)) currentObjects.Add(zdo);
                foreach (var zdo in s_distantBuffer)
                    if (s_seenDistant.Add(zdo)) distantObjects.Add(zdo);
            }

            ReflectionCache.ZNetScene_CreateObjects.Invoke(__instance, new object[] { currentObjects, distantObjects });
            ReflectionCache.ZNetScene_RemoveObjects.Invoke(__instance, new object[] { currentObjects, distantObjects });
            return false;
        }
    }

    /// <summary>
    /// v0.6.4: ships with a player aboard are owned by that player, not the
    /// server — restoring vanilla's design.
    ///
    /// Vanilla never simulates a ship on the server. Ship.UpdateOwner exists
    /// specifically to hand ownership to a player aboard, but it's gated
    /// behind `Player.m_localPlayer != null`, so it can't run on a dedicated
    /// server ("Pattern C", see [[valheim-local-player-concept]]). Combined
    /// with our ReleaseZDOS claiming everything, the server ended up
    /// simulating ships with players standing on them.
    ///
    /// That breaks because the server's copy of the player is a stand-in
    /// positioned from network updates, while the deck under it is simulated
    /// locally. At sail speeds the gap between the two grows with
    /// speed × latency, and a sharp speed change teleports the stand-in into
    /// the deck; depenetration shoves the hull downward and can register as
    /// impact damage. v0.6.2 diagnostics confirmed water was always found and
    /// the server always saw the player aboard, while the hull still dropped
    /// 4–5m (vy ≈ −9 m/s) on Half/Full ↔ Slow transitions. The server's wind
    /// also ignores Moder's power (that branch in EnvMan.UpdateWind needs a
    /// local player), so its sail force can disagree with the client's too.
    ///
    /// Ownership rules, in order:
    ///  1. Sticky: a live peer that already owns the ship and is still within
    ///     KeepRadius keeps it. The server's aboard list comes from physics
    ///     triggers against those network-positioned stand-ins, so it can
    ///     flicker; this stops ownership thrashing on a blip. It also matches
    ///     vanilla, which only moves ownership when the owner leaves the boat.
    ///  2. The player at the helm (ship ZDO's s_user, set by ShipControlls).
    ///  3. Any player aboard — vanilla's GetNewOwnerID picks the first.
    ///  4. Nobody aboard → return false; the caller applies the normal server
    ///     claim, so parked boats stay server-simulated.
    /// </summary>
    internal static class ShipOwnership
    {
        private static readonly FieldInfo s_playersField = AccessTools.Field(typeof(Ship), "m_players");

        // A Karve is ~10m long and a longship ~20m; players aboard stay well
        // inside this. Generous on purpose — erring toward "keep" is harmless,
        // since vanilla's own client-side UpdateOwner still hands the ship on
        // as soon as the owner's player is no longer in the boat.
        private const float KeepRadius = 15f;

        /// <summary>
        /// True if this ZDO is a ship that should be owned by a peer (ownership
        /// is set if needed). False means "not a ship" or "nobody aboard" — the
        /// caller should apply its normal rules.
        /// </summary>
        public static bool TryAssignToPeer(ZDO zdo)
        {
            var scene = ZNetScene.instance;
            if (scene == null || ZNet.instance == null) return false;
            var prefab = scene.GetPrefab(zdo.GetPrefab());
            if (prefab == null || prefab.GetComponent<Ship>() == null) return false;

            long desired = PickPeerOwner(zdo, scene);
            if (desired == 0) return false;

            long prev = zdo.GetOwner();
            if (prev != desired)
            {
                zdo.SetOwner(desired);
                var pos = zdo.GetPosition();
                Plugin.Log.LogInfo(
                    $"[Diag] Ship ownership → peer {desired} (was {prev}) " +
                    $"prefab={prefab.name} pos=world({pos.x:F0},{pos.z:F0})");
            }
            return true;
        }

        private static long PickPeerOwner(ZDO zdo, ZNetScene scene)
        {
            var shipPos = zdo.GetPosition();
            long current = zdo.GetOwner();

            if (current != 0)
            {
                var currentPeer = ZNet.instance.GetPeer(current);
                if (currentPeer != null && Utils.DistanceXZ(currentPeer.m_refPos, shipPos) <= KeepRadius)
                    return current;
            }

            var go = scene.FindInstance(zdo.m_uid);
            var ship = go != null ? go.GetComponent<Ship>() : null;
            var players = ship != null ? (List<Player>)s_playersField.GetValue(ship) : null;
            if (players == null || players.Count == 0) return 0;

            long helmPlayerId = zdo.GetLong(ZDOVars.s_user, 0L);
            if (helmPlayerId != 0)
            {
                foreach (var p in players)
                {
                    if (p != null && p.GetPlayerID() == helmPlayerId && ServerCoverage.IsLivePeer(p.GetOwner()))
                        return p.GetOwner();
                }
            }

            foreach (var p in players)
            {
                if (p != null && ServerCoverage.IsLivePeer(p.GetOwner()))
                    return p.GetOwner();
            }
            return 0;
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
        private static readonly HashSet<ZDO> s_seenZdos = new HashSet<ZDO>();

        // Reclaim diagnostic counters, split by prevOwner class:
        //  - unowned→server: healthy path (freshly spawned, no player ever
        //    claimed it). Very high volume; noise.
        //  - livePeer→server: the actual race case — a peer briefly owned it
        //    and we clawed it back. Each is a candidate silent interaction
        //    failure. Log prominently.
        //  - absentPeer→server: peer that owned it is no longer connected —
        //    the normal cleanup case.
        // Split by prefab kind so ItemDrops and Containers get counted apart
        // (Container reclaims are the smoking gun for stuck-chest issues;
        // ItemDrop reclaims are the smoking gun for pickup failures).
        internal static int s_itemDropReclaimUnowned;
        internal static int s_itemDropReclaimLivePeer;
        internal static int s_itemDropReclaimAbsentPeer;
        internal static string s_lastLivePeerReclaimName;
        internal static long s_lastLivePeerReclaimPrevOwner;

        internal static int s_containerReclaimUnowned;
        internal static int s_containerReclaimLivePeer;
        internal static int s_containerReclaimAbsentPeer;
        internal static string s_lastContainerLivePeerReclaimName;
        internal static long s_lastContainerLivePeerReclaimPrevOwner;

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
            var simDist = ServerCoverage.GetSimulationDistance();

            // Near-ring ZDOs around every peer, deduped. We pass no distant list
            // so FindSectorObjects folds the distant ring into the same buffer —
            // harmless here since ownership claiming doesn't care about the
            // near/distant split, only about which ZDOs are in reach of a peer.
            s_seenZdos.Clear();
            foreach (var pos in ServerCoverage.GetUniquePeerPositions())
            {
                s_sectorBuffer.Clear();
                __instance.FindSectorObjects(ZoneSystem.GetZone(pos), simDist, s_sectorBuffer);
                foreach (var zdo in s_sectorBuffer)
                {
                    if (!s_seenZdos.Add(zdo)) continue;
                    if (!zdo.Persistent) continue;
                    // Ships a player is aboard belong to that player, as in
                    // vanilla. Must run before the "server already owns it"
                    // skip below, or a parked (server-owned) boat would never
                    // be handed over when someone boards it.
                    if (ShipOwnership.TryAssignToPeer(zdo)) continue;
                    if (zdo.GetOwner() == sessionId) continue;
                    // Skip ZDOs a peer is actively using (Container/wagon/etc.
                    // in-use flag). Vanilla hands ownership to the interacting
                    // player for the duration of an open; reclaiming it mid-use
                    // breaks the container UI (InventoryGui.UpdateContainer
                    // hides the grid when IsOwner() flips false) and leaves the
                    // chest stuck in its "open" animation. Container_RPC_*_Patch
                    // sets this flag atomically with the ownership grant so this
                    // check is race-free.
                    //
                    // But only respect the flag if the owner is actually a live
                    // peer — otherwise a crash / hard disconnect / server
                    // shutdown mid-open leaves s_inUse=1 with an absent owner
                    // forever, and the container is unopenable. Heal by
                    // clearing the flag and reclaiming below.
                    if (zdo.GetInt(ZDOVars.s_inUse, 0) != 0)
                    {
                        if (ServerCoverage.IsLivePeer(zdo.GetOwner())) continue;
                        zdo.Set(ZDOVars.s_inUse, 0);
                    }
                    // v0.5.22: TerrainComp ZDOs get vanilla peer-ownership
                    // instead of server ownership. Vanilla's own ReleaseZDOS
                    // (server-only, which we otherwise fully replace) does two
                    // things: claims ZDOs near the server's own ref position,
                    // AND assigns ZDOs near each connected peer to THAT peer.
                    // We only replicate the first half for most ZDOs (by
                    // design — the server should own mob/container/item sim).
                    // But TerrainComp.RPC_ApplyOperation gates on IsOwner(),
                    // and a ZDO with owner=0 can never satisfy that check on
                    // ANY machine (ZDO.SetOwnerInternal hardcodes Owner=false
                    // for uid==0). v0.5.20 tried to leave TerrainComp
                    // untouched hoping vanilla would claim it — but vanilla's
                    // peer-assignment logic only exists inside the very method
                    // we're overriding, so nothing ever claimed it and every
                    // dig silently no-op'd everywhere. Fix: replicate vanilla's
                    // peer-assignment specifically for TerrainComp, giving
                    // ownership to whichever connected peer is in-area. That
                    // peer's own TerrainComp instance applies the dig locally
                    // and Saves to the ZDO; our existing heal (v0.5.16) +
                    // CheckLoad guard (v0.5.19) make sure the server's own
                    // instance picks up the resulting diff safely even if it
                    // lost the Heightmap-Awake race.
                    if (ZNetScene.instance != null)
                    {
                        var prefabForSkip = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                        if (prefabForSkip != null && prefabForSkip.GetComponent<TerrainComp>() != null)
                        {
                            var zdoPos = zdo.GetPosition();
                            long assignTo = 0;
                            foreach (var peer in ZNet.instance.GetPeers())
                            {
                                if (peer == null) continue;
                                if (ZNetScene.InActiveArea(zdoPos, peer.m_refPos))
                                {
                                    assignTo = peer.m_uid;
                                    break;
                                }
                            }
                            if (assignTo != 0 && zdo.GetOwner() != assignTo)
                                zdo.SetOwner(assignTo);
                            continue;
                        }
                    }

                    // Take ownership. Peers can't steal it back because
                    // vanilla only runs ZDO reclaim logic on the server
                    // (ZDOMan.Update calls ReleaseZDOS only when IsServer()),
                    // and this patch replaces that logic wholesale.
                    long prevOwner = zdo.GetOwner();
                    zdo.SetOwner(sessionId);

                    // Diagnostic: classify reclaims by prevOwner and by prefab
                    // kind. ItemDrops → pickup-race diagnostic. Containers →
                    // stuck-chest diagnostic. See counter declarations above.
                    var prefabHash = zdo.GetPrefab();
                    if (ZNetScene.instance != null)
                    {
                        var prefab = ZNetScene.instance.GetPrefab(prefabHash);
                        if (prefab != null)
                        {
                            bool isItemDrop = prefab.GetComponent<ItemDrop>() != null;
                            bool isContainer = !isItemDrop && prefab.GetComponent<Container>() != null;
                            if (isItemDrop)
                            {
                                if (prevOwner == 0)
                                    System.Threading.Interlocked.Increment(ref s_itemDropReclaimUnowned);
                                else if (ServerCoverage.IsLivePeer(prevOwner))
                                {
                                    System.Threading.Interlocked.Increment(ref s_itemDropReclaimLivePeer);
                                    s_lastLivePeerReclaimName = prefab.name;
                                    s_lastLivePeerReclaimPrevOwner = prevOwner;
                                }
                                else
                                    System.Threading.Interlocked.Increment(ref s_itemDropReclaimAbsentPeer);
                            }
                            else if (isContainer)
                            {
                                if (prevOwner == 0)
                                    System.Threading.Interlocked.Increment(ref s_containerReclaimUnowned);
                                else if (ServerCoverage.IsLivePeer(prevOwner))
                                {
                                    System.Threading.Interlocked.Increment(ref s_containerReclaimLivePeer);
                                    s_lastContainerLivePeerReclaimName = prefab.name;
                                    s_lastContainerLivePeerReclaimPrevOwner = prevOwner;
                                }
                                else
                                    System.Threading.Interlocked.Increment(ref s_containerReclaimAbsentPeer);
                            }
                        }
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Vanilla's `ZNetScene.OutsideActiveArea(Vector3 point)` calls
    /// `GetReferencePosition()` = centroid. Used by SpawnArea (spawn-trigger
    /// gate on nests etc.) and WearNTear (skip structural-wear when outside
    /// active area). With peers spread apart, the centroid falls outside
    /// every peer's box, so SpawnArea near an actual peer thinks it's outside
    /// the active area and suppresses spawns; WearNTear stops running.
    ///
    /// Fix: check against every peer's active area independently. Peer-aware
    /// semantics, not centroid-based.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea", new[] { typeof(Vector3) })]
    internal static class ZNetScene_OutsideActiveArea_Instance_Patch
    {
        static bool Prefix(Vector3 point, ref bool __result)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            __result = !ServerCoverage.IsPointInsideAnyPeerActiveArea(point);
            return false;
        }
    }

    /// <summary>
    /// Static `OutsideActiveArea(Vector3 point, Vector2s centerZone)`. Used by
    /// StaticPhysics-style callers that pass a caller-computed center zone —
    /// on the server that center derives from `GetReferencePosition()`, i.e.
    /// the peer centroid, so falling objects near a peer but far from the
    /// centroid never run their "should I fall?" check. Ignore the supplied
    /// centerZone and answer per-peer instead.
    ///
    /// (Build 25185644 replaced the old 3-arg
    /// `(Vector3, Vector2i, int activeArea)` overload with this 2-arg
    /// `(Vector3, Vector2s)` form — the explicit activeArea int is gone now
    /// that simulation distance is read from ZNet.)
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "OutsideActiveArea",
                  new[] { typeof(UnityEngine.Vector3), typeof(Vector2s) })]
    internal static class ZNetScene_OutsideActiveArea_Static_Patch
    {
        static bool Prefix(Vector3 point, ref bool __result)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            // centerZone from the caller is ignored on server — see summary.
            __result = !ServerCoverage.IsPointInsideAnyPeerActiveArea(point);
            return false;
        }
    }

    /// <summary>
    /// Vanilla ZNetScene.CreateObjectsSorted early-exits if
    /// IsActiveAreaLoaded() returns false, which checks the ±m_activeArea box
    /// around ZNet.GetReferencePosition() — which our plugin makes the
    /// centroid of connected peers. With peers spread apart, that centroid
    /// falls in an unloaded sector, the gate fails, and the server never
    /// instantiates GameObjects for near-list ZDOs. All interaction RPCs
    /// (damage/pickup/chest/rename) then silently miss their targets on the
    /// server — from the client the world looks fine, but nothing responds.
    ///
    /// Fix: on the server, always report the active area as loaded. The
    /// per-ZDO IsZoneReadyForType gate at CreateObjectsSorted:215 already
    /// handles individual "terrain not yet built here" cases correctly, so
    /// this top-level gate adds no useful safety for us and only ever fires
    /// as a false positive under our per-peer coverage model.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "IsActiveAreaLoaded")]
    internal static class ZoneSystem_IsActiveAreaLoaded_Patch
    {
        static bool Prefix(ref bool __result)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// Close the open-time race between vanilla's Container.RPC_RequestOpen
    /// success branch (SetOwner(uid) + OpenRespons(true)) and the client
    /// eventually setting m_inUse=1 via InventoryGui.UpdateContainer next
    /// frame. Without this postfix, our ReleaseZDOS reclaim can fire in the
    /// ~50-200ms window and steal ownership back before the client marks the
    /// container in-use, which breaks the container UI and leaves the chest
    /// stuck in its open animation.
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
    internal static class Container_RPC_RequestOpen_Patch
    {
        static void Postfix(Container __instance, long uid)
        {
            ContainerRPCHelper.MarkInUseIfGranted(__instance, uid);
        }
    }

    /// <summary>
    /// Same race exists on the "stack all" RPC — it also does ForceSendZDO +
    /// SetOwner(uid) in its success branch.
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_RequestStack")]
    internal static class Container_RPC_RequestStack_Patch
    {
        static void Postfix(Container __instance, long uid)
        {
            ContainerRPCHelper.MarkInUseIfGranted(__instance, uid);
        }
    }

    internal static class ContainerRPCHelper
    {
        // Only marks in-use on the ZDO if the success branch of the RPC ran —
        // detected by ownership having just been transferred to the requester.
        // Server-side only.
        public static void MarkInUseIfGranted(Container container, long uid)
        {
            // v0.5.2: `IsServer` gate removed. Vanilla's `RPC_RequestOpen`
            // success branch runs SetOwner(uid) on WHICHEVER machine handles
            // the RPC (server on first open, but the client itself on rapid
            // reopens where ownership never actually left it). If we only
            // marked `s_inUse=1` on the server side, the client-owned reopen
            // path left a ~one-frame gap where the ZDO had `s_inUse=0` and
            // ReleaseZDOS could reclaim mid-cycle — stuck-open chest.
            // `zdo.Set` on a non-owner queues to the change-buffer and reaches
            // the owner shortly; on the owner it's authoritative immediately.
            // Either way, the flag is set atomically with the success branch.
            if (container == null) return;
            var nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            if (zdo.GetOwner() != uid) return;
            zdo.Set(ZDOVars.s_inUse, 1);
        }
    }

    /// <summary>
    /// v0.5.3 diagnostic: dump per-open state right before vanilla's
    /// `Container.RPC_RequestOpen` runs its branch decision. Paired with
    /// vanilla's own success/failure branch logs, gives an exact per-open
    /// ownership timeline. Format:
    ///   [Diag] RequestOpen chest=<name> requester=<uid> prevOwner=<owner> s_inUse=<0/1> imOwner=<t/f> imUid=<sessionId>
    /// Server-side only — client-side handling is uninteresting for the
    /// race we're chasing.
    /// </summary>
    [HarmonyPatch(typeof(Container), "RPC_RequestOpen")]
    internal static class Container_RPC_RequestOpen_Diag_Patch
    {
        static void Prefix(Container __instance, long uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__instance == null) return;
            var nview = __instance.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            long prevOwner = zdo.GetOwner();
            int sInUse = zdo.GetInt(ZDOVars.s_inUse, 0);
            bool imOwner = nview.IsOwner();
            long imUid = ZDOMan.GetSessionID();
            Plugin.Log.LogInfo(
                $"[Diag] RequestOpen chest={__instance.gameObject.name} " +
                $"requester={uid} prevOwner={prevOwner} s_inUse={sInUse} " +
                $"imOwner={imOwner} imUid={imUid}");
        }
    }

    /// <summary>
    /// v0.5.3 diagnostic: log once when a random event starts so we can
    /// correlate QA "wake the forest but nothing spawned" reports with
    /// the event target position, spawner-list size, and whether our
    /// SpawnSystem prefix is even seeing event spawners.
    /// </summary>
    [HarmonyPatch(typeof(RandEventSystem), "SetRandomEvent")]
    internal static class RandEventSystem_SetRandomEvent_Diag_Patch
    {
        static void Postfix(RandomEvent ev, Vector3 pos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ev == null)
            {
                Plugin.Log.LogInfo("[Diag] Random event cleared.");
                return;
            }
            int spawnerCount = ev.m_spawn?.Count ?? 0;
            Plugin.Log.LogInfo(
                $"[Diag] Random event started: name={ev.m_name} target=world({pos.x:F0},{pos.z:F0}) " +
                $"targetSector={ZoneSystem.GetZone(pos)} spawnerCount={spawnerCount} " +
                $"spawnerDelay={ev.m_spawnerDelay}s duration={ev.m_duration}s.");
        }
    }

    /// <summary>
    /// v0.5.3 diagnostic hooks inside our SpawnSystem prefix — count how
    /// often the event-spawner branch actually runs versus the biome-spawn
    /// branch. Drained on the peer-aware timer alongside the other diags.
    /// If event fires but count stays zero, our prefix never sees non-null
    /// `GetCurrentSpawners` — points at vanilla RandEventSystem-side issue.
    /// If count is high but no mobs appear, points at biome/geometry
    /// rejection inside vanilla `UpdateSpawnList`.
    /// </summary>
    internal static class SpawnDiag
    {
        internal static int s_biomeSpawnInvocations;
        internal static int s_eventSpawnInvocations;
    }

    /// <summary>
    /// v0.5.6: fix a latent vanilla bug that manifests only on dedicated servers.
    /// RandEventSystem.FixedUpdate only calls SetActiveEvent(m_randomEvent) inside
    /// an `if (Player.m_localPlayer)` branch. On a dedicated server there is no
    /// local player, so m_activeEvent stays null, GetCurrentSpawners() returns
    /// null, and the event's spawner list never reaches SpawnSystem — the raid
    /// starts, plays music, and spawns nothing. We reproduce the intent using
    /// connected peers instead: after vanilla's FixedUpdate runs, if a random
    /// event is set and any peer is inside the event area, promote it to
    /// m_activeEvent. If no peer is in the area, clear it. Forced-event branch
    /// is left alone since vanilla already handles that path unconditionally.
    /// </summary>
    [HarmonyPatch(typeof(RandEventSystem), "FixedUpdate")]
    internal static class RandEventSystem_FixedUpdate_ServerActiveEvent_Patch
    {
        private static readonly MethodInfo s_setActiveEvent = AccessTools.Method(
            typeof(RandEventSystem), "SetActiveEvent",
            new[] { typeof(RandomEvent), typeof(bool) });
        private static readonly MethodInfo s_isAnyPlayerInEventArea = AccessTools.Method(
            typeof(RandEventSystem), "IsAnyPlayerInEventArea",
            new[] { typeof(RandomEvent) });
        private static readonly FieldInfo s_randomEventField = AccessTools.Field(
            typeof(RandEventSystem), "m_randomEvent");
        private static readonly FieldInfo s_forcedEventField = AccessTools.Field(
            typeof(RandEventSystem), "m_forcedEvent");
        private static readonly FieldInfo s_activeEventField = AccessTools.Field(
            typeof(RandEventSystem), "m_activeEvent");

        static void Postfix(RandEventSystem __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Forced events are handled by vanilla and short-circuit ahead of us.
            if (s_forcedEventField.GetValue(__instance) != null) return;

            var randomEvent = (RandomEvent)s_randomEventField.GetValue(__instance);
            var priorActive = (RandomEvent)s_activeEventField.GetValue(__instance);

            RandomEvent desiredActive = null;
            if (randomEvent != null)
            {
                bool anyPeer = (bool)s_isAnyPlayerInEventArea.Invoke(
                    __instance, new object[] { randomEvent });
                if (anyPeer) desiredActive = randomEvent;
            }

            if (desiredActive == priorActive) return;
            s_setActiveEvent.Invoke(__instance, new object[] { desiredActive, false });
            Plugin.Log.LogInfo(
                $"[Diag] Server-side active event promoted: " +
                $"{(priorActive?.m_name ?? "null")} → {(desiredActive?.m_name ?? "null")}.");
        }
    }

    /// <summary>
    /// Debug helper (gated by Plugin.DebugDisableRaids): suppress random raids
    /// so they can't interfere with whatever is being tested.
    ///
    /// Vanilla's roll is `Random.Range(0f, 100f) <= m_eventChance / eventRate`.
    /// Setting m_eventChance negative makes that unpassable — note 0f would NOT
    /// be sufficient, since Random.Range is inclusive of 0.
    ///
    /// The interval is also shortened, which is harmless while the chance is
    /// negative; it's left in place because this same patch is how we force
    /// *frequent* raids when we need to reproduce an event bug on demand (flip
    /// the chance to 100f).
    /// </summary>
    [HarmonyPatch(typeof(RandEventSystem), "Awake")]
    internal static class RandEventSystem_Awake_DebugDisableRaids_Patch
    {
        static void Postfix(RandEventSystem __instance)
        {
            if (!Plugin.DebugDisableRaids) return;
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;
            __instance.m_eventIntervalMin = 2f;
            __instance.m_eventChance = -1f;
            Plugin.Log.LogWarning(
                "[Debug] Raids DISABLED (eventChance=-1, no roll can pass). " +
                "Set Plugin.DebugDisableRaids=false and rebuild to restore the vanilla cadence.");
        }
    }

    /// <summary>
    /// v0.5.16 fix: heal TerrainComps that lost the Awake race with Heightmap.
    /// Vanilla TerrainComp.Awake bails out (returns before Initialize() and before
    /// adding itself to s_instances) when Heightmap.FindHeightmap returns null,
    /// and never retries. On the server, the zone-load race means this happens
    /// intermittently — leaving the sector's TerrainComp permanently broken until
    /// destroyed/recreated on unload/reload (which is itself a coin flip).
    ///
    /// A prefix on Update re-runs FindHeightmap; if it now returns non-null, we
    /// manually populate m_hmap, call Initialize via reflection (to allocate the
    /// per-vertex arrays), register in s_instances so FindTerrainCompiler works,
    /// then let vanilla's own CheckLoad path (already downstream of us) read the
    /// ZDO's s_TCData and poke the newly-available Heightmap. Same shape as the
    /// SpawnSystem.UpdateSpawning heal we did in v0.4.0.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Update")]
    internal static class TerrainComp_Update_Heal_Patch
    {
        private static readonly FieldInfo s_hmapField = AccessTools.Field(
            typeof(TerrainComp), "m_hmap");
        private static readonly FieldInfo s_initField = AccessTools.Field(
            typeof(TerrainComp), "m_initialized");
        private static readonly FieldInfo s_instancesField = AccessTools.Field(
            typeof(TerrainComp), "s_instances");
        private static readonly MethodInfo s_initializeMethod = AccessTools.Method(
            typeof(TerrainComp), "Initialize");
        private static readonly MethodInfo s_loadMethod = AccessTools.Method(
            typeof(TerrainComp), "Load");
        internal static int s_healCount;

        static void Prefix(TerrainComp __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            bool initialized = (bool)s_initField.GetValue(__instance);
            if (initialized) return;

            var hmap = Heightmap.FindHeightmap(__instance.transform.position);
            if (hmap == null) return; // still not ready — try again next frame.

            s_hmapField.SetValue(__instance, hmap);
            s_initializeMethod.Invoke(__instance, null);

            var instances = (List<TerrainComp>)s_instancesField.GetValue(null);
            if (!instances.Contains(__instance)) instances.Add(__instance);

            // Force Load + Poke instead of relying on CheckLoad's DataRevision
            // check. On disk-loaded ZDOs, DataRevision is a runtime-only sync
            // counter that starts at 0 — same as m_lastDataRevision's default —
            // so CheckLoad short-circuits and never populates the delta arrays,
            // leaving the heightmap raw. Loading directly guarantees the deltas
            // are applied when the heightmap regenerates.
            bool loaded = false;
            byte[] tcData = __instance.GetComponent<ZNetView>()?.GetZDO()?.GetByteArray(ZDOVars.s_TCData);
            if (tcData != null && tcData.Length > 0)
            {
                loaded = (bool)s_loadMethod.Invoke(__instance, null);
                // Poke's signature changed in 25185644: bool delayed → int
                // delayed (0 = immediate, matching the old `false`).
                if (loaded) hmap.Poke(0);
            }

            System.Threading.Interlocked.Increment(ref s_healCount);
            var pos = __instance.transform.position;
            Plugin.Log.LogInfo(
                $"[Diag] TerrainComp healed pos=world({pos.x:F0},{pos.z:F0}) " +
                $"sector={ZoneSystem.GetZone(pos)} totalHeals={s_healCount} " +
                $"tcDataBytes={tcData?.Length ?? 0} loaded={loaded}");
        }
    }

    /// <summary>
    /// v0.5.19 fix: guard TerrainComp.CheckLoad so it can't call Load on an
    /// uninitialized TerrainComp. When Awake bailed (Heightmap race), the delta
    /// arrays are null; any subsequent CheckLoad → Load NREs at `if (num !=
    /// m_modifiedHeight.Length)`. The NRE loses the client's dig data AND
    /// bumps m_lastDataRevision as a side-effect (Load line 150), so future
    /// CheckLoads short-circuit without ever retrying. By skipping Load here,
    /// m_lastDataRevision stays at 0 until our heal fires and does a proper
    /// force-Load, at which point the accumulated pending data is applied.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "CheckLoad")]
    internal static class TerrainComp_CheckLoad_Guard_Patch
    {
        private static readonly FieldInfo s_initField = AccessTools.Field(
            typeof(TerrainComp), "m_initialized");

        static bool Prefix(TerrainComp __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            bool initialized = (bool)s_initField.GetValue(__instance);
            // If not yet initialized, skip vanilla CheckLoad entirely. The heal
            // patch on Update will eventually initialize + force-Load using the
            // current ZDO bytes, which includes anything clients have Saved in
            // the meantime.
            return initialized;
        }
    }

    /// <summary>
    /// v0.5.18 diagnostic: log every successful TerrainComp.Load on the server.
    /// Load is called from CheckLoad whenever DataRevision advances (either
    /// because our heal triggered it directly, or because ZDOMan propagated a
    /// remote-owner Save). This lets us distinguish "server has stale terrain
    /// because clients aren't sending diffs" from "diffs are arriving but the
    /// heightmap isn't being poked correctly." Postfix so we only log the actual
    /// completed load (Load returns false on decompression failure, we skip that).
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Load")]
    internal static class TerrainComp_Load_Diag_Patch
    {
        static void Postfix(TerrainComp __instance, bool __result)
        {
            if (!__result) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pos = __instance.transform.position;
            var nview = __instance.GetComponent<ZNetView>();
            uint dataRev = nview?.GetZDO()?.DataRevision ?? 0;
            long owner = nview?.GetZDO()?.GetOwner() ?? 0;
            Plugin.Log.LogInfo(
                $"[Diag] TerrainComp.Load pos=world({pos.x:F0},{pos.z:F0}) " +
                $"sector={ZoneSystem.GetZone(pos)} owner={owner} newDataRev={dataRev}");
        }
    }

    /// <summary>
    /// v0.5.21 diagnostic: log every ownership change on a TerrainComp ZDO,
    /// wherever it originates (our ReleaseZDOS reclaim, a client's own
    /// ClaimOwnership call before digging, vanilla's absent-owner handling,
    /// etc). Terrain digs still aren't reaching the server after we stopped
    /// reclaiming these ZDOs (v0.5.20) — this catches any *other* code path
    /// still flipping ownership around, which would explain a client's
    /// InvokeRPC landing on a stale/wrong session.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), "SetOwner")]
    internal static class ZDO_SetOwner_TerrainCompDiag_Patch
    {
        static void Prefix(ZDO __instance, long uid)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZNetScene.instance == null) return;
            var prefab = ZNetScene.instance.GetPrefab(__instance.GetPrefab());
            if (prefab == null || prefab.GetComponent<TerrainComp>() == null) return;

            long prevOwner = __instance.GetOwner();
            if (prevOwner == uid) return; // vanilla SetOwner no-ops in this case anyway
            var pos = __instance.GetPosition();
            Plugin.Log.LogWarning(
                $"[Diag] TerrainComp ZDO ownership change pos=world({pos.x:F0},{pos.z:F0}) " +
                $"sector={ZoneSystem.GetZone(pos)} {prevOwner} → {uid} " +
                $"prevLive={ServerCoverage.IsLivePeer(prevOwner)} newLive={ServerCoverage.IsLivePeer(uid)}");
        }
    }

    /// <summary>
    /// v0.6.1 fix: build 25185644 added a local-player dereference to
    /// Pickable.RPC_Pick that NREs on a dedicated server, breaking EVERY
    /// pickable (berries, mushrooms, flint, thistle...):
    ///
    ///   old: m_pickEffector.Create(basePos, Quaternion.identity);
    ///   new: m_pickEffector.Create(basePos, Quaternion.identity, null, 1f, -1,
    ///                              Player.m_localPlayer.GetZDOID());
    ///
    /// RPC_Pick only runs on the ZDO's owner. In stock multiplayer that's the
    /// picking client, where m_localPlayer exists — so vanilla never trips over
    /// it. Our plugin makes the server the owner, so it runs where
    /// m_localPlayer is null. The NRE fires *before* any Drop() call, so no
    /// items spawn and RPC_SetPicked never broadcasts: the bush just sits there.
    /// This is "Pattern C" from [[valheim-local-player-concept]].
    ///
    /// Transpiler rather than a reimplementation Prefix: only this one
    /// expression is broken, and everything else should keep flowing through
    /// vanilla. A hand-copied RPC_Pick body would need re-auditing on every
    /// future update — the exact trap that made SpawnSystem.UpdateSpawning
    /// silently drop the new alt-biome spawn loop.
    ///
    /// The replacement is a null-safe equivalent returning ZDOID.None, which is
    /// what the effect system uses to mean "no associated player" anyway — and
    /// the effect is purely cosmetic on a headless server regardless.
    /// </summary>
    [HarmonyPatch(typeof(Pickable), "RPC_Pick")]
    internal static class Pickable_RPC_Pick_NullLocalPlayer_Patch
    {
        private static ZDOID SafeLocalPlayerZDOID()
        {
            var player = Player.m_localPlayer;
            return player != null ? player.GetZDOID() : ZDOID.None;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var localPlayerField = AccessTools.Field(typeof(Player), nameof(Player.m_localPlayer));
            var getZdoid = AccessTools.Method(typeof(Character), nameof(Character.GetZDOID));
            var replacement = AccessTools.Method(
                typeof(Pickable_RPC_Pick_NullLocalPlayer_Patch), nameof(SafeLocalPlayerZDOID));

            var codes = new List<CodeInstruction>(instructions);
            bool patched = false;
            for (int i = 0; i < codes.Count - 1; i++)
            {
                // ldsfld Player::m_localPlayer  →  callvirt Character::GetZDOID()
                if (codes[i].LoadsField(localPlayerField) && codes[i + 1].Calls(getZdoid))
                {
                    codes[i] = new CodeInstruction(OpCodes.Call, replacement)
                        .MoveLabelsFrom(codes[i])
                        .MoveBlocksFrom(codes[i]);
                    codes[i + 1] = new CodeInstruction(OpCodes.Nop);
                    patched = true;
                    break;
                }
            }
            if (!patched)
            {
                // Loud, because silently failing here means every pickable in
                // the world stops working with only a stack trace to show for it.
                Plugin.Log.LogError(
                    "[Pickable] Transpiler could not find the Player.m_localPlayer.GetZDOID() " +
                    "sequence in RPC_Pick — vanilla IL changed. Pickables will NRE on the " +
                    "server until this patch is updated.");
            }
            return codes;
        }
    }

    /// <summary>
    /// v0.5.24 fix: suppress NRE spam from ShieldDomeImageEffect.GetDomeColor on
    /// the server. ShieldGenerator.UpdateShield runs every 0.22s for every active
    /// ShieldGenerator and unconditionally calls GetDomeColor(m_lastFuel), which
    /// reads a static field (s_staticGradient) that's only ever assigned inside
    /// ShieldDomeImageEffect.Awake() — a camera image-effect component that never
    /// exists on a headless dedicated server. Purely a visual particle/light-color
    /// computation with no gameplay effect and nothing to render server-side, so
    /// we just short-circuit with a placeholder color instead of NREing forever.
    /// </summary>
    [HarmonyPatch(typeof(ShieldDomeImageEffect), nameof(ShieldDomeImageEffect.GetDomeColor))]
    internal static class ShieldDomeImageEffect_GetDomeColor_NRESuppress_Patch
    {
        private static readonly FieldInfo s_staticGradientField = AccessTools.Field(
            typeof(ShieldDomeImageEffect), "s_staticGradient");

        static bool Prefix(ref Color __result)
        {
            if (s_staticGradientField.GetValue(null) != null) return true;
            __result = Color.white;
            return false;
        }
    }

    /// <summary>
    /// v0.6.2 diagnostic: trace Ship physics state on the server.
    ///
    /// Symptom: changing boat speed makes the boat lunge downward and take
    /// damage; steering and wind behave normally.
    ///
    /// Why those split: Ship.CustomFixedUpdate runs UpdateSail/UpdateRudder for
    /// everyone but returns early before applying any forces unless
    /// m_nview.IsOwner(). And Forward()/Backward()/Stop() use InvokeRPC (routed
    /// to the ZDO owner) while Rudder() uses a local Invoke — so under our
    /// server-owns-everything model, speed changes land on the server and
    /// steering stays client-local.
    ///
    /// Two candidate causes, both measured here:
    ///  1. Water level. Floating.GetWaterLevel returns -10000 when no
    ///     WaterVolume collider is found at a point. That makes
    ///     `heightAboveWater = comY - waterLevel - offset` ≈ +10030, which is
    ///     greater than m_disableLevel (-0.5), so the ENTIRE buoyancy + sail +
    ///     damping block is skipped and the hull free-falls under gravity —
    ///     exactly "lunges downward and takes damage".
    ///  2. Players aboard. m_players is filled by OnTriggerEnter against
    ///     replicated player colliders. If the server never registers anyone
    ///     aboard, it forces m_speed=Stop and rudder=0 every tick and cuts
    ///     horizontal velocity to 10%.
    ///
    /// Also logs the ZDO owner, because vanilla's own handoff (Ship.UpdateOwner)
    /// gives the ship to a player aboard but is gated behind
    /// `Player.m_localPlayer != null` — so it can never run on a dedicated
    /// server, leaving our ReleaseZDOS claim permanent. See
    /// [[valheim-local-player-concept]].
    ///
    /// Throttled per ship to ~2s. Postfix so vanilla has already updated state.
    /// </summary>
    [HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate))]
    internal static class Ship_CustomFixedUpdate_Diag_Patch
    {
        private static readonly FieldInfo s_nviewField = AccessTools.Field(typeof(Ship), "m_nview");
        private static readonly FieldInfo s_bodyField = AccessTools.Field(typeof(Ship), "m_body");
        private static readonly FieldInfo s_playersField = AccessTools.Field(typeof(Ship), "m_players");
        private static readonly FieldInfo s_speedField = AccessTools.Field(typeof(Ship), "m_speed");
        private static readonly FieldInfo s_rudderField = AccessTools.Field(typeof(Ship), "m_rudderValue");

        private static readonly Dictionary<int, float> s_lastLog = new Dictionary<int, float>();
        private const float ThrottleSeconds = 2f;

        static void Postfix(Ship __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            int key = __instance.GetInstanceID();
            float now = Time.time;
            if (s_lastLog.TryGetValue(key, out float last) && now - last < ThrottleSeconds) return;
            s_lastLog[key] = now;

            var nview = (ZNetView)s_nviewField.GetValue(__instance);
            var body = (Rigidbody)s_bodyField.GetValue(__instance);
            if (nview == null || body == null) return;

            bool isOwner = nview.IsOwner();
            long owner = nview.GetZDO()?.GetOwner() ?? 0;
            var players = (List<Player>)s_playersField.GetValue(__instance);
            int playerCount = players?.Count ?? -1;
            var speed = s_speedField.GetValue(__instance);
            float rudder = (float)s_rudderField.GetValue(__instance);

            // Recompute the water check the same way Ship does, but with our own
            // WaterVolume cache slot so we don't disturb the ship's cached one.
            var com = body.worldCenterOfMass;
            WaterVolume probe = null;
            float waterLevel = Floating.GetWaterLevel(com, ref probe);
            float heightAboveWater = com.y - waterLevel - __instance.m_waterLevelOffset;
            bool physicsRan = !(heightAboveWater > __instance.m_disableLevel);
            bool waterFound = waterLevel > -9000f;

            Plugin.Log.LogWarning(
                $"[Diag] Ship prefab={__instance.gameObject.name.Replace("(Clone)", "")} " +
                $"pos=world({com.x:F0},{com.y:F1},{com.z:F0}) sector={ZoneSystem.GetZone(com)} " +
                $"owner={owner} isOwner={isOwner} ownerLive={ServerCoverage.IsLivePeer(owner)} " +
                $"playersAboard={playerCount} speed={speed} rudder={rudder:F2} " +
                $"waterLevel={waterLevel:F1} waterFound={waterFound} " +
                $"heightAboveWater={heightAboveWater:F2} disableLevel={__instance.m_disableLevel:F2} " +
                $"PHYSICS_RAN={physicsRan} vel=({body.linearVelocity.x:F1},{body.linearVelocity.y:F1},{body.linearVelocity.z:F1})");
        }
    }

    /// <summary>
    /// v0.5.23 diagnostic: trace Vagon (cart) attach/detach on the server.
    /// Theory: Vagon.FixedUpdate only maintains the ConfigurableJoint (distance
    /// check via CanAttach, physics via m_breakForce) on whichever machine owns
    /// the ZDO. Our plugin makes the server own it, so the server computes the
    /// joint against the puller's network-REPLICATED position (laggy/interpolated)
    /// instead of their real local rigidbody — unlike vanilla, where ownership
    /// normally goes to the nearest peer (usually the puller themselves). If the
    /// replicated position drifts past m_detachDistance even briefly, or a
    /// correction spikes past m_breakForce, the server calls Detach() even
    /// though the player never let go. This patch logs every Detach with the
    /// actual computed distance vs threshold so we can confirm or rule this out.
    /// </summary>
    [HarmonyPatch(typeof(Vagon), "Detach")]
    internal static class Vagon_Detach_Diag_Patch
    {
        private static readonly FieldInfo s_attachJoinField = AccessTools.Field(
            typeof(Vagon), "m_attachJoin");
        private static readonly FieldInfo s_attachedObjectField = AccessTools.Field(
            typeof(Vagon), "m_attachedObject");
        private static readonly FieldInfo s_nviewField = AccessTools.Field(
            typeof(Vagon), "m_nview");

        static void Prefix(Vagon __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var joint = (ConfigurableJoint)s_attachJoinField.GetValue(__instance);
            var attachedObj = (GameObject)s_attachedObjectField.GetValue(__instance);
            var nview = (ZNetView)s_nviewField.GetValue(__instance);
            var pos = __instance.transform.position;
            long owner = nview?.GetZDO()?.GetOwner() ?? 0;

            string reason;
            float dist = -1f;
            if (attachedObj == null)
            {
                reason = "no_attached_object (already detached or never attached)";
            }
            else
            {
                dist = Vector3.Distance(
                    attachedObj.transform.position + __instance.m_attachOffset,
                    __instance.m_attachPoint.position);
                reason = dist >= __instance.m_detachDistance
                    ? $"DISTANCE_EXCEEDED ({dist:F2}m >= {__instance.m_detachDistance:F2}m threshold)"
                    : "joint_broke_or_owner_changed_or_puller_stopped";
            }

            Plugin.Log.LogWarning(
                $"[Diag] Vagon.Detach prefab={__instance.gameObject.name.Replace("(Clone)","")} " +
                $"pos=world({pos.x:F0},{pos.z:F0}) owner={owner} " +
                $"attachedObj={(attachedObj != null ? attachedObj.name : "null")} " +
                $"hadJoint={joint != null} dist={dist:F2} reason={reason}");
        }
    }

    /// <summary>
    /// v0.5.23 diagnostic: log every successful Vagon attach so we can pair it
    /// with the Detach line and measure how long the cart stayed attached.
    /// </summary>
    [HarmonyPatch(typeof(Vagon), "AttachTo")]
    internal static class Vagon_AttachTo_Diag_Patch
    {
        private static readonly FieldInfo s_nviewField = AccessTools.Field(
            typeof(Vagon), "m_nview");

        static void Postfix(Vagon __instance, GameObject go)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pos = __instance.transform.position;
            var nview = (ZNetView)s_nviewField.GetValue(__instance);
            long owner = nview?.GetZDO()?.GetOwner() ?? 0;
            Plugin.Log.LogInfo(
                $"[Diag] Vagon.AttachTo prefab={__instance.gameObject.name.Replace("(Clone)","")} " +
                $"pos=world({pos.x:F0},{pos.z:F0}) owner={owner} attachedTo={go.name}");
        }
    }

    /// <summary>
    /// v0.5.15 diagnostic: trace TerrainComp lifecycle on the server. Hypothesis:
    /// TerrainComp.Awake bails when Heightmap.FindHeightmap returns null (zone-load
    /// race), leaving m_initialized=false. Later RPC_ApplyOperation from a digging
    /// client arrives at the server (since our ReleaseZDOS made the server the
    /// owner), tries to run, and Save short-circuits on !m_initialized — the
    /// deformation is silently lost. We log Awake outcome + RPC outcome to confirm.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "Awake")]
    internal static class TerrainComp_Awake_Diag_Patch
    {
        private static readonly FieldInfo s_hmapField = AccessTools.Field(
            typeof(TerrainComp), "m_hmap");
        private static readonly FieldInfo s_initField = AccessTools.Field(
            typeof(TerrainComp), "m_initialized");

        static void Postfix(TerrainComp __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pos = __instance.transform.position;
            var hmap = (Heightmap)s_hmapField.GetValue(__instance);
            bool initialized = (bool)s_initField.GetValue(__instance);
            var nview = __instance.GetComponent<ZNetView>();
            long owner = nview?.GetZDO()?.GetOwner() ?? 0;
            byte[] tcData = nview?.GetZDO()?.GetByteArray(ZDOVars.s_TCData);
            Plugin.Log.LogInfo(
                $"[Diag] TerrainComp.Awake pos=world({pos.x:F0},{pos.z:F0}) " +
                $"sector={ZoneSystem.GetZone(pos)} hmapFound={hmap != null} " +
                $"initialized={initialized} owner={owner} tcDataBytes={tcData?.Length ?? 0}");
        }
    }

    /// <summary>
    /// v0.5.15 diagnostic: log every ApplyOperation RPC the server receives.
    /// If we see Awake with hmapFound=False followed by RPC_ApplyOperation with
    /// initialized=False, we've confirmed the hypothesis and the fix is a lazy
    /// hmap re-find (same pattern as the SpawnSystem v0.4.0 heal).
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "RPC_ApplyOperation")]
    internal static class TerrainComp_RPC_ApplyOperation_Diag_Patch
    {
        private static readonly FieldInfo s_hmapField = AccessTools.Field(
            typeof(TerrainComp), "m_hmap");
        private static readonly FieldInfo s_initField = AccessTools.Field(
            typeof(TerrainComp), "m_initialized");

        static void Prefix(TerrainComp __instance, long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pos = __instance.transform.position;
            var hmap = (Heightmap)s_hmapField.GetValue(__instance);
            bool initialized = (bool)s_initField.GetValue(__instance);
            var nview = __instance.GetComponent<ZNetView>();
            bool isOwner = nview != null && nview.IsOwner();
            Plugin.Log.LogWarning(
                $"[Diag] TerrainComp.RPC_ApplyOperation from sender={sender} " +
                $"pos=world({pos.x:F0},{pos.z:F0}) sector={ZoneSystem.GetZone(pos)} " +
                $"isOwner={isOwner} hmapFound={hmap != null} initialized={initialized} " +
                $"willSave={initialized && isOwner}");
        }
    }

    /// <summary>
    /// v0.5.14 diagnostic: log every WearNTear.Destroy call on the server so we
    /// can see WHY base pieces are dying. Vanilla WearNTear.UpdateWear can destroy
    /// a piece for four reasons:
    ///   1. rain wear (no roof, wet, HP>50%): -5% per 60s
    ///   2. no support: instant 100% damage
    ///   3. ashlands ash damage (biome specific)
    ///   4. ashlands lava damage (biome specific)
    /// External hits pass a non-null HitData. Natural wear passes null. We log
    /// enough context to tell the four apart plus attacker info if present.
    /// </summary>
    [HarmonyPatch(typeof(WearNTear), "Destroy")]
    internal static class WearNTear_Destroy_Diag_Patch
    {
        private static readonly FieldInfo s_biomeField = AccessTools.Field(
            typeof(WearNTear), "m_biome");
        private static readonly FieldInfo s_haveRoofField = AccessTools.Field(
            typeof(WearNTear), "m_haveRoof");
        private static readonly MethodInfo s_haveSupportMethod = AccessTools.Method(
            typeof(WearNTear), "HaveSupport");

        static void Prefix(WearNTear __instance, HitData hitData)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var pos = __instance.transform.position;
            string prefab = __instance.gameObject.name.Replace("(Clone)", "");
            var biome = (Heightmap.Biome)s_biomeField.GetValue(__instance);
            bool haveSupport = (bool)s_haveSupportMethod.Invoke(__instance, null);
            bool haveRoof = (bool)s_haveRoofField.GetValue(__instance);
            bool wet = __instance.IsWet();
            string support = haveSupport ? "supported" : "UNSUPPORTED";
            string cause;
            string attacker = "";
            if (hitData == null)
            {
                if (__instance.m_noSupportWear && !haveSupport)
                    cause = "NATURAL_NO_SUPPORT";
                else if (biome == Heightmap.Biome.AshLands)
                    cause = "NATURAL_ASHLANDS";
                else if (__instance.m_noRoofWear && wet)
                    cause = "NATURAL_RAIN_ROT";
                else
                    cause = "NATURAL_OTHER";
            }
            else
            {
                cause = "HIT";
                attacker = $" hitType={hitData.m_hitType} dmg={hitData.GetTotalDamage():F1} attackerId={hitData.m_attacker}";
            }
            float nearestPeerDist = float.PositiveInfinity;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var pp = peer.m_refPos;
                float d = UnityEngine.Vector2.Distance(
                    new UnityEngine.Vector2(pos.x, pos.z),
                    new UnityEngine.Vector2(pp.x, pp.z));
                if (d < nearestPeerDist) nearestPeerDist = d;
            }
            Plugin.Log.LogWarning(
                $"[Diag] WearNTear.Destroy prefab={prefab} " +
                $"pos=world({pos.x:F0},{pos.y:F1},{pos.z:F0}) biome={biome} " +
                $"cause={cause}{attacker} support={support} haveRoof={haveRoof} wet={wet} " +
                $"nearestPeerDist={nearestPeerDist:F0}m");
        }
    }

    /// <summary>
    /// v0.5.13 diagnostic: log every ItemStand.RPC_DropItem the server receives.
    /// User reports items are not coming off stands when hold-interacting.
    /// If the client short-circuits (m_canBeRemoved=false on that stand type or a
    /// ward blocks the interaction), the RPC never arrives and no log line appears.
    /// If the RPC arrives, we log what the server saw and decided so we know why
    /// the DropItem() body did or didn't fire.
    /// </summary>
    [HarmonyPatch(typeof(ItemStand), "RPC_DropItem")]
    internal static class ItemStand_RPC_DropItem_Diag_Patch
    {
        private static readonly FieldInfo s_nviewField = AccessTools.Field(
            typeof(ItemStand), "m_nview");

        static void Prefix(ItemStand __instance, long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var nview = (ZNetView)s_nviewField.GetValue(__instance);
            bool isOwner = nview != null && nview.IsOwner();
            bool haveAttachment = __instance.HaveAttachment();
            bool canBeRemoved = __instance.m_canBeRemoved;
            var pos = __instance.transform.position;
            long ownerId = nview?.GetZDO()?.GetOwner() ?? 0;
            string attachedItem = nview?.GetZDO()?.GetString(ZDOVars.s_item) ?? "";
            Plugin.Log.LogInfo(
                $"[Diag] ItemStand RPC_DropItem received: sender={sender} " +
                $"pos=world({pos.x:F0},{pos.z:F0}) prefab={__instance.gameObject.name.Replace("(Clone)","")} " +
                $"isOwner={isOwner} owner={ownerId} canBeRemoved={canBeRemoved} " +
                $"haveAttachment={haveAttachment} attachedItem='{attachedItem}' " +
                $"willDrop={isOwner && canBeRemoved}");
        }
    }

    /// <summary>
    /// v0.5.9 diagnostic: log every Wet/Tar application to a non-player character.
    /// Both effects are gated by the same Character.UpdateWater branch (m_waterLevel
    /// vs m_tarLevel), so logging both lets us tell apart "mob is genuinely in water"
    /// from "mob is on tar" from "mob shouldn't have either but does anyway."
    /// Throttled per (character, effect) pair to one line per 30s so a stationary
    /// mob standing in a puddle doesn't spam. Server-only.
    /// </summary>
    // Build 25185644 added a 5th parameter (short variant) to both
    // AddStatusEffect overloads; the type array must match exactly or Harmony
    // can't resolve the target and throws out of PatchAll.
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect),
                  new[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) })]
    internal static class SEMan_AddStatusEffect_WetTarDiag_Patch
    {
        private static readonly int s_wetHash = "Wet".GetStableHashCode();
        private static readonly int s_tarHash = "Tared".GetStableHashCode();
        private static readonly Dictionary<long, float> s_lastLogTime =
            new Dictionary<long, float>();
        private static readonly FieldInfo s_characterField = AccessTools.Field(
            typeof(SEMan), "m_character");
        private static readonly FieldInfo s_waterLevelField = AccessTools.Field(
            typeof(Character), "m_waterLevel");
        private static readonly FieldInfo s_tarLevelField = AccessTools.Field(
            typeof(Character), "m_tarLevel");
        private const float ThrottleSeconds = 30f;

        static void Postfix(SEMan __instance, int nameHash, StatusEffect __result)
        {
            if (__result == null) return;
            if (nameHash != s_wetHash && nameHash != s_tarHash) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var character = (Character)s_characterField.GetValue(__instance);
            if (character == null) return;
            if (character is Player) return;

            long key = (long)character.GetInstanceID() * 3L + (nameHash == s_wetHash ? 0 : 1);
            var now = Time.time;
            if (s_lastLogTime.TryGetValue(key, out float last) && now - last < ThrottleSeconds)
                return;
            s_lastLogTime[key] = now;

            var pos = character.transform.position;
            string effect = (nameHash == s_wetHash) ? "Wet" : "Tar";
            string prefab = character.gameObject.name.Replace("(Clone)", "");
            float waterLevel = (float)s_waterLevelField.GetValue(character);
            float tarLevel = (float)s_tarLevelField.GetValue(character);
            float liquidLevel = (nameHash == s_wetHash) ? waterLevel : tarLevel;
            float depthBelow = liquidLevel - pos.y;
            string verdict = depthBelow >= 0.1f
                ? "GENUINELY_IN_LIQUID"
                : (depthBelow >= -0.5f ? "AT_SURFACE" : "ABOVE_LIQUID_SUSPICIOUS");

            // Distance to nearest peer so we know if the mob is even visible.
            float nearestPeerDist = float.PositiveInfinity;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_refPos == null) continue;
                var pp = peer.m_refPos;
                float d = UnityEngine.Vector2.Distance(
                    new UnityEngine.Vector2(pos.x, pos.z),
                    new UnityEngine.Vector2(pp.x, pp.z));
                if (d < nearestPeerDist) nearestPeerDist = d;
            }

            Plugin.Log.LogInfo(
                $"[Diag] {effect} applied to {prefab} at world({pos.x:F0},{pos.y:F1},{pos.z:F0}) " +
                $"{effect.ToLower()}Level={liquidLevel:F2} depthBelow={depthBelow:F2}m " +
                $"verdict={verdict} nearestPeerDist={nearestPeerDist:F0}m " +
                $"tolerateWater={character.m_tolerateWater} tolerateTar={character.m_tolerateTar}");
        }
    }
}
