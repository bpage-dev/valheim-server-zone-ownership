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
        public const string PluginVersion = "0.7.92";

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
        private const float NRESummaryIntervalSeconds = 60f;
        private const float PeerAwareSummaryIntervalSeconds = 60f;
        // --- createDestroy stride (v0.7.10) -------------------------------
        // 1 = vanilla behaviour (every tick). >1 skips the object create/destroy
        // pass on all but every Nth tick, rehearsing how round-robin would feel:
        // with N peers, round-robin refreshes each peer every Nth tick. Set to 8
        // with ONE player online to feel an 8-player round-robin. Watch for late
        // object pop-in while travelling fast (boat, cart). Put back to 1 after.
        //
        // BPTODO refactor roundrobin the create/destroy ticks among players
        // rather than artificially all on every x'th frame (eg 8), flattening spike
        // NOTE the stride's cost is partly offset by an accident in our favour:
        // ZNetScene.InLoadingScreen() returns true unconditionally on a headless
        // server (no local player), so vanilla hands us maxCreatedPerFrame=100
        // permanently where a client gets 10 outside of teleports. Stride 8 costs
        // 8x in frequency, that gives back 10x in volume per pass. Change either
        // one without the other and object-creation latency moves sharply — see
        // ZNetScene_CreateObjectsSorted_Diag_Patch.
        internal const int DebugCreateDestroyStride = 8;

        // v0.7.55 — ROUND-ROBIN KILL SWITCH. false restores the previous
        // single-spike behaviour exactly: both phases on the full union, once
        // per stride. Creation is sliced per peer when true; removal is NEVER
        // sliced either way.
        internal const bool DebugRoundRobinCreate = true;

        // --- Per-peer send rate (v0.7.17) ---------------------------------
        // Sends per second EACH peer should receive, independent of how many
        // players are online. 0 = leave vanilla's scheduler alone, where the
        // rate divides by player count (~12/sec at 1 player, ~3/sec at 8) and
        // server-owned mobs visibly stutter for everyone in a group.
        // 20 matches vanilla's own 0.05s cap, i.e. what a lone player already
        // gets. Watch PassTimer's peerSend cost and the [Perf] starvation
        // figures when raising it; back off if the server stops holding 33ms.
        internal const float DebugPeerSendHz = 20f;   // THE FIX: vanilla's own per-peer cap, applied per peer instead of per round

        // --- Synthetic peer load test (v0.7.3) ----------------------------
        // Fake extra players so we can measure 8-player load without 8 people.
        // See SyntheticPeers for the mechanism and its limits (no network load).
        // OFF unless the anchor is set to a character name; the ring then
        // follows that player and stops when they log out.
        // KEEP THIS EMPTY except during a deliberate load test — the fake areas
        // are fully live: zones load, mobs spawn and wander there. Stay ~800m
        // clear of bases you care about at a 600m radius.
        internal const string DebugSyntheticPeerAnchor = "";  // "" = off. Set to a character name only for a deliberate load test
        internal const int DebugSyntheticPeerCount = 7;   // 7 + the anchor = 8
        internal const float DebugSyntheticPeerMinRadius = 600f;   // annulus around the anchor
        internal const float DebugSyntheticPeerMaxRadius = 1400f;
        internal const float DebugSyntheticPeerDriftSpeed = 3f;    // m/s, roughly a walking player

        // When the daily [Digest] block is written (local time, matching the
        // AutoHotkey scripts). 05:30 sits before TimedLogCopy.ahk archives the
        // log at 05:58 and TimedRestart.ahk reboots at 06:00, so the day's
        // summary lands inside that morning's archive. To test, set these a
        // couple of minutes ahead and restart.
        internal const int DigestHour = 5;
        internal const int DigestMinute = 30;

        // --- Send census (v0.7.28) -----------------------------------------
        // Seconds between [Census] blocks, or 0 to disable. VERBOSE: one header
        // plus a line per prefab, so this is a deliberate diagnostic, not
        // something to leave running. Set back to 0 once a capture is in hand.
        //
        // Unlike SyncVolume (which samples CANDIDATE lists) this counts what was
        // actually written into an outgoing packet, by hooking ZDO.Serialize —
        // the call vanilla makes once per ZDO that fits inside the byte budget.
        // So these are real sends, with real byte counts, not offers.
        internal const float DebugSendCensusSeconds = 0f;   // v0.7.85: 0 = off (was 5s, ~274 lines a pop)

        // v0.7.69 bug #11: run the pheromone mechanic server-side. False
        // restores vanilla behaviour, which on this server is a silent no-op.
        internal const bool DebugPheromoneServerSide = true;

        // Vanilla applies NO distance test — it iterates every player, which on
        // a client is implicitly bounded because only nearby players exist. On
        // this server they all exist, so a bound is required.
        //
        // v0.7.72: set to the distance mobs can spawn from a player, so the
        // effect reaches every matching creature that could have spawned for
        // them. SpawnSystem uses a 40-80m ring (m_spawnDistanceMin/Max, and
        // per-spawner radii defaulting to the same), so 80 is the outer edge.
        // For comparison the loaded area at simulationdistance 1 is a 3x3 block
        // of 64m zones, about 96m to the corner — spawn range is the tighter
        // and more meaningful bound. ObservedSpawnRadius logs what spawners
        // actually declare, so this can be corrected from data.
        internal const float PheromoneRadius = 80f;

        // v0.7.71 bug #11: implement the spawn/level values the asset authors
        // and vanilla never reads. BEYOND vanilla, deliberately - independent
        // of DebugPheromoneServerSide so it can be reverted on its own.
        internal const bool DebugPheromoneSpawnBoost = true;

        // v0.7.80 bug #9: re-broadcast a destroy for a phantom object once a
        // client has asked for it enough times to rule out a race. Every retry
        // logs a warning, because a wrongful destroy would otherwise be hidden
        // rather than surfaced.
        internal const bool DebugGhostDestroyRetry = true;

        // v0.7.81 bug #9 prevention: repeat each destroy broadcast once, so a
        // single lost packet no longer leaves a cluster of phantoms. Loss on
        // separate sends is independent, so this squares the failure rate.
        internal const bool DebugDuplicateDestroySend = true;

        // v0.7.83 (bug #7): ask vanilla IsAreaReady about a teleport destination
        // from the server and name the ZDO that fails it.
        internal const bool DebugTeleportAreaProbe = true;

        // v0.7.83 (bug #7): poll whether the server has a GameObject for each
        // player, rather than waiting for an RPC to reveal that it does not.
        internal const bool DebugPlayerInstanceWatch = true;

        // v0.7.83 (bug #7): watch the zone-loading registry for an entry that
        // never unsets, which blocks every lower-ranked object in that zone.
        internal const bool DebugZoneLoadingWatch = true;

        // v0.7.84: time DungeonGenerator.Spawn, the synchronous interior rebuild
        // that 64% of this session's hitches correlate with. Reports ms, room
        // count, how many times that same dungeon has been rebuilt, and the
        // horizontal distance to the nearest peer.
        internal const bool DebugDungeonSpawnTimer = true;

        // v0.7.91 (bug #31): do not build a dungeon interior until a player is
        // in its active area (3x3 zones) rather than anywhere in our 5x5
        // coverage box. Gates the generator AND its contents together.
        internal const bool DebugGateDungeonInteriors = true;

        // --- Distance-tiered send rate (v0.7.30) ---------------------------
        // THE PROBLEM: because the server owns every mob, every mob's motion
        // has to be sent from here to every player. Vanilla refuses to send to
        // a peer whose queue is over 8192 bytes and caps each packet at
        // 10240-queue, so past a certain volume sends are refused and packets
        // truncated — which is the mob stutter reported in group play.
        // Measured 2026-10-01/04: gating averages 2-7% solo but ~22% with just
        // TWO players, because a second player enlarges the union of loaded
        // zones, so more mobs tick and more ZDOs change.
        //
        // THE FIX: cap how often each peer is told about a given ZDO, by that
        // ZDO's distance to THAT peer. Vanilla has no such concept — distance
        // appears only in the sort order, which is never consulted unless the
        // packet overflows. Near things stay fluid; distant things still update,
        // just less often.
        //
        // Deliberately NOT type-aware. A projectile that matters is near, so
        // distance already keeps it at full rate, and anything genuinely static
        // never enters the list at all (ShouldSend is revision-based — measured
        // walls at ~1.4 sends each, i.e. once).
        internal const bool DebugSendTiers = true;      // false = vanilla, one bool

        // Upper bound of each tier in metres, ascending, last must be huge.
        internal static readonly float[] SendTierEdges = { 30f, 100f, float.MaxValue };

        // How many send opportunities a ZDO in each tier must skip: 1 = may ride
        // every send, 2 = every other, 7 = every seventh. 1 means uncapped.
        //
        // Counted in OPPORTUNITIES, not seconds, and that is the whole point.
        // v0.7.30-0.7.37 compared wall-clock timestamps against a 1/Hz deadline,
        // but opportunities arrive on frame boundaries ~33ms and ~67ms apart
        // around a 50ms mean, so a deadline lands between them and whether it
        // passes is luck. That needed a tolerance fudge, the fudge needed its own
        // correction, and the correction silently halved the near tier (300 of
        // 649 near candidates throttled, 10.0/s against a 20/s cap). Counting
        // opportunities is exact: frame timing cannot affect "has this skipped
        // one send yet".
        //
        // The resulting rate is DebugPeerSendHz / stride, so at 20/s:
        //   1 -> 20/s    2 -> 10/s    3 -> 6.7/s    4 -> 5/s    7 -> 2.9/s
        // Rates between those steps are not reachable — you cannot send on a
        // fraction of an opportunity. What IS exact is the share: a stride-2
        // object gets precisely half the sends.
        internal static readonly int[] SendTierEveryNth = { 1, 2, 7 };

        // Seconds between [Tier] accounting blocks (0 = silent, but the cap and
        // its pruning still run). Four lines per window, so this is cheap enough
        // to leave on through a group session.
        // 5s for a short capture (an hour gives ~720 points, good chart
        // resolution). Raise to 20f for long unattended runs — at 8 players this
        // is 32 lines per window, so 5s means ~6.4 lines/sec.
        internal const float DebugSendTierSeconds = 0f;   // v0.7.85: 0 = prune only, no print; pruning still runs every 30s

        // Promote projectiles above Default in the send order (v0.7.42). Pure
        // ordering — projectiles still pass the same distance stride as
        // everything else. false = vanilla ordering, one bool.
        internal const bool DebugProjectilePriority = true;

        // v0.7.52 — BUG #7 KILL SWITCH. Set false to restore the pre-fix
        // behaviour exactly (random-event spawners run in every zone that
        // contains a player), then rebuild and restart. One line, no git.
        //
        // Scopes random-event spawning to zones inside the event area.
        // SpawnSystem is per-zone and vanilla only asks "are there players in
        // MY zone", never "is my zone near the event" — correct on a client
        // where the only loaded zones surround the local player, who is inside
        // the event area by definition, but wrong here because zones load
        // around every peer. Result: an invasion at one base attacked players
        // anywhere in the world.
        //
        // Gated on vanilla's own IsInsideRandomEventArea, and affects ONLY the
        // event spawner list — see the isolation proof in the bug table.
        internal const bool DebugScopeEventSpawns = true;


        // --- Navmesh tile pressure knobs (v0.7.0) --------------------------
        // Mob pathfinding builds 32m navmesh tiles on demand, one layer per
        // agent size, and stitches neighbouring tiles together with links.
        // Links come from a single engine-wide pool of 65535; at ~64 links per
        // tile that is ~1000 live tiles for the whole server. Because we own
        // every mob for every peer, all of that pressure lands on one machine:
        // 8 spread-out peers on 2026-09-22 exhausted the pool and logged 6337
        // "Failed to allocate NavMeshLink" errors in 2.5 minutes, which is what
        // group-combat lag looked like from the inside.
        //
        // How long an unused tile is kept before it can be freed.
        // VANILLA DEFAULT: 60s (measured live, 2026-09-22).
        // The decompiled C# initialiser claims 30f, but the Pathfinding prefab
        // serialises over it — so never trust the decompiled initialiser for
        // an inspector-exposed field; read the [Nav] startup line instead.
        // <= 0 means "leave vanilla's value untouched" — the honest baseline,
        // and immune to Iron Gate re-tuning the prefab in a future build.
        // Lower = a travelling peer's trail of dead tiles is returned to the
        // link pool sooner. Too low = tiles expire while mobs still need them
        // and must be rebuilt, and rebuilds are capped at one per pass, which
        // stalls mob movement. If the [Nav] gauge shows built and freed both
        // high in the same window, that's thrashing — back it off.
        // 2026-09-22 baseline at 30s, 7 peers, nobody fighting: 300-610 tiles,
        // 40-54k stitches (61-82% of the pool), ~110 stitches/tile. Cost is
        // ~50-80 tiles per peer, so ~9-10 peers would exhaust the pool while
        // standing still. Now trying 15f to shrink the idle resident set and
        // leave headroom for fights. Revert to 30f if it doesn't help, or
        // <= 0 for true vanilla (60s).
        // Set to -1 (vanilla 60s) 2026-09-25 to baseline the synthetic load test
        // against the real 8-player session, which ran on all-vanilla dials.
        internal const float NavTileTimeoutSeconds = 15f;

        // Max stale tiles our sweep frees in one maintenance pass.
        // VANILLA DEFAULT: 1 (an artifact of its dictionary loop, not a tuned
        // budget — see Pathfinding_TimeoutTiles_Patch). Set this to 1 to get
        // vanilla behaviour back. Passes run at most 10x/sec, so 64 means up
        // to 640 tiles/sec can be reclaimed. The actual engine-side teardown
        // is queued and throttled by vanilla regardless of this number, so
        // raising it adds bookkeeping, not a frame-time spike.
        // 64 is a safety cap rather than a target: observed peak churn was 173
        // tiles freed per 20s window (~9/sec) against 10 passes/sec, so ~16
        // would do. 64 leaves room for a burst (several peers fast-travelling
        // or logging off at once) at a bounded cost — each freed tile makes
        // vanilla rescan the tile list, so a pass is O(freed x alive) cheap
        // field reads, ~35k at 64 x 550.
        // Set to 1 (vanilla) 2026-09-25 for the same baseline run.
        internal const int NavTileSweepPerPass = 64;

        // Spacing between the stitches that join a tile to its neighbours
        // (Pathfinding.m_linkWidth). VANILLA DEFAULT: 1f — verify against the
        // [Nav] startup line, since the prefab overrode m_tileTimeout.
        // RebuildLinks stitches 2 of the 4 edges at this spacing, so a 32m tile
        // costs ~64 stitches at 1m and ~32 at 2m: doubling this halves every
        // tile's draw on the 65535 pool. That is the biggest lever we have, and
        // unlike the lifetime dial it does not touch how often tiles are built
        // or expired (rebuilds already run at ~50% of their ~200/20s ceiling).
        // The cost is fidelity: crossing points at tile seams are 2m apart, so
        // a mob could fumble a gap narrower than that — a bridge, gate, doorway
        // or stairs. If mobs start pacing at an invisible line, set this back
        // to 1f. <= 0 leaves vanilla's value untouched.
        // 2026-09-24: 2m measured a 63% cut solo (92 -> 51 stitches per tile,
        // 23% -> 7% of the pool) with no change in rebuild rate. Briefly set to
        // vanilla to retest mobs pathing through a swamp-ruin rock (the arrows
        // passing through it cannot be spacing — projectiles use colliders, not
        // the navmesh). Retest verdict: the rock bug reproduced at 1m too, so it
        // is pre-existing and unrelated to spacing — 2m stays.
        // 2026-09-25: temporarily back to vanilla 1m to validate the synthetic
        // peers against the REAL 8-player session (which ran at 1m): 300-610
        // tiles, ~110 stitches/tile, 61-82% of pool, 6337 link failures. If the
        // fake ring reproduces that, the simulator is trustworthy. Set to 2f
        // afterwards — that is the setting we actually want to ship.
        internal const float NavLinkWidth = 2f;

        private float _statsTimer;
        private float _populationTimer;
        private float _nreTimer;
        private float _peerAwareTimer;
        private float _probeTimer;
        // Date the digest last fired, so it emits once per day and not once per
        // frame for the whole minute.
        private DateTime _lastDigestDate = DateTime.MinValue;

        // --- Server tick health (v0.7.1) -----------------------------------
        // A headless server still runs Unity's loop; each pass ("frame") is
        // where mob AI, physics and ZDO sends happen. Tick time is therefore
        // the most direct measure of server-side lag, and unlike HWMonitor it
        // sees inside the process: the 2026-09-22 hardware capture showed CPU
        // never above 43%, which cannot rule out one saturated main thread.
        // Physics is scheduled 20x/sec, so ticks over 50ms mean the server is
        // falling behind its own simulation.
        private const float PerfStatsIntervalSeconds = 20f;
        private const float PerfHitchMs = 100f;
        private float _perfTimer;
        private int _tickCount;
        private int _hitchCount;
        private float _tickTotalMs;
        private float _tickWorstMs;

        internal static ManualLogSource Log;

        /// <summary>
        /// Local wall-clock stamp for our own log lines. BepInEx 5's disk logger
        /// writes no timestamps, and until v0.7.2 we dated our lines by the
        /// neighbouring vanilla ZLog messages (which carry their own) — that
        /// stops working now the per-event diagnostics are gone. Prefixed onto
        /// the periodic gauges and every immediate warning so a report like
        /// "it lagged around 9:40" can be lined up with the log and with an
        /// HWMonitor capture.
        /// </summary>
        internal static string Stamp => DateTime.Now.ToString("HH:mm:ss");
        private Harmony _harmony;

        // One-shot guard for the resolved-simulation-distance line. ZNet does
        // not exist yet in Awake (FejdStartup applies the setting via
        // ZNet.s_onZNetStart), so the authoritative value can only be read once
        // the server is up.
        private bool _loggedSimDistance;
        private float _censusTimer;
        private float _tierTimer;

        private void Awake()
        {
            Log = Logger;

            if (!IsDedicatedServer())
            {
                Log.LogInfo($"{PluginName} not running as dedicated server, patches skipped.");
                return;
            }

            _harmony = new Harmony(PluginGuid);
            ApplyPatches();
            // Count engine-side navmesh link-pool exhaustion. The message is
            // emitted by native Unity code, so there is nothing to patch — we
            // observe the log stream instead and report a count every 20s.
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

            // The options this server was actually launched with. Recorded so a
            // log can be tied to its launch config after the fact — above all
            // -simulationdistance, which sizes every peer's simulated box and
            // therefore how many mobs tick and how many ZDOs become sync
            // candidates. Absent that flag, vanilla applies
            // SimulationDistance.OriginalDistance (near 2, far 2, classic).
            Log.LogMessage($"[Launch] args: {RedactedCommandLine()}");
            Log.LogMessage(DebugSendTiers
                ? $"[Launch] send tiers: {SendTiers.Describe()}"
                : "[Launch] send tiers: DISABLED (vanilla send behaviour)");
        }

        /// <summary>
        /// This process's command line with secrets blanked.
        ///
        /// Redaction matters here: these logs are archived every morning and
        /// pasted around while diagnosing, and -password would otherwise sit in
        /// plaintext in every archive. Valheim takes each option's value as the
        /// NEXT argv entry, so flag the following entry rather than trying to
        /// split on '='.
        /// </summary>
        private static string RedactedCommandLine()
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception e)
            {
                return $"<unavailable: {e.GetType().Name}>";
            }
            if (args == null || args.Length == 0) return "<empty>";

            var sb = new System.Text.StringBuilder();
            bool redactNext = false;
            foreach (var raw in args)
            {
                string a = raw ?? "";
                if (sb.Length > 0) sb.Append(' ');
                if (redactNext)
                {
                    sb.Append("***");
                    redactNext = false;
                    continue;
                }
                sb.Append(a);
                switch (a.ToLowerInvariant())
                {
                    case "-password":
                    case "-joincode":
                        redactNext = true;
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Apply each patch class on its own instead of calling PatchAll().
        ///
        /// PatchAll aborts every REMAINING patch the moment one fails, so a
        /// single vanilla parameter rename leaves the server running
        /// half-patched with no obvious sign beyond a missing session banner.
        /// That has now happened twice: IsInPeerActiveArea in build 25185644,
        /// and ZDOMan.AddPeer on 2026-10-05 (the parameter is `netPeer`, and a
        /// patch bound it as `peer`). Isolating each class means a future
        /// rename costs exactly one feature, and the log names it.
        /// </summary>
        private void ApplyPatches()
        {
            int applied = 0;
            var broken = new List<string>();

            foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                try
                {
                    var patched = _harmony.CreateClassProcessor(type)?.Patch();
                    // Returns null/empty for the many types that carry no
                    // Harmony attributes at all, which is not a failure.
                    if (patched != null && patched.Count > 0) applied++;
                }
                catch (Exception e)
                {
                    broken.Add(type.Name);
                    Log.LogError($"PATCH FAILED — {type.Name}: {e.Message}");
                }
            }

            if (broken.Count > 0)
            {
                // Loud and specific: the feature that class implements is
                // inactive, but everything else is still running.
                Log.LogError(
                    $"{broken.Count} patch class(es) FAILED and their features are INACTIVE: " +
                    string.Join(", ", broken) + $". {applied} applied successfully.");
            }
            else
            {
                Log.LogInfo($"{applied} patch classes applied, none failed.");
            }
        }

        private void OnDestroy()
        {
            // Flush whatever the day accumulated so a clean shutdown doesn't
            // silently discard it. A hard kill still loses the counts.
            if (_harmony != null) DailyDigest.Emit("shutdown");
            _harmony?.UnpatchSelf();
        }

        private void Update()
        {
            if (_harmony == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // Resolved simulation distance, once per session. The launch args
            // above show what was REQUESTED; this shows what vanilla actually
            // applied, which is the number that sizes the per-peer box. Logged
            // separately because an unset flag silently means near 2.
            if (!_loggedSimDistance)
            {
                _loggedSimDistance = true;
                LogSimulationDistance();
            }

            _statsTimer += Time.deltaTime;
            _populationTimer += Time.deltaTime;
            _nreTimer += Time.deltaTime;
            _peerAwareTimer += Time.deltaTime;
            _perfTimer += Time.deltaTime;

            // Sample this tick's duration. unscaledDeltaTime so a timeScale
            // change (we never set one, but mods/console can) can't skew it.
            SendPressure.Sample();

            float tickMs = Time.unscaledDeltaTime * 1000f;
            _tickCount++;
            _tickTotalMs += tickMs;
            if (tickMs > _tickWorstMs) _tickWorstMs = tickMs;
            if (tickMs > PerfHitchMs) _hitchCount++;

            if (_perfTimer >= PerfStatsIntervalSeconds)
            {
                _perfTimer = 0f;
                LogPerfStats();
                _tickCount = 0;
                _hitchCount = 0;
                _tickTotalMs = 0f;
                _tickWorstMs = 0f;
            }

            DuplicateDestroySend.Poll();

            _probeTimer += Time.deltaTime;
            if (_probeTimer >= 1f)
            {
                _probeTimer = 0f;
                TeleportAreaProbe.Poll();
                PlayerInstanceWatch.Poll();
                ZoneLoadingWatch.Poll();
            }

            if (DebugSendCensusSeconds > 0f)
            {
                _censusTimer += Time.deltaTime;
                if (_censusTimer >= DebugSendCensusSeconds)
                {
                    SendCensus.Emit(_censusTimer);
                    _censusTimer = 0f;
                }
            }

            // Tier accounting AND pruning. Runs even when printing is off,
            // capped at 30s, because the prune is what bounds the per-peer map;
            // leaving it to a disabled log would let the map grow unbounded.
            if (DebugSendTiers)
            {
                _tierTimer += Time.deltaTime;
                float due = DebugSendTierSeconds > 0f
                    ? DebugSendTierSeconds
                    : 30f;
                if (_tierTimer >= due)
                {
                    SendTiers.EmitAndPrune(_tierTimer, DebugSendTierSeconds > 0f);
                    _tierTimer = 0f;
                }
            }

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
            // Daily digest (v0.7.2). Fires once when local time reaches
            // DigestHour:DigestMinute; _lastDigestDate stops it repeating for
            // the rest of that minute.
            var localNow = DateTime.Now;
            if (localNow.Date != _lastDigestDate
                && localNow.Hour == DigestHour && localNow.Minute >= DigestMinute)
            {
                _lastDigestDate = localNow.Date;
                DailyDigest.Emit("daily");
            }
        }

        /// <summary>
        /// The simulation distance vanilla actually applied, plus what it means
        /// geometrically. Zones are 64m, and a peer's simulated box spans
        /// +/-near zones around their own zone — so the count of zones (the
        /// AREA) scales as the square of `near`, while the reach (a LINEAR
        /// measure) scales with it directly. Both are printed because they are
        /// easy to conflate: near 2 -> 1 cuts reach by 40% but zones by 64%.
        ///
        /// Non-classic trims the box corners with a radius test, so the zone
        /// count is computed rather than assumed to be span^2.
        /// </summary>
        private void LogSimulationDistance()
        {
            var sd = ServerCoverage.GetSimulationDistance();
            int near = sd.NearSimulationDistance;
            int span = 2 * near + 1;

            int zones = span * span;
            if (!sd.IsClassic && ZoneSystem.instance != null)
            {
                zones = 0;
                var origin = new Vector2s(0, 0);
                for (int y = -near; y <= near; y++)
                {
                    for (int x = -near; x <= near; x++)
                    {
                        if (ZoneSystem.instance.ZonesWithinRadius(origin, new Vector2s(x, y), near))
                            zones++;
                    }
                }
            }

            float zoneSize = ZoneSystem.instance != null ? ZoneSystem.instance.m_zoneSize : 64f;
            float axisReach = near * zoneSize + zoneSize * 0.5f;

            Log.LogMessage(
                $"[Launch] simulation distance: near={near} far={sd.FarSimulationDistance} " +
                $"classic={sd.IsClassic} → {zones} zones per peer " +
                $"({span}x{span} box, {span * zoneSize:F0}m across, " +
                $"{axisReach:F0}m axis reach from zone centre). " +
                $"Caps every client at min(client, server).");
        }

        /// <summary>
        /// v0.7.1: server tick health + per-peer network backlog, every 20s.
        ///   [Perf] tick avg=Nms worst=Nms hitches>100ms=N/N | sendQueue max=NKB (name) | peers=N
        ///
        /// Written to separate three causes of "the server feels laggy" that
        /// we have so far only been able to guess between:
        ///   * tick avg/worst climbing during fights → the server itself is
        ///     stalling (CPU, or navmesh link exhaustion — see NavMeshPressure)
        ///   * ticks fine but sendQueue growing → network-bound, which would
        ///     point at the relayed -crossplay transport
        ///   * both clean while players still report lag → client-side or
        ///     latency, and none of our server-side tuning will help
        /// sendQueue is vanilla's own backpressure signal: ZDOMan throttles ZDO
        /// sends on it, so a large queue means peers are being starved.
        /// </summary>
        private void LogPerfStats()
        {
            if (_tickCount == 0) return;

            int peers = 0;
            if (ZNet.instance != null)
            {
                foreach (var peer in ZNet.instance.GetPeers())
                {
                    if (peer?.m_socket != null) peers++;
                }
            }

            // Queue stats now come from SendPressure, which samples at 4Hz across
            // the whole window — a single reading at log time missed the spikes
            // that matter, since a peer is only starved while it is over 10KB.
            string queueSummary = SendPressure.Drain(PerfStatsIntervalSeconds)
                                  ?? $"sendQueue max=0KB (-) starved=0/0";

            float avgMs = _tickTotalMs / _tickCount;
            string line =
                $"[{Plugin.Stamp}] [Perf] tick avg={avgMs:F1}ms worst={_tickWorstMs:F0}ms " +
                $"hitches>{PerfHitchMs:F0}ms={_hitchCount}/{_tickCount} | " +
                $"{queueSummary} | peers={peers}";

            // Physics ticks 20x/sec; a 50ms average means the server is behind.
            if (avgMs > 50f || _hitchCount > 0) Log.LogWarning(line);
            else Log.LogInfo(line);

            // Companion line: where that tick time went across our own passes.
            // Anything unaccounted for is vanilla's (mob AI, physics, saves).
            var passes = PassTimer.DrainSummary(_tickTotalMs);
            var reclaims = ReclaimStats.Drain(peers);
            var sync = SyncVolume.DrainStats(peers, PerfStatsIntervalSeconds);
            var terrain = TerrainComp_DoOperation_Census_Patch.Drain();
            var proj = ZDOMan_ServerSortSendZDOS_ProjectilePriority_Patch.Drain();
            var rpcd = ZNetView_HandleRoutedRPC_Diag_Patch.Drain();
            var cdp = CreateDestroyPhases.Drain();
            if (cdp != null) Log.LogInfo($"[{Stamp}] [Diag] {cdp}");
            var dsp = DungeonSpawnTimer.Drain();
            if (dsp != null) Log.LogInfo($"[{Stamp}] [Diag] {dsp}");
            var zb = ZoneSystem_IsZoneReadyForType_Diag_Patch.Drain();
            if (zb != null) Log.LogWarning($"[{Stamp}] [Diag] {zb}");
            var dw = ZRoutedRpc_HandleRoutedRPC_DeadWindow_Patch.Drain();
            if (dw != null) Log.LogWarning($"[{Stamp}] [Diag] {dw}");
            var cs = ZNetScene_CreateObjectsSorted_Diag_Patch.Drain();
            if (cs != null) Log.LogInfo($"[{Stamp}] [Diag] {cs}");
            var dds = DuplicateDestroySend.Drain();
            if (dds != null) Log.LogInfo($"[{Stamp}] [Diag] {dds}");
            var gfx = GhostDestroyRetry.Drain();
            if (gfx != null) Log.LogWarning($"[{Stamp}] [Diag] {gfx}");
            var tap = TeleportAreaProbe.Drain();
            if (tap != null) Log.LogInfo($"[{Stamp}] [Diag] {tap}");
            var nob = PlayerInstanceWatch.Drain();
            if (nob != null) Log.LogWarning($"[{Stamp}] [Diag] {nob}");
            var zlw = ZoneLoadingWatch.Drain();
            if (zlw != null) Log.LogInfo($"[{Stamp}] [Diag] {zlw}");
            if (passes != null || reclaims != null || sync != null || terrain != null || proj != null || rpcd != null)
                Log.LogInfo($"[{Stamp}] [Pass] {passes} | {reclaims} | {sync}" +
                            (terrain != null ? $" | {terrain}" : "") +
                            (proj != null ? $" | {proj}" : "") +
                            (rpcd != null ? $" | {rpcd}" : ""));
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

            // Drain Container reclaim counters (same three-bucket split).
            int cUnowned = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimUnowned, 0);
            int cLive = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimLivePeer, 0);
            int cAbsent = System.Threading.Interlocked.Exchange(
                ref ZDOMan_ReleaseZDOS_Patch.s_containerReclaimAbsentPeer, 0);
            // v0.7.2: these used to log every 60s (53 windows in one session).
            // Roll them into the daily digest instead — the stuck-chest and
            // failed-pickup bugs are still open, so the counts stay, the noise
            // doesn't. A livePeer reclaim is the candidate cause for both.
            DailyDigest.RecordReclaims(cLive, cUnowned, cAbsent, idLive, idUnowned, idAbsent);

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
            int evScoped = SpawnSystem_UpdateSpawning_Patch.s_eventSpawnScopedOut;
            if (eventInv > 0 || evScoped > 0)
            {
                Log.LogInfo(
                    $"[{Plugin.Stamp}] [Diag] SpawnSystem invocations in last {PeerAwareSummaryIntervalSeconds:F0}s: " +
                    $"biome={biomeInv} event={eventInv} eventScopedOut={System.Threading.Interlocked.Exchange(ref SpawnSystem_UpdateSpawning_Patch.s_eventSpawnScopedOut, 0)}.");
            }
        }

        private static void FlushSuppressedNRECount()
        {
            int caught = System.Threading.Interlocked.Exchange(
                ref SpawnSystem_UpdateSpawning_Patch.s_suppressedNRECount, 0);
            if (caught > 0)
            {
                Log.LogWarning($"[{Plugin.Stamp}] [SpawnSystem] Suppressed {caught} NRE(s) from vanilla UpdateSpawnList " +
                    $"in last {NRESummaryIntervalSeconds:F0}s (likely a race we haven't covered yet — investigate if sustained).");
            }
        }

        private static void LogMobPopulation()
        {
            var characters = Character.GetAllCharacters();
            if (characters == null || characters.Count == 0)
            {
                Log.LogInfo($"[{Plugin.Stamp}] [Population] No characters active.");
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
                $"[{Plugin.Stamp}] [Population] {mobTotal} mob(s) across {counts.Count} type(s)");
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
                Log.LogInfo($"[{Plugin.Stamp}] [Coverage] 0 peers connected — no zones active.");
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
                $"[{Plugin.Stamp}] [Coverage] {sectors.Count} sectors owned by server across {peerCount} peer(s). " +
                $"Sector bbox: x[{minX}..{maxX}] z[{minY}..{maxY}]. " +
                $"World bbox: x[{worldMinX:F0}..{worldMaxX:F0}] z[{worldMinZ:F0}..{worldMaxZ:F0}] meters.");

            // Log each peer's position AND active-area box. Each ±m_activeArea
            // box is what the server treats as an independent reference for
            // gameplay control (owning ZDOs, running physics, allowing spawns
            // near that peer). NOT related to the vanilla centroid — server
            // does not gate on a single centroid box any more (see
            // ZoneSystem_IsActiveAreaLoaded_Patch and the two
            // ZNetScene_OutsideActiveArea_*_Patches).
            var sb = new System.Text.StringBuilder($"[{Plugin.Stamp}] [Coverage] Peer positions: ");
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
                AddSectorBox(ZoneSystem.GetZone(peer.GetRefPos()), near, simDist);
            }
            // Synthetic load-test peers: the FULL ring, including zones that
            // haven't loaded yet — this is the loop that loads them. Using the
            // zone-loaded-only list here would deadlock (a zone can't load until
            // it's loaded), and leaving synthetic peers out entirely is what let
            // ZNetScene create objects in areas with no terrain on 2026-09-25,
            // dropping physics objects through the world.
            foreach (var pos in SyntheticPeers.GetPositionsForZoneLoading())
            {
                AddSectorBox(ZoneSystem.GetZone(pos), near, simDist);
            }
            return s_activeSectors;
        }

        private static void AddSectorBox(Vector2s center, int near, SimulationDistance simDist)
        {
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
            // Synthetic load-test peers count as active areas too, otherwise
            // their zones would load but spawns and physics would stay gated.
            foreach (var pos in SyntheticPeers.GetPositions())
            {
                if (ZNetScene.InActiveArea(point, pos)) return true;
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
            foreach (var pos in SyntheticPeers.GetPositions())
            {
                if (s_seenPeerSectors.Add(ZoneSystem.GetZone(pos)))
                {
                    s_uniquePeerPositions.Add(pos);
                }
            }
            return s_uniquePeerPositions;
        }
    }

    /// <summary>
    /// v0.7.3: synthetic peers — an 8-player load test without 8 players.
    ///
    /// Everything expensive about extra players flows from one list of
    /// positions (ServerCoverage.GetUniquePeerPositions): which zones load,
    /// which ZDOs the server instantiates and owns, where mobs may spawn, and
    /// therefore how many navmesh tiles and stitches exist. Adding fake
    /// positions to that list reproduces the simulation load faithfully.
    ///
    /// Shape (v0.7.6): Plugin.DebugSyntheticPeerCount positions, placed once at
    /// random ON LAND within [MinRadius, MaxRadius] of the character named in
    /// Plugin.DebugSyntheticPeerAnchor, then each drifting on its own heading at
    /// DriftSpeed and turning when it meets water.
    ///
    /// The first version followed the anchor as a fixed ring, which was wrong in
    /// two ways the 2026-09-25 validation run exposed: several points sat on
    /// open water (nearly free — no walkable ground, almost no spawns), and all
    /// areas jumped in lockstep every 32m the anchor walked, so tiles churned
    /// far harder than real players cause. The result was 472-843 tiles at only
    /// 20-30 stitches each, versus 300-610 at ~110 in a real 8-player session:
    /// tiles created and discarded before navmesh could be built in them.
    ///
    /// Placement stays anchored to the tester's region so they can still choose
    /// somewhere clear of bases; at 600-1400m out, with areas reaching ~160m
    /// past their centre, stay ~1.6km clear of anything you care about.
    ///
    /// Off unless the anchor name is set. It also switches itself off when that
    /// player logs out, so no restart is needed to stop a test.
    ///
    /// What this does NOT reproduce: network load. Fake peers have no socket,
    /// so per-peer sends, bandwidth and sendQueue stay untested. They also
    /// never receive ZDO ownership — IsLivePeer only knows real uids — which is
    /// what we want, since the server owning everything is the case under test.
    /// </summary>
    internal static class SyntheticPeers
    {
        private static readonly List<Vector3> s_positions = new List<Vector3>();
        private static bool s_wasArmed;

        // Recompute cache. GetPositions is called from
        // IsPointInsideAnyPeerActiveArea, which vanilla hits once per object per
        // frame (WearNTear, StaticPhysics, SpawnArea) — so the land search must
        // NOT run per call, or the load test would itself become the slowdown we
        // are trying to measure. Refresh only when the anchor has moved half a
        // zone or the cache is a couple of seconds old; neither matters for a
        // 320m area, and it also keeps the ring from jittering.
        private static readonly List<Vector3> s_cached = new List<Vector3>();
        private static Vector3 s_cachedAnchor;
        private static float s_cachedAt = float.NegativeInfinity;
        private static int s_cachedOnWater;
        private const float CacheMaxAgeSeconds = 2f;
        private const float CacheMoveThreshold = 32f;

        // Staged activation. 2026-09-25: arming all 7 at once gave the server 7
        // cold areas in one frame — it began creating objects there before the
        // terrain existed (51 "could not find hmap"), physics objects fell
        // through the missing ground, and one tick stalled 1316ms. Bringing them
        // up one at a time lets zone loading (one zone per tick per position)
        // keep pace.
        private const float PeerRampSeconds = 5f;
        private static float s_armedAt = float.NegativeInfinity;

        private static readonly List<Vector3> s_loadingPositions = new List<Vector3>();
        private static readonly List<Vector3> s_cachedEmpty = new List<Vector3>();
        private static readonly List<Vector3> s_live = new List<Vector3>();
        private static readonly List<float> s_headings = new List<float>();
        private static readonly List<float> s_speeds = new List<float>();
        private static readonly List<bool> s_roaming = new List<bool>();
        private static readonly List<float> s_stateUntil = new List<float>();

        // Bout lengths, chosen so peers are stationary roughly half the time —
        // a rough stand-in for time spent in a base, crafting or fighting.
        private const float RoamSecondsMin = 20f;
        private const float RoamSecondsMax = 60f;
        private const float IdleSecondsMin = 30f;
        private const float IdleSecondsMax = 120f;

        internal static bool Armed => !string.IsNullOrEmpty(Plugin.DebugSyntheticPeerAnchor)
                                      && Plugin.DebugSyntheticPeerCount > 0;

        internal static int Count => s_positions.Count;

        /// <summary>
        /// Every ring position, whether or not its zone has loaded yet. Only for
        /// zone loading (ZoneSystem_Update_Patch) — the zones have to be allowed
        /// to generate before anything else treats the area as live.
        /// </summary>
        internal static List<Vector3> GetPositionsForZoneLoading()
        {
            s_loadingPositions.Clear();
            s_loadingPositions.AddRange(BuildPeerPositions());
            return s_loadingPositions;
        }

        /// <summary>
        /// Ring positions for this tick, or an empty list when disarmed or the
        /// anchor is offline. Recomputed per call so the ring tracks the anchor.
        /// </summary>
        internal static List<Vector3> GetPositions()
        {
            s_positions.Clear();
            var ring = BuildPeerPositions();
            if (ring.Count == 0) return s_positions;

            // Only hand out positions whose zone has actually loaded. Without
            // this, ZNetScene creates objects and StaticPhysics runs in an area
            // with no terrain yet, and physics objects fall through the world
            // (observed 2026-09-25). Zone loading itself uses
            // GetPositionsForZoneLoading, so the ground still gets generated —
            // this only delays everything that treats the area as live.
            var zs = ZoneSystem.instance;
            foreach (var pos in ring)
            {
                if (zs == null || zs.IsZoneLoaded(ZoneSystem.GetZone(pos)))
                    s_positions.Add(pos);
            }
            return s_positions;
        }

        /// <summary>
        /// The fake peers themselves: anchor lookup and guards, one-time random
        /// placement on land, then independent drift.
        ///
        /// v0.7.6 replaced the follow-the-anchor ring. That ring moved all 7
        /// areas in lockstep every 32m the anchor walked, which churned tiles
        /// far harder than real players do: the 2026-09-25 validation run showed
        /// 472-843 tiles at only 20-30 stitches each (real 8-player sessions run
        /// 300-610 tiles at ~110), because tiles were created and thrown away
        /// before the game could build navmesh in them. Several ring points also
        /// landed on open water, which is nearly free.
        ///
        /// Now: placed once at random on land within an annulus around the
        /// anchor (so the tester still chooses the region and can keep it clear
        /// of bases), then each peer walks its own heading at its own speed and
        /// turns when it hits water. That is much closer to 8 independent
        /// players wandering.
        /// </summary>
        private static List<Vector3> BuildPeerPositions()
        {
            if (!Armed || ZNet.instance == null) return s_cachedEmpty;

            Vector3? anchor = null;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                if (!string.Equals(peer.m_playerName, Plugin.DebugSyntheticPeerAnchor,
                                   StringComparison.OrdinalIgnoreCase)) continue;
                // GUARD (2026-09-25): a freshly connected peer has no position
                // yet — m_refPos is still Vector3.zero and m_characterID is None.
                // Taking that at face value planted everything at world(0,0),
                // the middle of the map, and objects fell out of the world there.
                if (peer.m_characterID == ZDOID.None) break;
                anchor = peer.GetRefPos();
                break;
            }

            if (!anchor.HasValue)
            {
                if (s_wasArmed)
                {
                    s_wasArmed = false;
                    s_armedAt = float.NegativeInfinity;
                    // Clear all five parallel lists together — they are indexed
                    // in lockstep, so a partial reset would misalign speeds and
                    // bout timers against positions on the next placement.
                    s_cached.Clear();
                    s_headings.Clear();
                    s_speeds.Clear();
                    s_roaming.Clear();
                    s_stateUntil.Clear();
                    Plugin.Log.LogWarning(
                        $"[{Plugin.Stamp}] [SynthPeers] anchor '{Plugin.DebugSyntheticPeerAnchor}' " +
                        "offline or unpositioned — synthetic load OFF.");
                }
                return s_cachedEmpty;
            }

            int count = Plugin.DebugSyntheticPeerCount;
            if (s_cached.Count == 0) PlaceOnLand(anchor.Value, count);
            if (s_cached.Count == 0) return s_cachedEmpty;

            if (!s_wasArmed)
            {
                s_wasArmed = true;
                var where = new System.Text.StringBuilder();
                foreach (var p in s_cached) where.Append($"({p.x:F0},{p.z:F0}) ");
                Plugin.Log.LogWarning(
                    $"[{Plugin.Stamp}] [SynthPeers] LOAD TEST ACTIVE: placed {s_cached.Count}/{count} " +
                    $"synthetic peers on land {Plugin.DebugSyntheticPeerMinRadius:F0}-" +
                    $"{Plugin.DebugSyntheticPeerMaxRadius:F0}m from '{Plugin.DebugSyntheticPeerAnchor}' " +
                    $"at world({anchor.Value.x:F0},{anchor.Value.z:F0}), drifting {Plugin.DebugSyntheticPeerDriftSpeed:F0}m/s, " +
                    $"ramping one every {PeerRampSeconds:F0}s. At: {where}" +
                    "| [Nav]/[Perf] readings in this state are NOT baseline.");
            }

            if (s_armedAt == float.NegativeInfinity) s_armedAt = Time.time;
            // Staged: one more peer every PeerRampSeconds so zone loading keeps up.
            int live = Mathf.Clamp(
                (int)((Time.time - s_armedAt) / PeerRampSeconds) + 1, 1, s_cached.Count);

            if (Time.time - s_cachedAt >= CacheMaxAgeSeconds)
            {
                Drift(live, Time.time - s_cachedAt);
                s_cachedAt = Time.time;
            }

            s_live.Clear();
            for (int i = 0; i < live; i++) s_live.Add(s_cached[i]);
            return s_live;
        }

        /// <summary>
        /// Pick count positions at random on land, within
        /// [MinRadius, MaxRadius] of the anchor and at least 320m apart so their
        /// simulated areas don't overlap. Uses WorldGenerator (procedural), so
        /// candidate spots don't need to be loaded to be tested.
        /// </summary>
        private static void PlaceOnLand(Vector3 anchor, int count)
        {
            var world = WorldGenerator.instance;
            var zs = ZoneSystem.instance;
            if (world == null || zs == null) return;

            float minHeight = zs.m_waterLevel + 2f;
            float minR = Plugin.DebugSyntheticPeerMinRadius;
            float maxR = Plugin.DebugSyntheticPeerMaxRadius;
            s_cached.Clear();
            s_headings.Clear();
            s_speeds.Clear();
            s_roaming.Clear();
            s_stateUntil.Clear();

            for (int i = 0; i < count; i++)
            {
                Vector3 chosen = Vector3.zero;
                bool found = false;
                for (int attempt = 0; attempt < 400 && !found; attempt++)
                {
                    float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                    float radius = UnityEngine.Random.Range(minR, maxR);
                    var candidate = new Vector3(
                        anchor.x + Mathf.Cos(angle) * radius,
                        anchor.y,
                        anchor.z + Mathf.Sin(angle) * radius);
                    if (world.GetHeight(candidate.x, candidate.z) <= minHeight) continue;
                    // Relax the separation requirement if the region is tight,
                    // rather than failing to place a peer at all.
                    float required = attempt < 300 ? 320f : 160f;
                    bool clear = true;
                    foreach (var other in s_cached)
                    {
                        if (Utils.DistanceXZ(candidate, other) < required) { clear = false; break; }
                    }
                    if (!clear) continue;
                    chosen = candidate;
                    found = true;
                }
                if (!found) continue;
                s_cached.Add(chosen);
                s_headings.Add(UnityEngine.Random.Range(0f, Mathf.PI * 2f));
                s_speeds.Add(UnityEngine.Random.Range(
                    Plugin.DebugSyntheticPeerDriftSpeed * 0.6f,
                    Plugin.DebugSyntheticPeerDriftSpeed * 1.6f));
                // Stagger the first bout so they do not all start and stop together.
                s_roaming.Add(UnityEngine.Random.value < 0.5f);
                s_stateUntil.Add(Time.time + UnityEngine.Random.Range(5f, RoamSecondsMax));
            }
            s_cachedAt = Time.time;
        }

        /// <summary>
        /// Move each live peer the way a player actually moves: alternating
        /// bouts of roaming and standing still, always on land.
        ///
        /// v0.7.7. Constant drift produced the right number of tiles but only
        /// half the stitches of a real 8-player session (54/tile vs ~110, while
        /// this world's solo runs average 92) — because tiles were freed three
        /// times faster than they were built, so the average tile never finished
        /// being stitched. Real players spend a lot of time stationary: in a
        /// base, crafting, fighting, or just standing. Idle bouts let an area's
        /// tiles complete, which is what makes stitch counts realistic.
        ///
        /// Water is never entered: a step onto water is refused and the peer
        /// turns instead, so they behave like walkers, not swimmers or boats.
        /// </summary>
        private static void Drift(int live, float dt)
        {
            var world = WorldGenerator.instance;
            var zs = ZoneSystem.instance;
            if (world == null || zs == null) return;
            float minHeight = zs.m_waterLevel + 2f;

            for (int i = 0; i < live && i < s_cached.Count; i++)
            {
                // Flip between roaming and standing still when this bout expires.
                if (Time.time >= s_stateUntil[i])
                {
                    s_roaming[i] = !s_roaming[i];
                    s_stateUntil[i] = Time.time + (s_roaming[i]
                        ? UnityEngine.Random.Range(RoamSecondsMin, RoamSecondsMax)
                        : UnityEngine.Random.Range(IdleSecondsMin, IdleSecondsMax));
                    if (s_roaming[i])
                    {
                        // New destination-ish heading and pace for this bout.
                        s_headings[i] = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                        s_speeds[i] = UnityEngine.Random.Range(
                            Plugin.DebugSyntheticPeerDriftSpeed * 0.6f,
                            Plugin.DebugSyntheticPeerDriftSpeed * 1.6f);
                    }
                }
                if (!s_roaming[i]) continue;

                var pos = s_cached[i];
                float heading = s_headings[i];
                var next = new Vector3(
                    pos.x + Mathf.Cos(heading) * s_speeds[i] * dt,
                    pos.y,
                    pos.z + Mathf.Sin(heading) * s_speeds[i] * dt);

                if (world.GetHeight(next.x, next.z) > minHeight)
                {
                    s_cached[i] = next;
                    // Gentle wander so they don't march in perfect straight lines.
                    s_headings[i] = heading + UnityEngine.Random.Range(-0.2f, 0.2f);
                }
                else
                {
                    s_headings[i] = heading + UnityEngine.Random.Range(1.5f, 4.7f);  // turn away
                }
            }
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
        // v0.7.55: scoped override so object CREATION sorts by distance to the
        // peer whose slice is being built, not to the centroid.
        //
        // CreateObjectsSorted seeds its sort with GetReferencePosition():
        //     Vector3 referencePosition = ZNet.instance.GetReferencePosition();
        //     ... m_tempSortValue = DistanceSqr(referencePosition, zdo.GetPosition())
        // With our centroid that origin is a geometric average that may sit near
        // nobody, so creation order suited no actual player. Slicing per peer
        // stops peers competing, but the ordering WITHIN a slice stays
        // centroid-biased unless the origin moves too.
        //
        // Scope is deliberately tiny: set immediately before the CreateObjects
        // call and cleared in a finally. Inside that call the only meaningful
        // consumer is the sort — IsActiveAreaLoaded is forced true by our own
        // patch and IsZoneReadyForType does not read a position. Leaving this
        // set would hand a stale single-peer position to every consumer in
        // [[valheim-reference-position-consumers]], hence the finally.
        private static bool s_overrideActive;
        private static Vector3 s_overridePos;

        internal static void BeginOverride(Vector3 pos)
        {
            s_overridePos = pos;
            s_overrideActive = true;
        }

        internal static void EndOverride() => s_overrideActive = false;

        static bool Prefix(ZNet __instance, ref Vector3 __result)
        {
            if (__instance == null || !__instance.IsServer())
                return true;

            if (s_overrideActive) { __result = s_overridePos; return false; }

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
        private static readonly MethodInfo IsInsideEventAreaMethod =
            AccessTools.Method(typeof(RandEventSystem), "IsInsideRandomEventArea",
                new[] { typeof(RandomEvent), typeof(UnityEngine.Vector3) });
        private static readonly FieldInfo ActiveEventField =
            AccessTools.Field(typeof(RandEventSystem), "m_activeEvent");
        internal static int s_eventSpawnScopedOut;

        // v0.7.52 (bug #7): is THIS zone inside the active event's area?
        // Uses vanilla's own geometry rather than a radius of our own, so the
        // boundary matches exactly what a client would compute.
        //
        // Fails OPEN on any reflection or state surprise: if we cannot answer
        // the question we let vanilla's behaviour stand, because the failure
        // mode of a false negative is an invasion that never arrives, which is
        // worse than the bug we are fixing.
        private static bool ZoneInActiveEventArea(UnityEngine.Vector3 zonePos)
        {
            if (!Plugin.DebugScopeEventSpawns) return true;
            var res = RandEventSystem.instance;
            if (res == null || IsInsideEventAreaMethod == null || ActiveEventField == null) return true;
            var active = ActiveEventField.GetValue(res) as RandomEvent;
            if (active == null) return true;
            try
            {
                return (bool)IsInsideEventAreaMethod.Invoke(res, new object[] { active, zonePos });
            }
            catch
            {
                return true;
            }
        }

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
            if (currentSpawners != null && ZoneInActiveEventArea(__instance.transform.position))
            {
                System.Threading.Interlocked.Increment(ref SpawnDiag.s_eventSpawnInvocations);
                InvokeUpdateSpawnList(__instance, currentSpawners, time, eventSpawners: true, "e_");
            }
            else if (currentSpawners != null)
                System.Threading.Interlocked.Increment(ref s_eventSpawnScopedOut);

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
            // v0.7.71: the pheromone spawn/level overrides are applied for the
            // duration of this call only, and restored in the finally. These
            // are shared, persistent SpawnData objects — leaving one modified
            // would change the world permanently.
            PheromoneSpawnOverride.Begin(
                spawners, (instance != null) ? instance.transform.position : Vector3.zero);
            try
            {
                UpdateSpawnListMethod.Invoke(
                    instance, new object[] { spawners, time, eventSpawners, groupSalt });
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException)
            {
                System.Threading.Interlocked.Increment(ref s_suppressedNRECount);
            }
            finally
            {
                PheromoneSpawnOverride.End();
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
        //
        // v0.7.55: creation and removal now use SEPARATE lists, and the naming
        // is deliberate. Handing a per-peer SLICE to RemoveObjects would destroy
        // every other peer's objects, permanently for non-persistent ones, so
        // "slice" and "union" never share a variable.
        private static readonly List<ZDO> s_nearBuffer = new List<ZDO>();
        private static readonly List<ZDO> s_distantBuffer = new List<ZDO>();
        private static readonly HashSet<ZDO> s_seenNear = new HashSet<ZDO>();
        private static readonly HashSet<ZDO> s_seenDistant = new HashSet<ZDO>();

        // Creation slice: ONE peer's area, rebuilt fresh every tick.
        private static readonly List<ZDO> s_sliceNear = new List<ZDO>();
        private static readonly List<ZDO> s_sliceDistant = new List<ZDO>();

        private static int s_frame;
        private static int s_rrCursor;          // which peer's turn it is
        private static bool s_unionBuiltThisTick;

        /// <summary>
        /// v0.7.55: round-robin creation, strided removal.
        ///
        /// Before: the entire pass ran on one tick in DebugCreateDestroyStride
        /// (8), walking every peer's sectors and then creating and removing from
        /// that union. All the work landed in one spike, and the per-pass
        /// creation budget - max(uncreated/100, 100) - was spent on a
        /// CENTROID-sorted list, so whichever peer happened to sit nearest the
        /// centroid took most of it.
        ///
        /// Now the two phases are decoupled, because they want opposite things:
        ///
        ///   CREATION  - every tick, ONE peer's freshly walked slice. Each peer
        ///               still comes up every N ticks, but gets the whole budget
        ///               floor of 100 on its turn instead of an unfair share of
        ///               a shared pass. Estimated per-peer throughput at 8 peers
        ///               goes from ~91 objects/sec to ~350. Slicing is safe here:
        ///               CreateObjects only instantiates, it never destroys.
        ///
        ///   REMOVAL   - unchanged cadence (every Nth tick), on a freshly walked
        ///               FULL union. RemoveObjects is a whitelist: it destroys
        ///               every live instance absent from the lists it is handed,
        ///               and permanently destroys non-persistent ones it owns.
        ///               It must never see a slice, and its frequency is left
        ///               alone so this change cannot regress its cost.
        ///
        /// Rejected design: a persistent union refreshed one slice per tick.
        /// ZDOs are POOLED (ZDOPool.Release in ZDOMan.HandleDestroyedZDO), so a
        /// ZDO reference held across ticks can come back pointing at a different
        /// object. Rebuilding both lists fresh avoids that entirely.
        ///
        /// DebugRoundRobinCreate = false restores the previous single-spike
        /// behaviour exactly.
        /// </summary>
        static bool Prefix(ZNetScene __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            s_frame++;
            s_unionBuiltThisTick = false;
            PheromoneServerSide.MaybeScan();

            var simDist = ServerCoverage.GetSimulationDistance();
            var zdoMan = ZDOMan.instance;
            bool removalTick = Plugin.DebugCreateDestroyStride <= 1
                               || s_frame % Plugin.DebugCreateDestroyStride == 0;

            if (!Plugin.DebugRoundRobinCreate)
            {
                // Legacy path: one spike per stride, both phases on the union.
                if (!removalTick) return false;
                long tW0 = CreateDestroyPhases.Now;
                BuildUnion(__instance, zdoMan, simDist,
                           out var legacyNear, out var legacyDistant);
                long tC0 = CreateDestroyPhases.Now;
                ReflectionCache.ZNetScene_CreateObjects.Invoke(
                    __instance, new object[] { legacyNear, legacyDistant });
                long tR0 = CreateDestroyPhases.Now;
                InvokeRemoveWithUnion(__instance, legacyNear, legacyDistant);
                CreateDestroyPhases.Record(tW0, tC0, tR0, CreateDestroyPhases.Now,
                    ZNet.instance.GetPeers().Count, legacyNear.Count,
                    legacyDistant.Count, CreateDestroyPhases.LiveInstances(__instance));
                return false;
            }

            // ---- PHASE 1: creation, one peer per tick -----------------------
            long tWalk = CreateDestroyPhases.Now;
            long tCreate;
            int sliceNear = 0, sliceDistant = 0;

            // GetUniquePeerPositions returns a SHARED static list that it clears
            // and refills per call, so read what we need immediately and do not
            // hold it across the removal phase.
            var positions = ServerCoverage.GetUniquePeerPositions();
            if (positions.Count > 0)
            {
                // Peer counts change as people join and leave, so clamp rather
                // than assuming the cursor is still in range.
                if (s_rrCursor >= positions.Count) s_rrCursor = 0;
                var pos = positions[s_rrCursor];
                s_rrCursor = (s_rrCursor + 1) % positions.Count;

                s_sliceNear.Clear();
                s_sliceDistant.Clear();
                zdoMan.FindSectorObjects(ZoneSystem.GetZone(pos), simDist,
                                         s_sliceNear, s_sliceDistant);
                sliceNear = s_sliceNear.Count;
                sliceDistant = s_sliceDistant.Count;
                tCreate = CreateDestroyPhases.Now;
                // Sort creation by distance to THIS peer, not the centroid.
                // finally is load-bearing: leaving the override set would feed a
                // single peer's position to every reference-position consumer.
                var gatedNear = GateInteriors(__instance, s_sliceNear, pos);
                ZNet_GetReferencePosition_Patch.BeginOverride(pos);
                try
                {
                    ReflectionCache.ZNetScene_CreateObjects.Invoke(
                        __instance, new object[] { gatedNear, s_sliceDistant });
                }
                finally
                {
                    ZNet_GetReferencePosition_Patch.EndOverride();
                }
            }
            else
            {
                tCreate = CreateDestroyPhases.Now;
            }

            // ---- PHASE 2: removal, strided, FULL union ----------------------
            long tRemove = CreateDestroyPhases.Now;
            if (removalTick)
            {
                BuildUnion(__instance, zdoMan, simDist, out var unionNear, out var unionDistant);
                InvokeRemoveWithUnion(__instance, unionNear, unionDistant);
                CreateDestroyPhases.Record(tWalk, tCreate, tRemove, CreateDestroyPhases.Now,
                    ZNet.instance.GetPeers().Count, unionNear.Count,
                    unionDistant.Count, CreateDestroyPhases.LiveInstances(__instance));
            }
            else
            {
                CreateDestroyPhases.Record(tWalk, tCreate, tRemove, tRemove,
                    ZNet.instance.GetPeers().Count, sliceNear, sliceDistant,
                    CreateDestroyPhases.LiveInstances(__instance));
            }
            return false;
        }


        // ---- v0.7.91: interior creation gate (bug #31) -------------------
        // Prefab hash -> does this prefab carry a DungeonGenerator. Populated
        // lazily; the set is tiny but the slice is thousands of ZDOs a tick,
        // so the lookup has to be O(1) and must not call GetComponent twice.
        private static readonly Dictionary<int, bool> s_isDungeonPrefab =
            new Dictionary<int, bool>();
        // Per-tick memo of "is this zone covered by the peer we are slicing
        // for". Decided ONCE PER ZONE, never per ZDO: a dungeon's contents are
        // spread across the interior radius and would otherwise straddle the
        // boundary, creating some mobs with no floor under them.
        private static readonly Dictionary<Vector2s, bool> s_zoneCovered =
            new Dictionary<Vector2s, bool>();
        private static readonly List<ZDO> s_gatedSlice = new List<ZDO>();

        private static bool HasDungeonGenerator(ZNetScene scene, int prefabHash)
        {
            if (s_isDungeonPrefab.TryGetValue(prefabHash, out bool known)) return known;
            var pf = scene.GetPrefab(prefabHash);
            bool has = pf != null && pf.GetComponent<DungeonGenerator>() != null;
            s_isDungeonPrefab[prefabHash] = has;
            return has;
        }

        /// <summary>
        /// Drop dungeon interiors from ONE peer's creation slice when that peer
        /// is not in the interior's active area.
        ///
        /// Measured over two sessions: 75-85% of DungeonGenerator.Spawn calls
        /// were for dungeons no player was in area of, each costing a flat
        /// ~450ms of blocked tick with no warm-asset discount on repeats. One
        /// rebuild cost 419ms for an interior that lived 7 seconds and was never
        /// closer than 138m to anybody.
        ///
        /// WHY THE WHOLE INTERIOR AND NOT JUST THE GENERATOR: the generator is
        /// what calls ZoneSystem.SetLoadingInZone during LoadRoomPrefabsAsync,
        /// and that registration is what makes IsZoneReadyForType refuse every
        /// lower-ranked ZDO in the zone until the rooms exist. Gate only the
        /// generator and nothing holds the Default-type contents back, so mobs
        /// and chests get created into empty space 5000m up. Both are gated on
        /// the identical per-zone condition, so they can never disagree.
        ///
        /// Interior membership is Character.InInterior's own test (y > 3000),
        /// plus a DungeonGenerator component check because the generator's own
        /// altitude VARIES - measured at y=34 for one location and y=5092 for
        /// another, decided by Location.m_useCustomInteriorTransform, which
        /// forces the generator to the location origin. Altitude alone would
        /// miss half of them.
        ///
        /// REMOVAL IS DELIBERATELY NOT FILTERED. RemoveObjects is a whitelist;
        /// a short list there destroys everything absent from it. Leaving the
        /// union alone also buys hysteresis for free: creation stops at
        /// InActiveArea (~112m) while destruction still happens at the coverage
        /// box (160m per axis), so an interior already built survives the player
        /// wandering out and back without a rebuild.
        /// </summary>
        private static List<ZDO> GateInteriors(ZNetScene scene, List<ZDO> slice, Vector3 peerPos)
        {
            if (!Plugin.DebugGateDungeonInteriors) return slice;

            s_gatedSlice.Clear();
            s_zoneCovered.Clear();
            bool droppedAny = false;

            for (int i = 0; i < slice.Count; i++)
            {
                var zdo = slice[i];
                if (zdo == null) continue;

                bool interior = zdo.GetPosition().y > 3000f;
                if (!interior && !HasDungeonGenerator(scene, zdo.GetPrefab()))
                {
                    s_gatedSlice.Add(zdo);
                    continue;
                }

                var zone = zdo.GetSector();
                if (!s_zoneCovered.TryGetValue(zone, out bool covered))
                {
                    // Tested at the ZONE CENTRE so every ZDO in the zone gets
                    // the same answer, matching how IsZoneReadyForType works.
                    covered = ZNetScene.InActiveArea(ZoneSystem.GetZonePos(zone), peerPos);
                    s_zoneCovered[zone] = covered;
                }
                if (covered) s_gatedSlice.Add(zdo);
                else droppedAny = true;
            }

            return droppedAny ? s_gatedSlice : slice;
        }

        /// <summary>
        /// Walks EVERY unique peer position and unions the results into vanilla's
        /// own temp lists. Sets s_unionBuiltThisTick, which is the only thing that
        /// authorises a RemoveObjects call.
        ///
        /// Note an empty peer set yields empty lists, and RemoveObjects will then
        /// destroy every live instance. That is pre-existing behaviour with no
        /// players online and is left unchanged.
        /// </summary>
        private static void BuildUnion(ZNetScene scene, ZDOMan zdoMan, SimulationDistance simDist,
                                       out List<ZDO> unionNear, out List<ZDO> unionDistant)
        {
            unionNear = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentObjects.GetValue(scene);
            unionDistant = (List<ZDO>)ReflectionCache.ZNetScene_tempCurrentDistantObjects.GetValue(scene);
            unionNear.Clear();
            unionDistant.Clear();
            // Rebuilt here rather than per tick: this is the only place the FULL
            // set of near ZDOs is walked, so clearing per tick would leave the
            // registry empty on every tick that does not rebuild the union.
            PheromoneServerSide.BeginPass();
            s_seenNear.Clear();
            s_seenDistant.Clear();

            foreach (var pos in ServerCoverage.GetUniquePeerPositions())
            {
                s_nearBuffer.Clear();
                s_distantBuffer.Clear();
                zdoMan.FindSectorObjects(ZoneSystem.GetZone(pos), simDist,
                                         s_nearBuffer, s_distantBuffer);
                foreach (var zdo in s_nearBuffer)
                {
                    if (!s_seenNear.Add(zdo)) continue;
                    PheromoneServerSide.Offer(zdo);
                    unionNear.Add(zdo);
                }
                foreach (var zdo in s_distantBuffer)
                    if (s_seenDistant.Add(zdo)) unionDistant.Add(zdo);
            }
            s_unionBuiltThisTick = true;
        }

        /// <summary>
        /// The one place RemoveObjects may be called. The guard is not defensive
        /// politeness - handing a per-peer slice here destroys every other peer's
        /// objects, and permanently destroys non-persistent ones the server owns.
        /// </summary>
        private static void InvokeRemoveWithUnion(ZNetScene scene,
                                                  List<ZDO> unionNear, List<ZDO> unionDistant)
        {
            if (!s_unionBuiltThisTick)
            {
                Plugin.Log.LogError(
                    "[CreateDestroy] REFUSING RemoveObjects: union was not built this tick. " +
                    "This would have destroyed every object outside the list.");
                return;
            }
            ReflectionCache.ZNetScene_RemoveObjects.Invoke(
                scene, new object[] { unionNear, unionDistant });
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
    /// The visible symptom — the hull lunging 4–5m down and taking damage on
    /// Half/Full sail-speed changes — turned out to be a crash, not a physics
    /// mismatch: Ship.UpdateSailSize threw on the server every tick the sail
    /// moved, aborting buoyancy (see Ship_UpdateSailSize_NoLocalPlayer_Patch).
    /// That patch fixes the crash; this ownership rule stays because it is
    /// still the right design:
    ///  - it's what vanilla does (Ship.UpdateOwner), just unable to run here;
    ///  - the server's wind ignores Moder's power (that branch in
    ///    EnvMan.UpdateWind needs a local player), so a server-simulated ship
    ///    sails on natural wind while the helmsman sees a tailwind;
    ///  - when the helmsman's client simulates the hull, deck and player share
    ///    one physics scene, instead of the deck arriving over the network.
    /// (An earlier version of this comment blamed the player's network
    /// stand-in colliding with the deck. The v0.6.2 Postfix diagnostic that
    /// seemed to support that couldn't see the throwing ticks — a Postfix
    /// doesn't run when the original method throws.)
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
        // v0.7.44 FIX (container stick): vanilla hands a container to the peer
        // that opens it (Container.RPC_RequestOpen does SetOwner(uid)), but our
        // 2s reclaim takes it back before that peer's client has set s_inUse=1
        // — measured 310 reclaims against 309 grants, i.e. essentially every
        // open. Ownership then flips repeatedly until the client's copy and
        // ours disagree, the client routes RPC_RequestOpen to the stale owner,
        // and vanilla answers "but im not the owner" with NO response at all,
        // leaving the UI waiting forever. Captured live 2026-10-11 14:38-14:39.
        //
        // The previous attempt (writing s_inUse=1 ourselves) could never work:
        // the new owner's UpdateUseVisual rewrites that field from its own
        // local m_inUse, which is still false until the UI opens. So this
        // guard holds state OUR side instead, and keys on revisions rather
        // than on grants we handled — a client-to-client handoff never reaches
        // the server, but the resulting OwnerRevision bump does arrive via ZDO
        // sync, so we notice it either way.
        //
        // DataRevision is included because closing and reopening while already
        // the owner does NOT bump OwnerRevision (SetOwner no-ops when the owner
        // is unchanged). Both open and close write s_inUse, and ZDO.Set calls
        // IncreaseDataRevision, so any interaction refreshes the grace. Cost is
        // containers staying peer-owned while in active use, which is the safe
        // direction: containers need next to no server-side simulation.
        internal const float ContainerGraceSeconds = 10f;
        private const float ContainerWatchExpirySeconds = 60f;
        private const int ContainerWatchSweepEveryNPasses = 30;

        private struct ContainerWatch
        {
            public ushort OwnerRev;
            public uint DataRev;
            public float Stamp;
        }

        private static readonly Dictionary<int, bool> s_containerPrefabCache = new Dictionary<int, bool>();
        private static readonly Dictionary<ZDOID, ContainerWatch> s_containerWatch = new Dictionary<ZDOID, ContainerWatch>();
        private static int s_containerWatchPassCount;
        internal static int s_containerGraceSkips;

        /// <summary>
        /// v0.7.57: widened from a root-only GetComponent, which silently
        /// excluded any prefab whose Container sits on a child.
        ///
        /// Found via bug #8 on 2026-10-25. The Obliterator churned ownership
        /// server-to-client 18 times in three minutes because our grace period
        /// never recognised it as a container, so the reclaim took it straight
        /// back every ~2s. A lever pull landed while we owned it, ownership
        /// flipped to the client mid-delay, and the re-check at
        /// Incinerator.Incinerate():186 silently aborted the burn. The lever had
        /// already animated by then, so it looked like nothing happened.
        ///
        /// Identical trap to PrefabHasSadle before v0.7.46: GetComponent only
        /// inspects the root. Searched with includeInactive because a container
        /// child can be inactive until used, same as the saddle child.
        ///
        /// NOT YET PROVEN to fix the Obliterator. Incinerator.m_container is a
        /// serialized reference and may point at a sibling or an unrelated
        /// object rather than a child, in which case this still misses it. The
        /// lazy log below is what settles that: if "incinerator" appears, the
        /// children search reached it. If it does not, follow the serialized
        /// reference instead, the way PrefabHasSadle falls back to
        /// Tameable.m_saddle.
        ///
        /// The log is deliberately lazy rather than a startup dump. This is only
        /// called for ZDOs the reclaim loop actually walks, i.e. objects in
        /// loaded zones near players, so it can only ever name things the user
        /// has already encountered. A startup enumeration of every container
        /// prefab would spoil unfound content — see
        /// [[feedback-no-spoilers-in-vcs]] for the user's standing preference.
        ///
        /// Widening only ever DECLINES to reclaim: it writes nothing and
        /// destroys nothing, so an unexpected match costs at most ten extra
        /// seconds of peer ownership, which is the direction vanilla errs in
        /// anyway.
        /// </summary>
        private static bool PrefabHasContainer(int prefabHash)
        {
            if (s_containerPrefabCache.TryGetValue(prefabHash, out bool known)) return known;
            var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            // Do NOT cache a negative when the lookup itself failed: the
            // scene may not be ready yet, and caching false here would
            // permanently exclude this prefab from every guard below.
            if (prefab == null) return false;

            bool onRoot = prefab.GetComponent<Container>() != null;
            bool has = onRoot || prefab.GetComponentInChildren<Container>(true) != null;

            s_containerPrefabCache[prefabHash] = has;
            if (has)
                Plugin.Log.LogInfo(
                    $"[{Plugin.Stamp}] [Diag] Container-bearing prefab: {prefab.name} " +
                    $"(onRoot={onRoot}) — grace period applies");
            return has;
        }

        // Containers that drift out of the active sectors stop being visited,
        // so their entries would leak. Swept rather than removed per-pass to
        // keep the hot loop free of enumeration.
        private static void SweepContainerWatch()
        {
            if (++s_containerWatchPassCount < ContainerWatchSweepEveryNPasses) return;
            s_containerWatchPassCount = 0;
            float now = Time.time;
            List<ZDOID> drop = null;
            foreach (var kv in s_containerWatch)
            {
                if (now - kv.Value.Stamp <= ContainerWatchExpirySeconds) continue;
                (drop ?? (drop = new List<ZDOID>())).Add(kv.Key);
            }
            if (drop == null) return;
            foreach (var id in drop) s_containerWatch.Remove(id);
        }

        // v0.7.44 diagnostic: is our 2s reclaim fighting vanilla's mount
        // ownership handoff? Sadle.RPC_RequestControl does
        // `GetZDO().SetOwner(sender)` when a player mounts, and
        // Sadle.RPC_Controls opens with `if (!m_nview.IsOwner()) return;` — so
        // every control input arriving while WE hold ownership is silently
        // dropped. Suspected cause of a ridden mount stalling then skipping
        // ahead. Keyed by prefab hash because the Sadle component is only
        // discoverable via GetComponent on the prefab.
        private static readonly Dictionary<int, bool> s_sadlePrefabCache = new Dictionary<int, bool>();

        internal static bool IsSaddlePrefab(int prefabHash) => PrefabHasSadle(prefabHash);

        private static bool PrefabHasSadle(int prefabHash)
        {
            if (s_sadlePrefabCache.TryGetValue(prefabHash, out bool known)) return known;
            var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
            // Do NOT cache a negative when the lookup itself failed: the
            // scene may not be ready yet, and caching false here would
            // permanently exclude this prefab from every guard below.
            if (prefab == null) return false;

            // v0.7.46: `GetComponent<Sadle>()` on the root ALWAYS misses.
            // Sadle.Awake does `GetComponentInParent<Character>()`, so the
            // component sits on a CHILD, and Tameable.SetSaddle does
            // `m_saddle.gameObject.SetActive(enabled)` — so that child is
            // INACTIVE until a saddle is equipped. Hence includeInactive:true,
            // with the serialised Tameable.m_saddle reference as a fallback in
            // case the child is parented elsewhere.
            bool has = prefab.GetComponentInChildren<Sadle>(true) != null;
            if (!has)
            {
                var tameable = prefab.GetComponent<Tameable>();
                has = tameable != null && tameable.m_saddle != null;
            }
            s_sadlePrefabCache[prefabHash] = has;
            return has;
        }

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
                    ReclaimStats.CountScanned();
                    if (!zdo.Persistent) continue;
                    // Ships a player is aboard belong to that player, as in
                    // vanilla. Must run before the "server already owns it"
                    // skip below, or a parked (server-owned) boat would never
                    // be handed over when someone boards it.
                    if (ShipOwnership.TryAssignToPeer(zdo)) continue;
                    // v0.7.44 trace (bug #1): one line per pass per ridden
                    // mount, whether or not we reclaim it. Placed BEFORE the
                    // already-owned early-exit on purpose: once we hold the
                    // mount, every later pass short-circuits there, so the
                    // reclaim warning alone would go silent and be
                    // indistinguishable from the diagnostic never firing.
                    //
                    // ownerIsUs=True on consecutive passes means ownership is
                    // STABLE with us — Sadle.RequestControl is invoked once
                    // from Interact and never re-requested, so one steal per
                    // mount is the expected shape, not continuous churn. If
                    // the mount still skips while this reads stable, ownership
                    // is not the cause and the send rate is the next suspect.
                    // v0.7.47 FIX (bug #1): leave a ridden mount owned by its
                    // rider, the way vanilla does.
                    //
                    // Sadle.RPC_RequestControl does `GetZDO().SetOwner(sender)`
                    // when a player mounts, so in vanilla the rider's own client
                    // owns and simulates the creature it is sitting on. Our
                    // reclaim took that back, making the rider a NON-owner of
                    // its own mount — and ZSyncTransform.SyncPosition then
                    // dead-reckons for non-owners:
                    //     m_targetPosTimer += dt (clamped to 2s)
                    //     position += zdo.GetVec3(s_velHash) * m_targetPosTimer
                    //     transform.position = (dist < 5f) ? Lerp(...) : position
                    // so the rider's client projects the mount forward by
                    // velocity x gap and HARD-SNAPS once that exceeds 5m.
                    //
                    // Measured 2026-10-13 while riding: 4 snap events, peak
                    // projection 11.31m, on gaps of 672-3699ms. Windows holding
                    // near the healthy 50ms gap produced none. An owner runs
                    // OwnerSync instead and never extrapolates at all, so
                    // handing ownership back removes this outright rather than
                    // narrowing the window.
                    //
                    // NOTE s_user means different things per prefab: Sadle
                    // writes `player.GetZDOID().UserID` (a PEER uid) whereas
                    // ShipControlls writes `player.GetPlayerID()`. Confirmed
                    // empirically — our MountSync line showed peer=2614171117
                    // and rider=2614171117 for the same ride — so it is used
                    // directly as the owner here, NOT resolved through
                    // GetPlayerID() the way ShipOwnership has to.
                    //
                    // Placed BEFORE the already-owned early-exit because the
                    // server currently owns ridden mounts; a guard after it
                    // would never fire. A dead or absent rider falls through to
                    // the normal reclaim, which is the cleanup path.
                    if (PrefabHasSadle(zdo.GetPrefab()))
                    {
                        long mountRider = zdo.GetLong(ZDOVars.s_user, 0L);
                        if (mountRider != 0L && ServerCoverage.IsLivePeer(mountRider))
                        {
                            long mountPrev = zdo.GetOwner();
                            if (mountPrev != mountRider)
                            {
                                zdo.SetOwner(mountRider);
                            }
                            continue;
                        }
                    }
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
                    // v0.7.44 FIX: grace period on a container a live peer has
                    // just taken or touched. See ContainerGraceSeconds above
                    // for why this keys on revisions rather than on grants.
                    // Ordered after the s_inUse guard so an already-open
                    // container short-circuits on vanilla's own flag first and
                    // never needs an entry here.
                    if (PrefabHasContainer(zdo.GetPrefab()))
                    {
                        bool liveOwner = ServerCoverage.IsLivePeer(zdo.GetOwner());
                        bool watched = s_containerWatch.TryGetValue(zdo.m_uid, out var watch);
                        if (liveOwner)
                        {
                            // Unknown counts as activity: after a restart a peer
                            // may already be mid-open, and reclaiming to learn
                            // otherwise is exactly the bug. Costs one grace
                            // period per container, once.
                            if (!watched || watch.OwnerRev != zdo.OwnerRevision
                                         || watch.DataRev != zdo.DataRevision)
                            {
                                s_containerWatch[zdo.m_uid] = new ContainerWatch
                                {
                                    OwnerRev = zdo.OwnerRevision,
                                    DataRev = zdo.DataRevision,
                                    Stamp = Time.time,
                                };
                                System.Threading.Interlocked.Increment(ref s_containerGraceSkips);
                                continue;
                            }
                            if (Time.time - watch.Stamp < ContainerGraceSeconds)
                            {
                                System.Threading.Interlocked.Increment(ref s_containerGraceSkips);
                                continue;
                            }
                        }
                        else if (watched)
                        {
                            // Owner is gone or is us — nothing to protect.
                            s_containerWatch.Remove(zdo.m_uid);
                        }
                    }
                    // v0.6.7: a hitched cart is in use too. Carts never set
                    // s_inUse — the only "busy" signal is s_attachJointHash,
                    // written by the puller's client, which owns the cart and
                    // runs the joint. Reclaiming it made that client see "not
                    // owner" and drop the cart (Vagon.Update's
                    // `else if (IsAttached()) Detach();`) — confirmed in-game.
                    // Dead owner → fall through and reclaim; the server's own
                    // Vagon.Update then sees the stale flag as owner and
                    // clears it via Detach(), so no manual heal is needed.
                    if (zdo.GetBool(ZDOVars.s_attachJointHash) &&
                        ServerCoverage.IsLivePeer(zdo.GetOwner())) continue;
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
                            // v0.7.88 TRIED letting the server claim TerrainComps that no
                            // peer is in area for (assignTo==0 falling through instead of
                            // hitting this continue), to stop RPC_ApplyOperation dying on
                            // unowned comps. REVERTED in v0.7.89: it caused a CLIENT-side
                            // duplicate storm. 7,218 "Removed duplicate terrain compiler"
                            // lines in the client log, ALL after the v0.7.88 restart and
                            // ZERO in the 29 minutes before it, peaking at 6,300 in one
                            // minute. TerrainComp.TryCleanInvalidTCs ends each one with
                            // ZNetScene.Destroy, a NETWORKED destroy, so bulk-claiming 250
                            // comps at once on arrival fed a create/destroy loop. The lag
                            // was far worse than the dropped RPCs it fixed, which are loud
                            // but harmless.
                            //
                            // DO NOT retry this as a bulk claim. Any retry must be LAZY:
                            // claim one comp at the moment an op actually targets it.
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

                    // v0.6.6 diagnostic for cart theory B (see
                    // Vagon_Detach_Diag_Patch): are we taking a cart away from
                    // a player while it's still hitched to them? Since the
                    // v0.6.7 skip above, only live=False (owner disconnected
                    // mid-pull) should appear; live=True means a regression.
                    if (zdo.GetBool(ZDOVars.s_attachJointHash))
                    {
                        // A dead owner here is the expected cleanup path, so it
                        // only counts toward the digest. A LIVE owner means the
                        // v0.6.7 in-use skip failed and carts are dropping
                        // again — that still warns immediately, and should
                        // never appear.
                        if (ServerCoverage.IsLivePeer(prevOwner))
                        {
                            var cartPos = zdo.GetPosition();
                            var cartPrefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                            Plugin.Log.LogWarning(
                                $"[{Plugin.Stamp}] [Diag] REGRESSION: cart reclaimed by server from LIVE peer {prevOwner} " +
                                $"while attachJoint=True " +
                                $"prefab={(cartPrefab != null ? cartPrefab.name : "?")} " +
                                $"pos=world({cartPos.x:F0},{cartPos.z:F0})");
                        }
                        else
                        {
                            DailyDigest.RecordCartReclaimedDeadOwner();
                        }
                    }

                    // v0.7.44 diagnostic: mount ownership churn. Fires only
                    // for a saddled creature with a rider recorded (s_user).
                    // A LIVE prevOwner means we are taking the mount away from
                    // the player riding it, which drops their control input in
                    // Sadle.RPC_Controls until the next handoff. Immediate
                    // warning rather than a digest counter: this only happens
                    // while somebody is mounted, so the volume is bounded, and
                    // the per-event timing is the whole point.

                    // v0.7.11: every SetOwner bumps OwnerRevision, and vanilla's
                    // ZDOPeer.ShouldSend returns true unconditionally when a
                    // peer's known OwnerRevision is behind — so each reclaim
                    // forces that ZDO to be re-sent to EVERY nearby peer. With
                    // a 2s cycle this is a continuous, self-inflicted sync load
                    // vanilla never generates, and peers were measured pinned at
                    // vanilla's 10KB send ceiling 25-85% of the time (solo),
                    // ending in a ZRpc timeout disconnect on 2026-09-25.
                    ReclaimStats.CountOwnerChange();
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
                                    // v0.7.2: stays immediate — this is the prime
                                    // suspect for the chest that opens and then
                                    // empties a second later. Taking the ZDO from a
                                    // peer who has the chest open flips IsOwner()
                                    // false on their client, and
                                    // InventoryGui.UpdateContainer then hides the
                                    // grid. s_inUse SHOULD have made us skip this
                                    // ZDO, so seeing s_inUse=0 here means the
                                    // in-use flag never arrived (or was cleared)
                                    // and points at the ordering, not the skip.
                                    var chestPos = zdo.GetPosition();
                                    Plugin.Log.LogWarning(
                                        $"[{Plugin.Stamp}] [Diag] Container reclaimed from LIVE peer {prevOwner} " +
                                        $"prefab={prefab.name} pos=world({chestPos.x:F0},{chestPos.z:F0}) " +
                                        $"s_inUse={zdo.GetInt(ZDOVars.s_inUse, 0)} " +
                                        $"dataRev={zdo.DataRevision}");
                                }
                                else
                                    System.Threading.Interlocked.Increment(ref s_containerReclaimAbsentPeer);
                            }
                        }
                    }
                }
            }
            SweepContainerWatch();
            ReclaimStats.EndPass();
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
            // Kept immediate at the user's request (2026-09-24): chests still go
            // stuck, most recently opening fine and then losing their inventory
            // a second or two later. This line is the "before" half of that
            // story — pair it with a later "Container reclaimed from LIVE peer"
            // warning for the same chest to catch the ownership being pulled
            // out from under an open UI. Daily totals also go to the digest.
            int sInUse = zdo.GetInt(ZDOVars.s_inUse, 0);
            bool imOwner = nview.IsOwner();
            long imUid = ZDOMan.GetSessionID();
            Plugin.Log.LogInfo(
                $"[{Plugin.Stamp}] [Diag] RequestOpen chest={__instance.gameObject.name} " +
                $"requester={uid} prevOwner={prevOwner} s_inUse={sInUse} " +
                $"imOwner={imOwner} imUid={imUid}");
            DailyDigest.RecordContainerOpen(__instance.gameObject.name.Replace("(Clone)", ""));
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
                Plugin.Log.LogInfo($"[{Plugin.Stamp}] [Diag] Random event cleared.");
                return;
            }
            int spawnerCount = ev.m_spawn?.Count ?? 0;
            Plugin.Log.LogInfo(
                $"[{Plugin.Stamp}] [Diag] Random event started: name={ev.m_name} target=world({pos.x:F0},{pos.z:F0}) " +
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
                $"[{Plugin.Stamp}] [Diag] Server-side active event promoted: " +
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
                $"[{Plugin.Stamp}] [Debug] Raids DISABLED (eventChance=-1, no roll can pass). " +
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
            // v0.7.2: counted into the daily digest instead of one line per heal
            // (~578/session). The sector list there shows where the race bites.
            DailyDigest.RecordTerrainHeal(__instance.transform.position);
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
            DailyDigest.RecordTerrainLoad();
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
            // v0.7.2: the noisiest diagnostic we had (1483 lines in one session).
            // Only the churn total and its direction matter, so count them.
            DailyDigest.RecordTerrainOwnerChange(prevOwner, uid);
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
                    $"[{Plugin.Stamp}] [Pickable] Transpiler could not find the Player.m_localPlayer.GetZDOID() " +
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
    /// v0.6.6 fix: stop Ship.UpdateSailSize throwing on the server.
    ///
    /// Build 25185644 added this to UpdateSailSize, guarded only by
    /// `!flag && m_sailWasInPosition` (the first tick the sail leaves a rest
    /// position):
    ///
    ///   ZDOID zDOID = Player.m_localPlayer.GetPlayerID() == m_shipControlls.GetUser()
    ///       ? Player.m_localPlayer.GetZDOID() : ZDOID.None;
    ///   m_changeSailPosEffect.Create(..., zDOID);
    ///
    /// With no local player that NREs — and because the throw lands before
    /// `m_sailWasInPosition = flag` at the end of the method, the flag never
    /// clears, so it throws on EVERY tick until the sail reaches its target
    /// (~0.5–1s per speed change). CustomFixedUpdate calls UpdateSail before the
    /// owner gate and all buoyancy code, so on a server-owned ship each of those
    /// ticks skipped buoyancy entirely: the hull free-fell, then slammed back up
    /// — the "boat lunges down and takes damage on sail-speed changes" bug.
    /// (ShipOwnership moved simulation to the client, where this never throws;
    /// this patch fixes the remaining server-owned cases and the log spam.)
    ///
    /// Fix: when there is no local player, clear m_sailWasInPosition before the
    /// method runs, so the effect branch never fires. That field is read nowhere
    /// else. Deliberately NOT a null-safe transpiler (as for Pickable): letting
    /// the branch run would make the server spawn m_changeSailPosEffect, and
    /// since 25185644 effect prefabs can be networked (EffectList.Create stamps
    /// owner ZDO fields on them) — a server copy could replicate as a doubled
    /// sail sound. The server has no audience for the effect anyway; before this
    /// patch it never spawned it either, because it crashed first.
    /// </summary>
    [HarmonyPatch(typeof(Ship), "UpdateSailSize")]
    internal static class Ship_UpdateSailSize_NoLocalPlayer_Patch
    {
        private static readonly FieldInfo s_sailWasInPositionField =
            AccessTools.Field(typeof(Ship), "m_sailWasInPosition");

        static void Prefix(Ship __instance)
        {
            if (Player.m_localPlayer == null)
                s_sailWasInPositionField.SetValue(__instance, false);
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
            var hmap = (Heightmap)s_hmapField.GetValue(__instance);
            // hmapFound=false is the zone-load race; the digest reports how many
            // of the day's Awakes lost it.
            DailyDigest.RecordTerrainAwake(hmap != null);
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
            bool initialized = (bool)s_initField.GetValue(__instance);
            var nview = __instance.GetComponent<ZNetView>();
            bool isOwner = nview != null && nview.IsOwner();
            // willSave=false means the dig was silently thrown away — the class
            // of bug v0.5.22 fixed, so the digest tracks it as LOST.
            DailyDigest.RecordApplyOperation(initialized && isOwner);
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
                $"[{Plugin.Stamp}] [Diag] WearNTear.Destroy prefab={prefab} " +
                $"pos=world({pos.x:F0},{pos.y:F1},{pos.z:F0}) biome={biome} " +
                $"cause={cause}{attacker} support={support} haveRoof={haveRoof} wet={wet} " +
                $"nearestPeerDist={nearestPeerDist:F0}m");
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


    /// <summary>
    /// Reflection handles and the engine's link-pool size, shared by the two
    /// Pathfinding patches that cap and sweep navmesh tiles (bug 25's fix).
    ///
    /// The [Nav] gauge that lived here was removed once bug 25 closed — the
    /// pool pressure it measured was real but turned out not to be the cause
    /// of the stutter, so the numbers were no longer worth the log volume.
    /// Only the members the fix itself needs are kept.
    /// </summary>
    internal static class NavMeshPressure
    {
        internal const int EngineLinkBudget = 65535;

        internal static readonly FieldInfo TilesField =
            AccessTools.Field(typeof(Pathfinding), "m_tiles");
        internal static readonly MethodInfo TimeoutTilesMethod =
            AccessTools.Method(typeof(Pathfinding), "TimeoutTiles");

        internal static int s_tilesFreed;
    }

    /// <summary>
    /// Apply our navmesh tuning once the Pathfinding singleton exists.
    /// Setting vanilla's own field rather than reimplementing its logic keeps
    /// this migration-proof: fields survive updates, method bodies don't
    /// (see the 25185644 migration).
    /// </summary>
    [HarmonyPatch(typeof(Pathfinding), "Awake")]
    internal static class Pathfinding_Awake_Patch
    {
        static void Postfix(Pathfinding __instance)
        {
            float vanillaTimeout = __instance.m_tileTimeout;
            if (Plugin.NavTileTimeoutSeconds > 0f)
                __instance.m_tileTimeout = Plugin.NavTileTimeoutSeconds;
            string timeoutState = Plugin.NavTileTimeoutSeconds > 0f
                ? $"{vanillaTimeout:F0}s → {__instance.m_tileTimeout:F0}s"
                : $"{vanillaTimeout:F0}s (vanilla, unchanged)";

            float vanillaLinkWidth = __instance.m_linkWidth;
            if (Plugin.NavLinkWidth > 0f)
                __instance.m_linkWidth = Plugin.NavLinkWidth;
            string linkState = Plugin.NavLinkWidth > 0f
                ? $"{vanillaLinkWidth:F1}m → {__instance.m_linkWidth:F1}m"
                : $"{vanillaLinkWidth:F1}m (vanilla, unchanged)";

            Plugin.Log.LogInfo(
                $"[{Plugin.Stamp}] [Nav] tile timeout {timeoutState}, " +
                $"sweep {Plugin.NavTileSweepPerPass} stale tile(s) per pass" +
                $"{(Plugin.NavTileSweepPerPass <= 1 ? " (vanilla, unchanged)" : " (vanilla frees 1)")}, " +
                $"stitch spacing {linkState}, tileSize {__instance.m_tileSize:F0}m. " +
                $"linkBudget={NavMeshPressure.EngineLinkBudget}");
        }
    }

    /// <summary>
    /// Free every expired navmesh tile per maintenance pass, not just one.
    ///
    /// Vanilla's TimeoutTiles walks m_tiles, removes the first tile whose last
    /// use is older than m_tileTimeout, and breaks. The break is a C# necessity
    /// — you can't keep enumerating a Dictionary you just removed from — rather
    /// than a work budget: the expensive part (destroying navmesh + link data)
    /// is already deferred to m_tileRemoveQueue / m_linkRemoveQueue, which
    /// drain at their own throttled rate. So one-per-pass is a side effect, and
    /// the pass itself runs at most 10x/sec and is skipped entirely while a
    /// tile build is in flight.
    ///
    /// Effect: a peer who travels leaves a trail of dead tiles behind, and
    /// vanilla reclaims them slower than 8 peers create new ones — the link
    /// pool fills with tiles nobody is using.
    ///
    /// Rather than reimplement the removal (which would couple us to the
    /// private NavMeshTile type and its queues), call vanilla's own method
    /// repeatedly until it stops shrinking the dictionary, capped per pass.
    /// s_sweeping guards the re-entry, since the re-invoke goes through this
    /// same patch.
    /// </summary>
    [HarmonyPatch(typeof(Pathfinding), "TimeoutTiles")]
    internal static class Pathfinding_TimeoutTiles_Patch
    {
        private static bool s_sweeping;

        static void Prefix(Pathfinding __instance, out int __state)
        {
            __state = -1;
            if (s_sweeping) return;
            if (NavMeshPressure.TilesField?.GetValue(__instance) is System.Collections.ICollection tiles)
                __state = tiles.Count;
        }

        static void Postfix(Pathfinding __instance, int __state)
        {
            if (s_sweeping || __state < 0) return;
            if (NavMeshPressure.TimeoutTilesMethod == null) return;
            if (!(NavMeshPressure.TilesField.GetValue(__instance) is System.Collections.ICollection tiles)) return;

            s_sweeping = true;
            try
            {
                // Vanilla's own call (the one we're a postfix of) already freed
                // at most one, and counts toward the budget — so start at 1.
                // NavTileSweepPerPass=1 therefore means "pure vanilla".
                for (int i = 1; i < Plugin.NavTileSweepPerPass; i++)
                {
                    int before = tiles.Count;
                    NavMeshPressure.TimeoutTilesMethod.Invoke(__instance, null);
                    if (tiles.Count >= before) break;
                }
            }
            finally
            {
                s_sweeping = false;
            }

            int removed = __state - tiles.Count;
            if (removed > 0)
                System.Threading.Interlocked.Add(ref NavMeshPressure.s_tilesFreed, removed);
        }
    }


    /// <summary>
    /// v0.7.5: per-pass timing. The 2026-09-25 synthetic 8-peer test degraded
    /// the server from 33ms to 93ms per tick over four minutes, with the stitch
    /// pool never above 14% and zero link failures — so the ceiling is not the
    /// navmesh pool but something that scales per simulated area. This measures
    /// the four suspects directly instead of guessing:
    ///   zoneLoad      ZoneSystem.Update  — pokes every active sector each tick
    ///   createDestroy ZNetScene.CreateDestroyObjects — per-peer ZDO walk
    ///   releaseZDOS   ZDOMan.ReleaseZDOS — ownership reclaim, every 2s
    ///   navSweep      Pathfinding.TimeoutTiles — our stale-tile sweep
    ///
    /// Measured by a separate pair of patches per method (Priority.First prefix,
    /// Priority.Last postfix) so the window covers our own replacement logic as
    /// well — patching the same method twice is fine, and each patch class gets
    /// its own __state. Whatever the four don't account for is vanilla's own
    /// work: mob AI, physics, saves.
    /// </summary>
    /// <summary>
    /// v0.7.9: per-peer send starvation.
    ///
    /// Vanilla refuses to send a peer anything while its socket send queue is
    /// over 10240 bytes (ZDOMan.SendZDOs), and caps each packet at whatever is
    /// left under that ceiling. It is deliberate backpressure — it would rather
    /// skip an update than deliver a growing backlog of stale positions.
    ///
    /// That ceiling matters much more for us than for vanilla: because the
    /// server owns every mob, every mob's movement has to be sent from here to
    /// all peers, whereas vanilla lets the nearest client own and simulate a mob
    /// locally for free. In a busy fight with several players this is a prime
    /// suspect for "combat feels laggy" while CPU, ticks and total bandwidth all
    /// look healthy — which is exactly the state we measured on 2026-09-22.
    ///
    /// Sampled at 4Hz rather than patched onto SendZDOs, whose parameter is a
    /// private nested type: a peer over the ceiling at sample time is a peer
    /// vanilla is currently refusing, so the share of samples over 10240 is the
    /// same signal. GetSendQueueSize can reach into the transport, so 4Hz keeps
    /// it away from the per-frame path.
    /// </summary>
    /// <summary>
    /// v0.7.11: how much work each ownership-reclaim pass does, and therefore
    /// how much forced network traffic it generates.
    ///
    /// Vanilla's ZDOPeer.ShouldSend re-sends a ZDO to a peer whenever its
    /// OwnerRevision is ahead of what that peer knows — so N ownership changes
    /// per pass means N forced ZDO sends PER PEER, on top of ordinary data
    /// updates. Our reclaim runs every 2s over every peer's surroundings, so if
    /// it is changing hundreds of owners per pass we are manufacturing a sync
    /// flood that vanilla (which leaves ownership alone) never produces.
    ///
    /// Reported on the [Pass] line as reclaims=avg/pass (max), scanned=avg.
    /// The number that matters is ownerChanges: scanned is just the sweep size.
    /// </summary>
    /// <summary>
    /// v0.7.14: how many ZDOs the server actually offers each peer, so the
    /// reclaim counters can be read as a SHARE of traffic rather than a bare
    /// number. Without this, "400 reclaims per window" could be 80% of what we
    /// send or 5% — and that decides between fixing our ownership churn and
    /// accepting that server-owned mobs simply cost this much.
    ///
    /// Hooked on ZDOMan.CreateSyncList, which vanilla calls once per peer per
    /// send attempt, and only after the 10KB queue gate has passed — so it
    /// counts real send opportunities, not refused ones. Its `toSync` parameter
    /// is a plain List&lt;ZDO&gt;, so we can bind by name and never touch the
    /// private ZDOPeer type.
    ///
    /// Reported as "candidates", deliberately: vanilla stops packing when the
    /// packet fills, so the number sent is at most this.
    /// </summary>
    internal static class SyncVolume
    {
        private static int s_calls, s_candidates, s_max;

        // v0.7.15: what the server keeps offering. Knowing the volume (~19k
        // candidates per window to ONE starved client) does not say whether it
        // is thousands of static building pieces, mobs in motion, or dropped
        // items — and those need completely different fixes. Sampled every
        // SampleEveryNth call and capped, so the prefab lookup stays off the
        // hot path.
        private const int SampleEveryNth = 30;
        private const int BurstThreshold = 500;   // always sample lists this big
        // Raised from 24 (v0.7.27): anything past the cap lands in "(other)",
        // which hides exactly the long tail we are trying to identify. 48 buckets
        // of (string,int) costs nothing next to the per-send list walk.
        private const int MaxPrefabsTracked = 48;

        // How many of those buckets reach the log line. Kept below the tracking
        // cap on purpose — "shown N of M" reports the shortfall rather than the
        // line growing without limit.
        private const int PrefabsPrinted = 24;
        private static readonly Dictionary<string, int> s_byPrefab = new Dictionary<string, int>();
        private static int s_sampledLists;

        // v0.7.26: ZDO.ObjectType distribution, and the same for the HEAD of the
        // sorted list.
        //
        // ObjectType decides send order before distance does — ServerSendCompare
        // puts Prioritized-and-owned-by-someone-else first, then everything else
        // by Type DESCENDING (Terrain, Solid, Prioritized, Default). Which type a
        // creature or a chest carries is set per prefab in ZNetView.m_type, which
        // lives in the prefab assets and so is NOT visible in the decompile — it
        // can only be read at runtime, which is the point of this histogram.
        //
        // HeadByType is a proxy for what actually LANDS: vanilla packs the sorted
        // list from the front until the packet is full, so the head is what gets
        // through and the tail is what waits. It is positional, not byte-exact —
        // a true measure needs a hook inside the packing loop. 32 is a guess at a
        // typical packet's worth; treat the ratio, not the absolute number, as
        // the signal.
        private const int HeadSample = 32;
        private static readonly int[] s_byType = new int[4];
        private static readonly int[] s_headByType = new int[4];
        private static readonly string[] s_typeNames = { "Default", "Prioritized", "Solid", "Terrain" };
        private static readonly string[] s_typeShort = { "D", "P", "S", "T" };

        internal static void Record(int count)
        {
            s_calls++;
            s_candidates += count;
            if (count > s_max) s_max = count;
        }

        /// <summary>Count what kinds of objects a sampled sync list contains.</summary>
        internal static void SampleContents(List<ZDO> toSync)
        {
            if (toSync == null || toSync.Count == 0) return;
            // Always sample a burst. The 2026-09-28 disconnect produced a single
            // 3776-object list in a window with only ONE send — every other send
            // was refused because the queue was over the ceiling — and the
            // every-30th rule sampled none of it, losing exactly the list we
            // most wanted to see.
            if (toSync.Count < BurstThreshold && s_calls % SampleEveryNth != 0) return;
            var scene = ZNetScene.instance;
            if (scene == null) return;

            s_sampledLists++;
            // Indexed rather than foreach: position in the sorted list is the
            // whole point of the head/tail split.
            for (int i = 0; i < toSync.Count; i++)
            {
                var zdo = toSync[i];

                int t = (int)zdo.Type;
                if (t >= 0 && t < s_byType.Length)
                {
                    s_byType[t]++;
                    if (i < HeadSample) s_headByType[t]++;
                }

                var prefab = scene.GetPrefab(zdo.GetPrefab());
                string baseName = prefab != null ? prefab.name : "?";
                // Tag the prefab with its type, so one line answers both "what
                // are we offering" and "what type is it". A prefab carries a
                // single m_type, so this does not fragment the histogram.
                string name = t >= 0 && t < s_typeShort.Length
                    ? baseName + "/" + s_typeShort[t]
                    : baseName;
                if (s_byPrefab.TryGetValue(name, out int seen)) s_byPrefab[name] = seen + 1;
                else if (s_byPrefab.Count < MaxPrefabsTracked) s_byPrefab[name] = 1;
                else
                {
                    s_byPrefab.TryGetValue("(other)", out int other);
                    s_byPrefab["(other)"] = other + 1;
                }
            }
        }

        internal static string DrainStats(int peersForRate, float windowSeconds)
        {
            if (s_calls == 0) return null;

            // v0.7.27: print enough prefabs to ACCOUNT for the list, and say so.
            // The old Take(6) showed 290 of 438 objects on 2026-10-01 and silently
            // dropped the remaining third, which led to the list being described
            // as "basically all mobs" when a third of it had never been looked at.
            // The "shown N of M" suffix makes any remaining blind spot visible
            // instead of inviting the same mistake again.
            string top = "none";
            int shown = 0;
            if (s_byPrefab.Count > 0)
            {
                var ordered = s_byPrefab.OrderByDescending(kv => kv.Value).Take(PrefabsPrinted).ToList();
                foreach (var kv in ordered) shown += kv.Value;
                top = string.Join(" ", ordered.Select(kv => $"{kv.Key}×{kv.Value}"));
            }

            // Sends per peer per second is the number the fix targets: vanilla
            // gives ~12 at one player and ~3 at eight, because it divides one
            // round between them. Computed here so before/after is readable
            // straight off the line.
            float perPeerHz = peersForRate > 0
                ? s_calls / (float)peersForRate / windowSeconds
                : 0f;

            // Types offered vs types in the head of the sorted list. A type that
            // is a large share of `offered` but a small share of `head` is one
            // being starved by the sort; the reverse is one crowding others out.
            string types = "none";
            int typeTotal = 0;
            foreach (int n in s_byType) typeTotal += n;
            if (typeTotal > 0)
            {
                var offered = new List<string>();
                var head = new List<string>();
                for (int i = 0; i < s_byType.Length; i++)
                {
                    if (s_byType[i] > 0) offered.Add($"{s_typeNames[i]}×{s_byType[i]}");
                    if (s_headByType[i] > 0) head.Add($"{s_typeShort[i]}×{s_headByType[i]}");
                }
                types = string.Join(" ", offered)
                      + $" | head{HeadSample}: " + (head.Count > 0 ? string.Join(" ", head) : "none");
            }

            string s = $"syncCandidates={s_candidates} over {s_calls} sends " +
                       $"({perPeerHz:F1}/s per peer, max {s_max}/send) " +
                       $"| types: {types}" +
                       $" | contents ({s_sampledLists} lists, shown {shown} of {typeTotal}" +
                       $"{(s_byPrefab.Count > PrefabsPrinted ? $", {s_byPrefab.Count} prefabs" : "")}): {top}";
            s_calls = 0; s_candidates = 0; s_max = 0; s_sampledLists = 0;
            s_byPrefab.Clear();
            Array.Clear(s_byType, 0, s_byType.Length);
            Array.Clear(s_headByType, 0, s_headByType.Length);
            return s;
        }
    }

    /// <summary>
    /// v0.7.17: give every peer vanilla's send rate instead of dividing it
    /// among them.
    ///
    /// Vanilla's ZDOMan.SendZDOToPeers2 services ONE peer per frame, with a
    /// 0.05s pause between full rounds. A round therefore costs
    /// 0.05s + peers x frameTime, and each peer gets exactly one send per
    /// round, so the rate each player hears from the server DIVIDES by player
    /// count — measured on this server: ~12/sec at 1 peer (matching our logged
    /// ~300 sends per 20s window), ~5/sec at 4, ~3/sec at 8. It is a schedule
    /// limit, not a load or bandwidth limit: 8 players on an idle server get
    /// the same 3/sec as 8 on a hammered one.
    ///
    /// Vanilla tolerates it because the player nearest a mob OWNS that mob and
    /// simulates it locally at full rate — the trickle only carries things you
    /// are not simulating yourself. Our design owns every mob on the server, so
    /// every creature's motion depends on this rate, and at 3 updates/sec mobs
    /// visibly step and teleport while the player's own movement stays smooth.
    /// That is exactly the symptom reported from the 8-player session.
    ///
    /// The 0.05s pause says vanilla considers 20Hz per peer acceptable (that is
    /// what a lone player gets), so targeting that rate for everyone is not
    /// exceeding vanilla's intent — it applies the cap per peer instead of per
    /// round. Cost is more CreateSyncList work per second, which is why
    /// PassTimer.PeerSend measures this path.
    ///
    /// Plugin.DebugPeerSendHz = 0 keeps vanilla's scheduler untouched.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "SendZDOToPeers2")]
    internal static class ZDOMan_SendZDOToPeers2_Rate_Patch
    {
        private static readonly FieldInfo s_peersField = AccessTools.Field(typeof(ZDOMan), "m_peers");
        private static readonly MethodInfo s_sendZdos = AccessTools.Method(typeof(ZDOMan), "SendZDOs");

        private static float s_budget;
        private static int s_cursor;

        // ZDOPeer is a private nested type, so resolve its m_peer field off the
        // live object rather than naming the type. ZNetPeer itself is public, so
        // once we hold it no further reflection is needed. Cached on first use —
        // this runs several times per frame.
        private static FieldInfo s_zdoPeerInnerField;

        private static ZNetPeer GetNetPeer(object zdoPeer)
        {
            if (zdoPeer == null) return null;
            if (s_zdoPeerInnerField == null)
                s_zdoPeerInnerField = AccessTools.Field(zdoPeer.GetType(), "m_peer");
            return s_zdoPeerInnerField?.GetValue(zdoPeer) as ZNetPeer;
        }

        static bool Prefix(ZDOMan __instance, float dt)
        {
            if (Plugin.DebugPeerSendHz <= 0f) return true;      // vanilla scheduler
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            if (s_peersField == null || s_sendZdos == null) return true;
            if (!(s_peersField.GetValue(__instance) is System.Collections.IList peers)
                || peers.Count == 0)
            {
                s_budget = 0f;
                return false;
            }

            long started = PassTimer.Now;

            // Sends owed this frame so that EACH peer gets DebugPeerSendHz per
            // second: rate x peers, accumulated so fractional sends aren't lost.
            s_budget += dt * Plugin.DebugPeerSendHz * peers.Count;
            int sends = (int)s_budget;
            if (sends > 0)
            {
                s_budget -= sends;
                // Cap per frame so a server hitch can't turn into a burst that
                // fills every peer's queue at once.
                int cap = Mathf.Max(1, peers.Count * 2);
                if (sends > cap) { sends = cap; s_budget = 0f; }

                for (int i = 0; i < sends; i++)
                {
                    if (peers.Count == 0) break;
                    s_cursor = (s_cursor + 1) % peers.Count;
                    var zdoPeer = peers[s_cursor];
                    var netPeer = GetNetPeer(zdoPeer);
                    string name = netPeer == null || string.IsNullOrEmpty(netPeer.m_playerName)
                        ? "?"
                        : netPeer.m_playerName;

                    // Read the queue BEFORE the call so a refusal can be
                    // attributed. SendZDOs bails on a backed-up queue, but it
                    // ALSO bails when CreateSyncList found nothing to send, and
                    // those two mean opposite things — the first is starvation,
                    // the second is a healthy idle peer. Vanilla reads the same
                    // value as its own first line, so this doubles one cheap
                    // call rather than adding a new kind of work.
                    int preQueue = netPeer?.m_socket != null
                        ? netPeer.m_socket.GetSendQueueSize()
                        : 0;

                    // The 10KB gate inside SendZDOs still applies, so a peer
                    // that is already backed up costs almost nothing here.
                    //
                    // SendZDOs returns true only when it actually invoked the
                    // ZDOData RPC (vanilla ends `return flag2 || flag`), so this
                    // is the real delivered signal. v0.7.23 discarded it and
                    // counted every attempt as a send, which reported the rate
                    // we TRIED at rather than the rate players received.
                    // Bracket the call so the ZDO.Serialize census only counts
                    // serialisation done for this send, not for a world save.
                    // The peer's position goes with it: Serialize cannot see who
                    // the packet is for, and distance-to-receiver is what makes
                    // the census interpretable.
                    var refPos = netPeer != null ? netPeer.GetRefPos() : Vector3.zero;
                    SendCensus.CurrentPeerPos = refPos;
                    SendCensus.Capturing = true;

                    // Park the peer identity for the duration of the call: the
                    // tier filter runs inside ServerSortSendZDOS and the pack
                    // recorder inside ZDO.Serialize, and neither can bind the
                    // ZDOPeer parameter (private nested type). Resolve the map
                    // once here rather than per ZDO — ZDOID.GetHashCode indexes
                    // a static list, so it is not a free field hash.
                    SendTarget.RefPos = refPos;
                    SendTarget.Uid = netPeer != null ? netPeer.m_uid : 0L;
                    SendTarget.Map = Plugin.DebugSendTiers && netPeer != null
                        ? SendTiers.MapFor(netPeer.m_uid)
                        : null;
                    SendTarget.Counters = Plugin.DebugSendTiers && netPeer != null
                        ? SendTiers.CountersFor(netPeer.m_uid, name)
                        : null;
                    SendTarget.Active = true;

                    object ret;
                    try
                    {
                        ret = s_sendZdos.Invoke(__instance, new object[] { zdoPeer, false });
                    }
                    finally
                    {
                        SendCensus.Capturing = false;
                        SendTarget.Active = false;
                        SendTarget.Map = null;
                        SendTarget.Counters = null;
                    }
                    bool sent = ret is bool b && b;
                    SendPressure.RecordAttempt(name, sent, preQueue);
                }
                PassTimer.Record(PassTimer.PeerSend, started);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "CreateSyncList")]
    internal static class ZDOMan_CreateSyncList_Count_Patch
    {
        static void Postfix(List<ZDO> toSync)
        {
            SyncVolume.Record(toSync?.Count ?? 0);
            SyncVolume.SampleContents(toSync);
        }
    }

    internal static class ReclaimStats
    {
        private static int s_passes, s_scanned, s_ownerChanges, s_maxInPass, s_inCurrentPass;

        internal static void CountScanned() => s_scanned++;

        internal static void CountOwnerChange()
        {
            s_ownerChanges++;
            s_inCurrentPass++;
        }

        /// <summary>Called at the end of each ReleaseZDOS pass.</summary>
        internal static void EndPass()
        {
            s_passes++;
            if (s_inCurrentPass > s_maxInPass) s_maxInPass = s_inCurrentPass;
            s_inCurrentPass = 0;
        }

        internal static string Drain(int peers)
        {
            if (s_passes == 0) return null;
            // Each owner change is re-sent to every peer, so this is the forced
            // ZDO volume our reclaim alone put on the wire this window.
            int forcedSends = s_ownerChanges * Mathf.Max(peers, 1);
            string s = $"reclaims={s_ownerChanges}/{s_passes}pass (max {s_maxInPass}/pass) " +
                       $"scanned={s_scanned} forcedZdoSends~{forcedSends}" +
                       $" containerGrace={System.Threading.Interlocked.Exchange(ref ZDOMan_ReleaseZDOS_Patch.s_containerGraceSkips, 0)}";
            s_passes = 0; s_scanned = 0; s_ownerChanges = 0; s_maxInPass = 0;
            return s;
        }
    }

    internal static class SendPressure
    {
        internal const int VanillaCeilingBytes = 10240;

        // Vanilla refuses a send whenever the queue leaves it under 2048 bytes
        // of payload room (`num = 10240 - queue; if (num < 2048)`), so the
        // effective backpressure threshold is 8192, not the 10240 ceiling.
        internal const int PayloadFloorBytes = VanillaCeilingBytes - 2048;

        private const float SampleIntervalSeconds = 0.25f;

        private static float s_nextSampleAt;
        private static int s_samples, s_overCeiling;
        private static int s_maxQueue;
        private static string s_maxQueuePeer = "-";
        private static int s_totalAttempts, s_totalDelivered;

        // Per-peer detail (v0.7.12). "Everyone is starved" and "one person has a
        // bad connection" need completely different fixes — the first points at
        // the server sending too much, the second at that player's link — and the
        // old worst-peer-only summary could not tell them apart.
        private sealed class PeerStat
        {
            public int Samples;
            public int Over;
            public int MaxQueue;

            // Unbroken runs over the ceiling (v0.7.13). The percentage alone
            // cannot tell 20 quarter-second blips from one five-second stall,
            // and only the latter is something a player would feel — the 24%
            // measured solo on 2026-09-25 came with no perceptible lag at all.
            // Running totals only: no lists, no allocation, a few ints per
            // sample, so this costs nothing next to the queue read itself.
            public int CurrentRun;      // samples, 4 per second
            public int RunsOverOneSec;  // runs that lasted >= 1s
            public int LongestRun;      // samples

            // Attempted vs DELIVERED (v0.7.24). The averaged "sends/s per peer"
            // hides the distribution: at 8 players a mean of 14/s is consistent
            // with one player on 6/s and another on 18/s, and the player on 6/s
            // is the one still reporting stutter. Only counted while
            // DebugPeerSendHz > 0, since vanilla's scheduler bypasses our patch.
            //
            // Delivered is the number that matters: it is the update rate of
            // every mob that player is watching — their mob "framerate". v0.7.23
            // counted attempts and called them deliveries, overstating it by
            // roughly 3x once the gate started refusing.
            public int Attempts;
            public int Delivered;
            public int Gated;   // refused because the queue was backed up
            public int Idle;    // refused because nothing had changed to send

            // Payload headroom (10240 - queue) summed over delivered sends.
            // Vanilla truncates a send at that many bytes, so a small average
            // means sends are landing but squeezed, carrying only the nearest
            // few mobs rather than everything that moved.
            public long BudgetSum;
        }
        private const int SamplesPerSecond = 4;
        private static readonly Dictionary<string, PeerStat> s_byPeer =
            new Dictionary<string, PeerStat>();

        /// <summary>
        /// Called by the send-rate patch for every peer it services, with
        /// whether vanilla actually sent and what the peer's queue looked like
        /// beforehand, so we can report delivered rate — and the reason for any
        /// shortfall — per player rather than an average of attempts.
        /// </summary>
        internal static void RecordAttempt(string peerName, bool sent, int preQueue)
        {
            if (string.IsNullOrEmpty(peerName)) peerName = "?";
            if (!s_byPeer.TryGetValue(peerName, out var stat))
            {
                stat = new PeerStat();
                s_byPeer[peerName] = stat;
            }

            stat.Attempts++;
            s_totalAttempts++;
            if (sent)
            {
                stat.Delivered++;
                s_totalDelivered++;
                stat.BudgetSum += Mathf.Max(0, VanillaCeilingBytes - preQueue);
            }
            // Both of vanilla's backpressure gates bail above 8192: the outright
            // `queue > 10240` check, and then `num = 10240 - queue; if (num <
            // 2048) return false`. Below that floor a refusal means the sync
            // list was empty, which is a quiet peer, not a starved one.
            else if (preQueue > PayloadFloorBytes) stat.Gated++;
            else stat.Idle++;
        }

        internal static void Sample()
        {
            if (Time.time < s_nextSampleAt || ZNet.instance == null) return;
            s_nextSampleAt = Time.time + SampleIntervalSeconds;

            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer?.m_socket == null) continue;
                int queued = peer.m_socket.GetSendQueueSize();
                string name = string.IsNullOrEmpty(peer.m_playerName) ? "?" : peer.m_playerName;

                s_samples++;
                if (queued > s_maxQueue)
                {
                    s_maxQueue = queued;
                    s_maxQueuePeer = name;
                }

                if (!s_byPeer.TryGetValue(name, out var stat))
                {
                    stat = new PeerStat();
                    s_byPeer[name] = stat;
                }
                stat.Samples++;
                if (queued > stat.MaxQueue) stat.MaxQueue = queued;
                if (queued <= VanillaCeilingBytes)
                {
                    // Under the ceiling: bank any run that was in progress.
                    if (stat.CurrentRun > 0)
                    {
                        if (stat.CurrentRun >= SamplesPerSecond) stat.RunsOverOneSec++;
                        stat.CurrentRun = 0;
                    }
                    continue;
                }

                s_overCeiling++;
                stat.Over++;
                stat.CurrentRun++;
                // Track the longest as it grows, so a run still in progress when
                // the window ends is still reflected.
                if (stat.CurrentRun > stat.LongestRun) stat.LongestRun = stat.CurrentRun;
            }
        }

        /// <summary>Summary for the [Perf] line, then reset. Null when idle.</summary>
        internal static string Drain(float windowSeconds)
        {
            if (s_samples == 0) return null;

            // Per-peer: "name starved% (maxKB)", worst first. With several peers
            // this is the line that separates a server-side flood (everyone high)
            // from one bad connection (one name high, the rest near zero).
            // Worst first. Once the rate patch is active the refusal fraction is
            // the sharper signal, so rank on that and fall back to starvation
            // when vanilla's scheduler is running and we recorded no attempts.
            var perPeer = new List<string>();
            foreach (var kv in s_byPeer.OrderByDescending(kv =>
                         kv.Value.Attempts > 0
                             ? 1f - (float)kv.Value.Delivered / kv.Value.Attempts
                             : (kv.Value.Samples == 0
                                 ? 0f
                                 : (float)kv.Value.Over / kv.Value.Samples)))
            {
                var st = kv.Value;
                if (st.Samples == 0) continue;
                // Sustained runs are the part that should track felt lag, so
                // only mention them when there was one worth a second of stall.
                string runs = st.LongestRun >= SamplesPerSecond
                    ? $" runs≥1s={st.RunsOverOneSec} longest={(float)st.LongestRun / SamplesPerSecond:F1}s"
                    : "";
                // Delivered rate for THIS peer, with the attempted rate beside it
                // so the gap is visible: "5.2/s of 14.5" says the gate ate two
                // thirds of what we scheduled. gate% vs idle% splits the reason
                // — gate is backpressure, idle just means nothing changed — and
                // the KB figure is the average payload room a delivered send
                // had, so a low number means sends land but carry few mobs.
                string rate = "";
                if (windowSeconds > 0f && st.Attempts > 0)
                {
                    float budgetKB = st.Delivered > 0
                        ? (float)st.BudgetSum / st.Delivered / 1024f
                        : 0f;
                    rate = $" {st.Delivered / windowSeconds:F1}/s of {st.Attempts / windowSeconds:F1}"
                         + $" (gate {st.Gated * 100 / st.Attempts}%"
                         + $" idle {st.Idle * 100 / st.Attempts}%"
                         + $" {budgetKB:F1}KB)";
                }
                perPeer.Add(
                    $"{kv.Key} {st.Over * 100 / st.Samples}% ({st.MaxQueue / 1024f:F0}KB){rate}{runs}");
            }

            // Headline delivered/attempted across all peers, so the [Perf] line
            // answers "is the gate eating our sends" without reading the list.
            string sends = s_totalAttempts > 0
                ? $"sent={s_totalDelivered}/{s_totalAttempts} "
                : "";

            string summary =
                $"sendQueue max={s_maxQueue / 1024f:F0}KB ({s_maxQueuePeer}) " +
                $"starved={s_overCeiling}/{s_samples} " + sends +
                $"[{string.Join(", ", perPeer)}]";

            s_samples = 0; s_overCeiling = 0; s_maxQueue = 0; s_maxQueuePeer = "-";
            s_totalAttempts = 0; s_totalDelivered = 0;
            s_byPeer.Clear();
            return summary;
        }
    }

    /// <summary>
    /// v0.7.28: what the server ACTUALLY sends, per prefab, with byte counts.
    ///
    /// Everything before this measured CANDIDATES — the list CreateSyncList
    /// offers. But vanilla packs that sorted list only until the packet is full
    /// (`if (zPackage.Size() &gt; num) break;`), so offers and sends are different
    /// things, and on 2026-10-01 a third of a candidate list went unexamined
    /// because only the top 6 prefabs were printed.
    ///
    /// This hooks ZDO.Serialize, which vanilla calls exactly once per ZDO that
    /// made the cut, and which hands us the serialized ZPackage so the byte cost
    /// is measured rather than estimated. Serialize is also used by world saves,
    /// so it only counts while the send path has set Capturing.
    ///
    /// Distinct ZDOs are tracked alongside send counts: 1,644 sends of
    /// "Greydwarf" could be 22 greydwarves updating 75 times each or 800
    /// updating twice, and those imply completely different fixes.
    /// </summary>
    internal static class SendCensus
    {
        /// <summary>Set by the send path so saves aren't counted as sends.</summary>
        internal static bool Capturing;

        /// <summary>
        /// Reference position of the peer currently being served, stashed by the
        /// send path because ZDO.Serialize has no idea who the packet is for.
        /// Distance to the RECEIVER is the right axis here: it is the same one
        /// vanilla sorts on (ServerSortSendZDOS uses peer.m_peer.GetRefPos()),
        /// so it answers "are we spending this player's byte budget on things
        /// far away from them".
        /// </summary>
        internal static Vector3 CurrentPeerPos;

        private sealed class Entry
        {
            public string Name;
            public string Kind;          // mob / player / projectile / other
            public int Sends;
            public long Bytes;
            public double DistSum;       // for a mean; mean-of-sqrt needs the sqrt
            public readonly HashSet<ZDOID> Distinct = new HashSet<ZDOID>();

            // ZDO.Distant, copied from the prefab's ZNetView.m_distant — so it
            // is a property of the KIND of object, not of how far away this one
            // happens to be. It decides eligibility for the outer-ring distant
            // tier (~160-288m), which is gated behind `toSync.Count < 10`.
            // Inside the near box the flag is irrelevant: FindObjects AddRanges
            // the whole sector without consulting it. Prefab-serialised, so the
            // decompile cannot tell us — only runtime can.
            public bool Distant;
        }

        // Distance bands, in metres. 20 = melee/visual, 50 = BaseAI.m_viewRange,
        // 160 = the near-box axis reach at near=2, beyond = distant-tier objects.
        // Per band we keep sends, bytes AND distinct, because sends/distinct per
        // band is the number that settles whether FAR things are getting a high
        // update rate (pathological) or just many far things updating once each
        // (expected).
        private static readonly float[] s_bandEdges = { 20f, 50f, 100f, 160f, float.MaxValue };
        private static readonly string[] s_bandNames = { "0-20m", "20-50m", "50-100m", "100-160m", "160m+" };
        private static readonly int[] s_bandSends = new int[5];
        private static readonly long[] s_bandBytes = new long[5];
        private static readonly HashSet<ZDOID>[] s_bandDistinct =
        {
            new HashSet<ZDOID>(), new HashSet<ZDOID>(), new HashSet<ZDOID>(),
            new HashSet<ZDOID>(), new HashSet<ZDOID>()
        };

        private static readonly Dictionary<int, Entry> s_byPrefab = new Dictionary<int, Entry>();
        private static int s_totalSends;
        private static long s_totalBytes;

        // Component lookups are reflection-free but still a GetComponent per
        // prefab, so classify once per prefab hash and reuse.
        private static readonly Dictionary<int, string> s_kindCache = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> s_nameCache = new Dictionary<int, string>();

        private static void Classify(int prefabHash, out string name, out string kind)
        {
            if (s_kindCache.TryGetValue(prefabHash, out kind)
                && s_nameCache.TryGetValue(prefabHash, out name)) return;

            name = prefabHash.ToString();
            kind = "other";
            var scene = ZNetScene.instance;
            if (scene != null)
            {
                var prefab = scene.GetPrefab(prefabHash);
                if (prefab != null)
                {
                    name = prefab.name;
                    // Player before Character: Player derives from Character, and
                    // "all the mobs" must not silently include the players.
                    if (prefab.GetComponent<Player>() != null) kind = "player";
                    else if (prefab.GetComponent<Character>() != null) kind = "mob";
                    else if (prefab.GetComponent<Projectile>() != null) kind = "projectile";
                }
            }
            s_kindCache[prefabHash] = kind;
            s_nameCache[prefabHash] = name;
        }

        internal static void Record(ZDO zdo, int bytes)
        {
            if (zdo == null) return;
            int hash = zdo.GetPrefab();
            if (!s_byPrefab.TryGetValue(hash, out var e))
            {
                Classify(hash, out string name, out string kind);
                e = new Entry { Name = name, Kind = kind, Distant = zdo.Distant };
                s_byPrefab[hash] = e;
            }
            e.Sends++;
            e.Bytes += bytes;
            e.Distinct.Add(zdo.m_uid);
            s_totalSends++;
            s_totalBytes += bytes;

            float dist = Utils.DistanceXZ(zdo.GetPosition(), CurrentPeerPos);
            e.DistSum += dist;
            for (int i = 0; i < s_bandEdges.Length; i++)
            {
                if (dist < s_bandEdges[i])
                {
                    s_bandSends[i]++;
                    s_bandBytes[i] += bytes;
                    s_bandDistinct[i].Add(zdo.m_uid);
                    break;
                }
            }
        }

        internal static void Emit(float windowSeconds)
        {
            if (s_totalSends == 0) { Reset(); return; }

            int distinctAll = 0, distinctMobs = 0, distinctPlayers = 0,
                distinctProjectiles = 0, distinctOther = 0;
            foreach (var e in s_byPrefab.Values)
            {
                int d = e.Distinct.Count;
                distinctAll += d;
                switch (e.Kind)
                {
                    case "mob":        distinctMobs += d; break;
                    case "player":     distinctPlayers += d; break;
                    case "projectile": distinctProjectiles += d; break;
                    default:           distinctOther += d; break;
                }
            }

            int mobSends = 0;
            foreach (var e in s_byPrefab.Values) if (e.Kind == "mob") mobSends += e.Sends;

            Plugin.Log.LogMessage(
                $"===[CENSUS {windowSeconds:F1}s]=== sends={s_totalSends} " +
                $"({s_totalSends / windowSeconds:F0}/s) bytes={s_totalBytes / 1024f:F0}KB " +
                $"| distinct ZDOs={distinctAll} (mobs {distinctMobs}, players {distinctPlayers}, " +
                $"projectiles {distinctProjectiles}, other {distinctOther}) " +
                $"| mob share of sends {(s_totalSends > 0 ? 100f * mobSends / s_totalSends : 0f):F1}% " +
                $"| prefabs={s_byPrefab.Count}");

            // Distance bands first: this is the line that says whether the byte
            // budget is going to things near the player or far from them.
            var bands = new List<string>();
            for (int i = 0; i < s_bandSends.Length; i++)
            {
                if (s_bandSends[i] == 0) continue;
                int d = s_bandDistinct[i].Count;
                bands.Add(
                    $"{s_bandNames[i]} {100f * s_bandSends[i] / s_totalSends:F0}% " +
                    $"({s_bandSends[i]} sends/{d} distinct = {(d > 0 ? s_bandSends[i] / (float)d : 0f):F1}x, " +
                    $"{s_bandBytes[i] / 1024f:F0}KB)");
            }
            Plugin.Log.LogMessage($"[Census bands] {string.Join("  ", bands)}");

            foreach (var e in s_byPrefab.Values.OrderByDescending(x => x.Sends))
            {
                Plugin.Log.LogMessage(
                    $"[Census] {100f * e.Sends / s_totalSends,5:F1}% {e.Sends,6} sends " +
                    $"{e.Bytes / 1024f,7:F1}KB {e.Bytes / (float)e.Sends,5:F0}B/send " +
                    $"{e.Distinct.Count,4} distinct {e.Sends / (float)e.Distinct.Count,5:F1}x " +
                    $"{e.DistSum / e.Sends,5:F0}m {(e.Distant ? "DIST" : "near"),-5} " +
                    $"{e.Kind,-10} {e.Name}");
            }
            Reset();
        }

        private static void Reset()
        {
            s_byPrefab.Clear();
            s_totalSends = 0;
            s_totalBytes = 0;
            Array.Clear(s_bandSends, 0, s_bandSends.Length);
            Array.Clear(s_bandBytes, 0, s_bandBytes.Length);
            foreach (var set in s_bandDistinct) set.Clear();
        }
    }

    /// <summary>
    /// Fires once per ZDO vanilla writes into an outgoing packet, so this is
    /// the authoritative "it really was sent" signal. World saves serialize
    /// too, hence the SendTarget.Active gate.
    ///
    /// Order matters: the tier bookkeeping is UNCONDITIONAL while the verbose
    /// census stays behind its own knob. Hanging the throttle's state on
    /// DebugSendCensusSeconds would mean switching the census off silently
    /// turned the throttle into a no-op.
    /// </summary>
    [HarmonyPatch(typeof(ZDO), nameof(ZDO.Serialize))]
    internal static class ZDO_Serialize_Census_Patch
    {
        static void Postfix(ZDO __instance, ZPackage pkg)
        {
            if (!SendTarget.Active) return;
            if (Plugin.DebugSendTiers) SendTiers.RecordPacked(__instance);
            if (Plugin.DebugSendCensusSeconds > 0f)
                SendCensus.Record(__instance, pkg != null ? pkg.Size() : 0);
        }
    }



    internal static class SendTarget
    {
        internal static bool Active;
        internal static long Uid;
        internal static int Seq;                                 // this send's opportunity number for that peer
        internal static Vector3 RefPos;
        internal static Dictionary<ZDOID, SendTiers.Sent> Map;   // resolved once per send
        internal static SendTiers.PeerCounters Counters;         // likewise, so increments stay array indexes
    }

    /// <summary>
    /// v0.7.30: cap how often each peer hears about a given ZDO, by distance.
    ///
    /// Vanilla sends every changed ZDO in range on every send opportunity. It
    /// has no per-object rate concept at all: distance enters only through
    /// ServerSortSendZDOS's sort value, which decides ORDER, and order is
    /// irrelevant whenever the whole list fits in the packet. So a creature
    /// 150m away is refreshed exactly as often as one swinging at your face.
    ///
    /// Filtering happens in a Prefix on ServerSortSendZDOS, which is before
    /// vanilla's `if (toSync.Count < 10)` distant-append and before
    /// AddForceSendZdos. Two consequences, both wanted: force-sends bypass the
    /// cap (they are urgent by construction), and a shortened list makes the
    /// distant-append fire more often, which should help the late tree/structure
    /// pop-in. The cost is that far-tier `packed` can exceed `offered`, so `cut`
    /// is printed signed — a negative far-tier cut IS that pop-in relief.
    /// </summary>
    internal static class SendTiers
    {
        internal struct Sent
        {
            public int LastSend;      // opportunity number of the last pack
            public ushort OwnerRev;   // to spot ownership changes, which must not wait
            public byte Tier;         // tier that governed the last send
            public ushort InWindow;   // packs this accounting window
        }

        // Per peer, per ZDO. Our own map rather than reflecting into
        // ZDOPeer.m_zdos[uid].m_syncTime: that is authoritative but it is a
        // private nested struct inside a private nested class, so every read
        // would box twice, up to ~600k times/sec. And drift is asymmetric in our
        // favour — this filter runs AFTER ShouldSend, so it can only ever delay
        // something vanilla already chose to send, never cause a send. Thinking
        // an entry is older than it is just means less throttling.
        private static readonly Dictionary<long, Dictionary<ZDOID, Sent>> s_byPeer =
            new Dictionary<long, Dictionary<ZDOID, Sent>>();

        private static readonly float[] s_edgeSq;
        private static readonly int[] s_stride;

        // Monotonic count of send opportunities per peer. This is the clock —
        // there is no time-based arithmetic anywhere in the cap any more.
        private static readonly Dictionary<long, int> s_seq = new Dictionary<long, int>();

        /// <summary>
        /// Per-tier accounting for one peer for one window (v0.7.35).
        ///
        /// Previously these were single static arrays summed across every peer,
        /// which made the output unusable for the thing it is for: the cap is
        /// applied per peer, so "how is this player being served" cannot be read
        /// out of a blended total. Eight players in eight different situations
        /// average into something true of none of them.
        ///
        /// Costs nothing on the hot path. SendTarget already resolves per-send
        /// state once per send (not per candidate), so the counter block is
        /// resolved in the same place and every increment stays a direct array
        /// index. One extra dictionary lookup per send, ~160/sec at 8 players.
        ///
        /// Exemptions are split by cause: the combined figure ran at 38% of all
        /// candidates, too large to leave unattributed. A high first-delivery
        /// share means movement-driven re-delivery (peers forget ZDOs via
        /// ZDOSectorInvalidated as sectors cycle); a high ownership share would
        /// point at our own reclaim pass.
        /// </summary>
        internal sealed class PeerCounters
        {
            public string Name = "?";
            public readonly int[] Cand, ExUnlim, ExFirst, ExOwner, Thr, Packed;
            public readonly int[] Pairs, MaxIn, Sat;
            public int Served, Emptied;

            public PeerCounters(int tiers)
            {
                Cand = new int[tiers]; ExUnlim = new int[tiers]; ExFirst = new int[tiers];
                ExOwner = new int[tiers]; Thr = new int[tiers]; Packed = new int[tiers];
                Pairs = new int[tiers]; MaxIn = new int[tiers]; Sat = new int[tiers];
            }

            public void ResetWindow()
            {
                Array.Clear(Cand, 0, Cand.Length); Array.Clear(ExUnlim, 0, ExUnlim.Length);
                Array.Clear(ExFirst, 0, ExFirst.Length); Array.Clear(ExOwner, 0, ExOwner.Length);
                Array.Clear(Thr, 0, Thr.Length); Array.Clear(Packed, 0, Packed.Length);
                Array.Clear(Pairs, 0, Pairs.Length); Array.Clear(MaxIn, 0, MaxIn.Length);
                Array.Clear(Sat, 0, Sat.Length);
                Served = 0; Emptied = 0;
            }
        }

        private static readonly Dictionary<long, PeerCounters> s_counters =
            new Dictionary<long, PeerCounters>();
        private static int s_tiers;

        internal static PeerCounters CountersFor(long uid, string name)
        {
            if (!s_counters.TryGetValue(uid, out var c))
            {
                c = new PeerCounters(s_tiers);
                s_counters[uid] = c;
            }
            c.Name = string.IsNullOrEmpty(name) ? uid.ToString() : name;
            return c;
        }

        static SendTiers()
        {
            s_tiers = Plugin.SendTierEdges.Length;
            s_edgeSq = new float[s_tiers];
            s_stride = new int[s_tiers];
            for (int i = 0; i < s_tiers; i++)
            {
                float e = Plugin.SendTierEdges[i];
                s_edgeSq[i] = e >= float.MaxValue * 0.5f ? float.MaxValue : e * e;
                s_stride[i] = i < Plugin.SendTierEveryNth.Length
                    ? Mathf.Max(1, Plugin.SendTierEveryNth[i])
                    : 1;
            }
        }


        internal static string Describe()
        {
            var parts = new List<string>();
            for (int i = 0; i < s_tiers; i++)
            {
                string edge = Plugin.SendTierEdges[i] >= float.MaxValue * 0.5f
                    ? "inf" : $"{Plugin.SendTierEdges[i]:F0}m";
                // Rate is derived for readability only; the stride is the truth.
                parts.Add(s_stride[i] <= 1
                    ? $"<{edge} every send"
                    : $"<{edge} every {s_stride[i]}th (~{Plugin.DebugPeerSendHz / s_stride[i]:F1}/s)");
            }
            return string.Join(", ", parts);
        }

        /// <summary>Called once per send, so the two-level lookup stays off the per-ZDO path.</summary>
        internal static Dictionary<ZDOID, Sent> MapFor(long uid)
        {
            if (!s_byPeer.TryGetValue(uid, out var m))
            {
                m = new Dictionary<ZDOID, Sent>();
                s_byPeer[uid] = m;
            }
            return m;
        }

        private static int TierOf(float sqrDist)
        {
            for (int i = 0; i < s_tiers; i++) if (sqrDist < s_edgeSq[i]) return i;
            return s_tiers - 1;
        }

        /// <summary>
        /// Drop not-yet-due entries from the already-gathered candidate list.
        /// Compacts in place with two pointers — RemoveAt would be O(n^2) with a
        /// memmove per removal, and lists reached 3,776 entries in the
        /// 2026-09-29 session.
        /// </summary>
        internal static void Filter(List<ZDO> objects, Vector3 refPos)
        {
            if (objects == null || objects.Count == 0) return;
            var map = SendTarget.Map;
            var ctr = SendTarget.Counters;
            if (map == null || ctr == null) return;

            // This send's opportunity number for this peer. Incremented here
            // because Filter runs exactly once per send that got as far as
            // building a candidate list — a send refused by the byte gate never
            // reaches us, and should not advance anyone's turn.
            s_seq.TryGetValue(SendTarget.Uid, out int seq);
            seq++;
            s_seq[SendTarget.Uid] = seq;
            SendTarget.Seq = seq;

            int w = 0;
            for (int r = 0; r < objects.Count; r++)
            {
                var zdo = objects[r];
                bool keep = true;
                if (zdo != null)
                {
                    Vector3 d = zdo.GetPosition() - refPos;
                    // XZ only, squared: matches the census, and avoids a sqrt
                    // per candidate on a path that can see 600k/sec.
                    float sq = d.x * d.x + d.z * d.z;
                    int t = TierOf(sq);
                    ctr.Cand[t]++;

                    if (s_stride[t] <= 1) ctr.ExUnlim[t]++;                  // every send allowed
                    else if (!map.TryGetValue(zdo.m_uid, out var s)) ctr.ExFirst[t]++;  // peer has never seen it
                    else if (zdo.OwnerRevision != s.OwnerRev) ctr.ExOwner[t]++;         // ownership must not wait
                    else if (seq - s.LastSend < s_stride[t]) { keep = false; ctr.Thr[t]++; }
                }
                if (keep) objects[w++] = objects[r];
            }
            if (w < objects.Count) objects.RemoveRange(w, objects.Count - w);
            ctr.Served++;
            if (w == 0) ctr.Emptied++;
        }

        /// <summary>Called from the ZDO.Serialize postfix: this ZDO really was packed.</summary>
        internal static void RecordPacked(ZDO zdo)
        {
            var map = SendTarget.Map;
            if (map == null || zdo == null) return;

            Vector3 d = zdo.GetPosition() - SendTarget.RefPos;
            int t = TierOf(d.x * d.x + d.z * d.z);
            if (SendTarget.Counters != null) SendTarget.Counters.Packed[t]++;

            map.TryGetValue(zdo.m_uid, out var s);
            s.LastSend = SendTarget.Seq;
            s.OwnerRev = zdo.OwnerRevision;
            s.Tier = (byte)t;
            if (s.InWindow < ushort.MaxValue) s.InWindow++;
            map[zdo.m_uid] = s;
        }

        internal static void DropZdo(ZDOID id)
        {
            foreach (var m in s_byPeer.Values) m.Remove(id);
        }

        internal static void DropPeer(long uid)
        {
            s_byPeer.Remove(uid);
            s_seq.Remove(uid);
            s_counters.Remove(uid);
        }

        private static readonly List<ZDOID> s_scratch = new List<ZDOID>();

        /// <summary>
        /// Roll up the window and prune in one pass. Pruning is harmless: a
        /// dropped entry looks never-sent and goes out immediately, which the
        /// cap would have permitted anyway.
        /// </summary>
        internal static void EmitAndPrune(float window, bool print)
        {
            // Fold the per-(peer,ZDO) map into the owning peer's counters, so
            // pairs/max/sat are attributed to the player they describe.
            foreach (var kv in s_byPeer)
            {
                var map = kv.Value;
                if (!s_counters.TryGetValue(kv.Key, out var ctr)) continue;
                for (int i = 0; i < s_tiers; i++) { ctr.Pairs[i] = 0; ctr.MaxIn[i] = 0; ctr.Sat[i] = 0; }

                // Snapshot the keys: the pass both removes stale entries and
                // rewrites surviving ones, and a Dictionary cannot be mutated
                // while being enumerated.
                s_scratch.Clear();
                foreach (var id in map.Keys) s_scratch.Add(id);

                foreach (var id in s_scratch)
                {
                    var s = map[id];
                    if (s.InWindow == 0)
                    {
                        // Untouched this window. Dropping it is harmless — it
                        // then looks never-sent and goes out immediately, which
                        // the cap would have allowed anyway.
                        map.Remove(id);
                        continue;
                    }

                    int t = s.Tier < s_tiers ? s.Tier : s_tiers - 1;
                    ctr.Pairs[t]++;
                    if (s.InWindow > ctr.MaxIn[t]) ctr.MaxIn[t] = s.InWindow;
                    // Saturated = took at least 80% of the sends its stride
                    // permits. Expressed in opportunities, so no rate maths:
                    // the ceiling for this window is served/stride.
                    int ceiling = s_stride[t] <= 1 ? ctr.Served : ctr.Served / s_stride[t];
                    if (ceiling > 0 && s.InWindow >= 0.8f * ceiling) ctr.Sat[t]++;

                    s.InWindow = 0;
                    map[id] = s;
                }
            }

            // One block per player. Timestamped (v0.7.35) because without it the
            // lines cannot be aligned to [Perf], to wall clock, or to each other
            // when building a time series after the fact.
            foreach (var kv in s_counters)
            {
                var c = kv.Value;
                if (print && c.Served > 0)
                {
                    Plugin.Log.LogMessage(
                        $"[{Plugin.Stamp}] ===[TIERS {window:F1}s]=== peer={c.Name} " +
                        $"sends={c.Served} emptied={c.Emptied} | caps: {Describe()}");
                    for (int t = 0; t < s_tiers; t++)
                    {
                        int offered = c.Cand[t] - c.Thr[t];
                        int cut = offered - c.Packed[t];
                        string edge = Plugin.SendTierEdges[t] >= float.MaxValue * 0.5f
                            ? "inf" : $"{Plugin.SendTierEdges[t]:F0}m";
                        Plugin.Log.LogMessage(
                            $"[{Plugin.Stamp}] [Tier {c.Name}] <{edge,-5} " +
                            $"every={s_stride[t],-3} " +
                            $"cand={c.Cand[t],-7} new={c.ExFirst[t],-6} own={c.ExOwner[t],-5} " +
                            $"unl={c.ExUnlim[t],-6} thr={c.Thr[t],-7} " +
                            $"off={offered,-7} pk={c.Packed[t],-7} cut={cut,-7} " +
                            $"pairs={c.Pairs[t],-6} max={c.MaxIn[t] / window,5:F1}/s " +
                            $"sat={(c.Pairs[t] > 0 ? 100 * c.Sat[t] / c.Pairs[t] : 0),3}%");
                    }
                }
                c.ResetWindow();
            }
        }
    }

    /// <summary>
    /// v0.7.42: give projectiles their own priority band, just above Default.
    ///
    /// Vanilla's ServerSendCompare sorts the non-group-A remainder by
    /// ObjectType DESCENDING: Terrain(3), Solid(2), Prioritized(1), Default(0).
    /// Every projectile prefab measured on this server is Default, so a
    /// gjall_spit_projectile 7m from your face queues behind every standing
    /// tree and dungeon wall that happened to take damage. Measured 2026-10-05
    /// with 8 players, near-tier candidates were being cut 45-84% by the packet
    /// budget, so that ordering actively costs projectile updates — while the
    /// damage itself travels as a routed RPC on a separate path that is NOT
    /// packet-limited. Hence impacts and damage arriving before the projectile
    /// that caused them.
    ///
    /// The cost of fixing it is negligible: projectiles are ~34 bytes a send
    /// (a mob is ~250, a terrain blob ~6,000), so promoting the whole category
    /// moves a few hundred bytes per packet.
    ///
    /// Resulting order: group A (other players, boats), Terrain, Solid,
    /// Prioritized, PROJECTILES, Default.
    ///
    /// This is ORDERING ONLY. Projectiles still have to earn their turn through
    /// the distance stride in ZDOMan_ServerSortSendZDOS_Tier_Patch — stride 1
    /// inside 30m, stride 7 past 100m — exactly like everything else. Eligible
    /// and ordered are separate questions, decided in separate places.
    ///
    /// Implementation note: because vanilla has already sorted by the time this
    /// Postfix runs, every Default ZDO forms a contiguous run at the TAIL
    /// (Default cannot be in group A, and sorts last within the remainder). So
    /// we only locate that run and stable-partition projectiles to the front of
    /// it. Everything above is untouched, and relative order is preserved, so
    /// projectiles stay sorted by distance-minus-staleness among themselves.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
    internal static class ZDOMan_ServerSortSendZDOS_ProjectilePriority_Patch
    {
        // prefab hash -> is it a Projectile. Resolved once per prefab, then an
        // int lookup on the hot path rather than a GetComponent call.
        //
        // Deliberately Projectile and NOT the IProjectile interface: Aoe also
        // implements IProjectile but covers stationary zone effects
        // (GoblinShaman_protect_aoe, Fenring_attack_flames_aoe), which do not
        // fly at anyone and do not need promoting.
        private static readonly Dictionary<int, bool> s_isProjectile = new Dictionary<int, bool>();

        private static bool IsProjectile(int prefabHash)
        {
            if (s_isProjectile.TryGetValue(prefabHash, out bool v)) return v;
            v = false;
            var scene = ZNetScene.instance;
            if (scene != null)
            {
                var prefab = scene.GetPrefab(prefabHash);
                if (prefab != null) v = prefab.GetComponent<Projectile>() != null;
            }
            s_isProjectile[prefabHash] = v;
            return v;
        }

        static void Postfix(List<ZDO> __0)
        {
            if (!Plugin.DebugProjectilePriority) return;
            var objects = __0;
            if (objects == null || objects.Count < 2) return;

            // Walk back over the trailing Default run.
            int start = objects.Count;
            while (start > 0)
            {
                var z = objects[start - 1];
                if (z == null || z.Type != ZDO.ObjectType.Default) break;
                start--;
            }
            if (start >= objects.Count - 1) return;   // 0 or 1 Default entries

            // Stable partition within [start, Count): projectiles first.
            // Two passes into a scratch list keeps it stable and O(n); an
            // in-place rotation would not preserve relative order.
            s_front.Clear(); s_back.Clear();
            for (int i = start; i < objects.Count; i++)
            {
                var z = objects[i];
                if (z != null && IsProjectile(z.GetPrefab())) s_front.Add(z);
                else s_back.Add(z);
            }
            if (s_front.Count == 0) return;           // nothing to promote

            int w = start;
            for (int i = 0; i < s_front.Count; i++) objects[w++] = s_front[i];
            for (int i = 0; i < s_back.Count; i++) objects[w++] = s_back[i];

            Promoted += s_front.Count;
            Windows++;
        }

        private static readonly List<ZDO> s_front = new List<ZDO>();
        private static readonly List<ZDO> s_back = new List<ZDO>();

        internal static int Promoted, Windows;

        internal static string Drain()
        {
            if (Windows == 0) return null;
            string s = $"projPromoted={Promoted} over {Windows} sorts";
            Promoted = 0; Windows = 0;
            return s;
        }
    }

    /// <summary>
    /// Applies the distance cap to the candidate list. A Prefix, not a Postfix:
    /// removing entries then sorting gives the same result as sorting then
    /// removing, and this way vanilla never computes a sort value for anything
    /// we were going to drop.
    ///
    /// Bound positionally (__0/__1) rather than by name. This plugin has been
    /// disabled outright once before by a vanilla parameter rename breaking a
    /// name-bound patch and throwing out of PatchAll.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "ServerSortSendZDOS")]
    internal static class ZDOMan_ServerSortSendZDOS_Tier_Patch
    {
        static void Prefix(List<ZDO> __0, Vector3 __1)
        {
            if (!Plugin.DebugSendTiers || !SendTarget.Active) return;
            SendTiers.Filter(__0, __1);
        }
    }

    /// <summary>
    /// A peer forgets a ZDO when its sector leaves that peer's active area, so
    /// re-entry is a genuine first delivery. Without this our map would still
    /// claim we had sent it and would delay it by up to one tier period.
    /// Public method, public parameter, explicit type array — rename-proof.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.ZDOSectorInvalidated), new[] { typeof(ZDO) })]
    internal static class ZDOMan_ZDOSectorInvalidated_Tier_Patch
    {
        static void Postfix(ZDO __0)
        {
            if (!Plugin.DebugSendTiers || __0 == null) return;
            SendTiers.DropZdo(__0.m_uid);
        }
    }

    /// <summary>Forget a peer's history on connect and disconnect alike — a
    /// reconnecting player's ZDOPeer.m_zdos starts empty, so everything is a
    /// first delivery for them.</summary>
    [HarmonyPatch(typeof(ZDOMan))]
    internal static class ZDOMan_PeerLifecycle_Tier_Patch
    {
        // __0 rather than the parameter name. Vanilla calls it `netPeer`, not
        // `peer`, and binding by name failed on 2026-10-05 — which aborted
        // PatchAll and left the server half-patched. Positional injection
        // survives renames; only a signature reorder breaks it.
        [HarmonyPostfix, HarmonyPatch(nameof(ZDOMan.AddPeer), new[] { typeof(ZNetPeer) })]
        static void OnAdd(ZNetPeer __0)
        {
            if (Plugin.DebugSendTiers && __0 != null) SendTiers.DropPeer(__0.m_uid);
        }

        [HarmonyPostfix, HarmonyPatch(nameof(ZDOMan.RemovePeer), new[] { typeof(ZNetPeer) })]
        static void OnRemove(ZNetPeer __0)
        {
            if (Plugin.DebugSendTiers && __0 != null) SendTiers.DropPeer(__0.m_uid);
        }
    }

    /// <summary>
    /// v0.7.39: report each CookingStation prefab's m_recordCrafter flag once.
    ///
    /// WHY: on 2026-10-05 the mystical forge produced infinite items and would
    /// not release its mold. Cause is vanilla CookingStation.SpawnItem:
    ///
    ///   if (m_recordCrafter) {
    ///       component.m_itemData.m_crafterID = Player.m_localPlayer.GetPlayerID();
    ///
    /// Player.m_localPlayer is NULL on a dedicated server, so that throws — and
    /// the throw escapes RPC_RemoveDoneItem BEFORE its SetSlot(i, "", ...) and
    /// its RPC_SetSlotVisual, so the slot keeps its item and can be harvested
    /// again indefinitely. In vanilla the CLIENT owns the station and runs this
    /// locally where m_localPlayer exists; our server ownership moved it to a
    /// machine that has no local player. Textbook case of the hazard catalogued
    /// in the local-player-concept notes.
    ///
    /// The cooking station, iron cooking station and oven share this exact code
    /// and have never misbehaved, so they presumably leave the flag false —
    /// but m_recordCrafter is prefab-serialised and therefore invisible in the
    /// decompile. Only runtime can tell us, which is what this is for.
    ///
    /// Logs once per prefab, and only for stations that actually exist in a
    /// loaded zone — Unity never runs Awake on an uninstantiated prefab asset,
    /// so nothing unbuilt is revealed.
    /// </summary>
    [HarmonyPatch(typeof(CookingStation), "Awake")]
    internal static class CookingStation_Awake_CrafterFlag_Patch
    {
        private static readonly HashSet<string> s_seen = new HashSet<string>();

        static void Postfix(CookingStation __instance)
        {
            if (__instance == null) return;
            string name = __instance.gameObject != null
                ? __instance.gameObject.name.Replace("(Clone)", "")
                : "?";

            // THE FIX. Clearing the flag makes vanilla skip the block that
            // dereferences Player.m_localPlayer, so SpawnItem completes, the
            // slot is cleared and the mold is released. Measured 2026-10-06:
            // piece_FrostFoundry is the only station with this set — the oven,
            // cooking station and iron cooking station all leave it false,
            // which is why only the Frost Foundry ever misbehaved.
            //
            // Cost: items from affected stations carry no "Crafted by" name.
            // That is cosmetic, and the alternative (threading the sender's
            // player id from RPC_RemoveDoneItem through to the spawned
            // ItemDrop) needs three more patches on a method this plugin has
            // no other reason to touch. Not worth the fragility for a label —
            // this plugin has twice been broken by patch surface alone.
            //
            // Safe because the server is always the owner in our design, so
            // this code path only ever runs where m_localPlayer is null. It
            // does NOT affect clients: their own stations are unpatched.
            bool wasRisky = __instance.m_recordCrafter;
            if (wasRisky) __instance.m_recordCrafter = false;

            if (!s_seen.Add(name)) return;

            string line =
                $"[{Plugin.Stamp}] [Station] {name} recordCrafter={wasRisky} " +
                $"slots={(__instance.m_slots != null ? __instance.m_slots.Length : 0)} " +
                $"spawnPoint={(__instance.m_spawnPoint != null)}";

            if (wasRisky)
                Plugin.Log.LogWarning(line +
                    "  <-- would throw on harvest (infinite items); recordCrafter CLEARED by this plugin");
            else
                Plugin.Log.LogInfo(line);
        }
    }

    /// <summary>
    /// v0.7.41: measure terrain operations, and how many of them change nothing.
    ///
    /// WHY: a TerrainComp ZDO serialises its whole delta blob — measured at
    /// 6,108 bytes per send, ~18x a mob update — and ObjectType.Terrain sorts
    /// FIRST in ServerSendCompare. One terrain send therefore consumes ~60% of
    /// a 10KB packet and outranks everything else, while projectiles sort LAST
    /// (every projectile prefab logged on 2026-10-05 was /D = Default). With
    /// near-tier objects already 45-84% cut at 8 players, a burst of terrain
    /// ops would evict projectile position updates from the packets — while
    /// damage travels separately as a routed RPC and is NOT packet-limited.
    /// That matches the reported symptom: impacts and damage landing before the
    /// projectile that caused them.
    ///
    /// And vanilla saves unconditionally:
    ///   InternalDoOperation(pos, rot, modifier);
    ///   Save(paintOnly);     // no check for whether anything actually moved
    /// so an AoE on bare rock, already-flat ground, or terrain too hard to
    /// deform still costs 6KB per compiler per peer. TerrainOp.Awake also loops
    /// every heightmap in the op radius, so one blast can hit 2-4 compilers.
    ///
    /// This only MEASURES. It changes no behaviour. If no-ops turn out to be a
    /// large share, the fix is to skip Save() when nothing changed.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp), "DoOperation")]
    internal static class TerrainComp_DoOperation_Census_Patch
    {
        private static readonly FieldInfo s_modHeight = AccessTools.Field(typeof(TerrainComp), "m_modifiedHeight");
        private static readonly FieldInfo s_levelDelta = AccessTools.Field(typeof(TerrainComp), "m_levelDelta");
        private static readonly FieldInfo s_smoothDelta = AccessTools.Field(typeof(TerrainComp), "m_smoothDelta");
        private static readonly FieldInfo s_modPaint = AccessTools.Field(typeof(TerrainComp), "m_modifiedPaint");

        internal static int Ops, NoOps, Changed;
        internal static readonly HashSet<string> Sectors = new HashSet<string>();

        /// <summary>
        /// Cheap digest of the delta arrays. Not a hash — a sum plus a count,
        /// which is enough to tell "identical" from "something moved" and costs
        /// one pass over ~1k entries rather than a copy.
        /// </summary>
        private static double Digest(TerrainComp c)
        {
            double d = 0;
            if (s_modHeight.GetValue(c) is bool[] mh)
                for (int i = 0; i < mh.Length; i++) if (mh[i]) d += 1.0;
            if (s_modPaint.GetValue(c) is bool[] mp)
                for (int i = 0; i < mp.Length; i++) if (mp[i]) d += 2.0;
            if (s_levelDelta.GetValue(c) is float[] ld)
                for (int i = 0; i < ld.Length; i++) d += ld[i];
            if (s_smoothDelta.GetValue(c) is float[] sd)
                for (int i = 0; i < sd.Length; i++) d += sd[i] * 3.0;
            return d;
        }

        static void Prefix(TerrainComp __instance, out double __state)
        {
            __state = double.NaN;
            if (__instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            try { __state = Digest(__instance); } catch { }
        }

        static void Postfix(TerrainComp __instance, double __state)
        {
            if (__instance == null || double.IsNaN(__state)) return;
            double after;
            try { after = Digest(__instance); } catch { return; }

            Ops++;
            if (after == __state) NoOps++; else Changed++;
            var s = ZoneSystem.GetZone(__instance.transform.position);
            Sectors.Add($"{s.x},{s.y}");
        }

        /// <summary>Drained onto the [Perf] companion line; null when idle.</summary>
        internal static string Drain()
        {
            if (Ops == 0) return null;
            int bytesWasted = NoOps * 6108;
            string s = $"terrainOps={Ops} changed={Changed} noop={NoOps} " +
                       $"({(Ops > 0 ? 100 * NoOps / Ops : 0)}%) sectors={Sectors.Count} " +
                       $"wastedPerPeer={bytesWasted / 1024}KB";
            Ops = 0; NoOps = 0; Changed = 0; Sectors.Clear();
            return s;
        }
    }

    /// <summary>
    /// v0.7.43: find out WHY routed RPCs are being dropped.
    ///
    /// ZNetView.HandleRoutedRPC logs "Failed to find rpc method &lt;hash&gt;" when a
    /// client sends a routed RPC to a ZDO whose view has no handler for it.
    /// Measured 2026-10-06: ~1.8/sec sustained, every one of them hash
    /// 1077891207 = RPC_ApplyOperation (brute-forced against every string in
    /// the decompile; single match). That is terrain modification.
    ///
    /// TerrainComp.Awake returns BEFORE m_nview.Register("RPC_ApplyOperation")
    /// when Heightmap.FindHeightmap comes back null, so a comp that lost that
    /// race is alive but deaf. The open question is whether the drops matter:
    /// these ZDOs are deliberately peer-owned (see the ReleaseZDOS patch), and
    /// ZNetView.InvokeRPC routes to m_zdo.GetOwner(), so a dig should land on
    /// the owning CLIENT, not here. If so these are noise.
    ///
    /// The discriminator is the handler count on the receiving view:
    ///   0 handlers  -> Awake bailed entirely, the comp is deaf (our problem)
    ///   &gt;0 handlers -> registered fine, some other method is missing
    /// plus whether the target ZDO is owned by us or by a peer.
    ///
    /// Aggregated, not per-event: at 1.8/sec a line each would be 108/minute.
    /// </summary>
    [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.HandleRoutedRPC))]
    internal static class ZNetView_HandleRoutedRPC_Diag_Patch
    {
        private static readonly FieldInfo s_functions =
            AccessTools.Field(typeof(ZNetView), "m_functions");

        private static int s_drops, s_noHandlers, s_hasHandlers, s_serverOwned, s_peerOwned, s_unowned;
        private static readonly Dictionary<string, int> s_byPrefab = new Dictionary<string, int>();
        private static readonly Dictionary<int, int> s_byHash = new Dictionary<int, int>();
        // v0.7.87: WHERE the drops are. Aggregated by zone with a peer-distance,
        // because at 30/s a line per drop would be its own flood — and the
        // question is which zones, not which packets.
        private static readonly Dictionary<Vector2s, int> s_byZone =
            new Dictionary<Vector2s, int>();
        private static readonly Dictionary<Vector2s, float> s_zoneDist =
            new Dictionary<Vector2s, float>();
        // v0.7.90: one sample per distinct prefab+hash, so a new drop still
        // shows up immediately while a known one stays quiet forever.
        private static readonly HashSet<long> s_sampled = new HashSet<long>();

        static bool Prefix(ZNetView __instance, ZRoutedRpc.RoutedRPCData __0)
        {
            if (__instance == null || __0 == null) return true;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            // Only interested in the ones vanilla is about to drop.
            var fns = s_functions?.GetValue(__instance) as System.Collections.IDictionary;
            if (fns == null || fns.Contains(__0.m_methodHash)) return true;

            s_drops++;
            if (fns.Count == 0) s_noHandlers++; else s_hasHandlers++;

            if (s_byHash.TryGetValue(__0.m_methodHash, out int hc)) s_byHash[__0.m_methodHash] = hc + 1;
            else s_byHash[__0.m_methodHash] = 1;

            var zdo = __instance.GetZDO();
            string name = "?";
            if (zdo != null)
            {
                long owner = zdo.GetOwner();
                if (owner == 0L) s_unowned++;
                else if (ZNet.GetUID() == owner) s_serverOwned++;
                else s_peerOwned++;

                var scene = ZNetScene.instance;
                if (scene != null)
                {
                    var prefab = scene.GetPrefab(zdo.GetPrefab());
                    if (prefab != null) name = prefab.name;
                }
            }
            if (s_byPrefab.TryGetValue(name, out int pc)) s_byPrefab[name] = pc + 1;
            else if (s_byPrefab.Count < 24) s_byPrefab[name] = 1;

            if (zdo != null)
            {
                var z = zdo.GetSector();
                s_byZone.TryGetValue(z, out int zc);
                if (zc > 0 || s_byZone.Count < 24) s_byZone[z] = zc + 1;
                if (!s_zoneDist.ContainsKey(z) && ZNet.instance != null)
                {
                    var zp = zdo.GetPosition();
                    float nearest = -1f;
                    foreach (var peer in ZNet.instance.GetPeers())
                    {
                        if (peer == null) continue;
                        var d = peer.m_refPos - zp; d.y = 0f;
                        float flat = d.magnitude;
                        if (nearest < 0f || flat < nearest) nearest = flat;
                    }
                    s_zoneDist[z] = nearest;
                }
            }

            // v0.7.90: SUPPRESS vanilla's per-drop warning.
            // HandleRoutedRPC's else branch does nothing but
            // ZLog.LogWarning("Failed to find rpc method " + hash), so
            // returning false loses no behaviour at all - only the line. At
            // 30/s it made the log unreadable and it carries less information
            // than our own rpcDrops summary already does.
            //
            // A NEW kind of drop must never be hidden, so the first sighting
            // of each distinct prefab+hash pair still logs once, with detail.
            // After that the 20s rpcDrops line carries the volume.
            long sampleKey = ((long)__0.m_methodHash << 24) ^ (long)name.GetHashCode();
            if (s_sampled.Add(sampleKey))
            {
                Plugin.Log.LogWarning(
                    $"[{Plugin.Stamp}] [Diag] rpcDrop FIRST SEEN prefab={name} " +
                    $"hash={__0.m_methodHash} owner={(zdo != null ? zdo.GetOwner() : 0L)} " +
                    $"handlers={fns.Count} - further copies of this pair are suppressed; " +
                    $"see the rpcDrops summary for volume");
            }
            return false;
        }

        internal static string Drain()
        {
            if (s_drops == 0) return null;
            string top = string.Join(" ", s_byPrefab.OrderByDescending(kv => kv.Value)
                .Take(5).Select(kv => $"{kv.Key}×{kv.Value}"));
            string hashes = string.Join(" ", s_byHash.OrderByDescending(kv => kv.Value)
                .Take(3).Select(kv => $"{kv.Key}×{kv.Value}"));
            string zones = string.Join(" ", s_byZone.OrderByDescending(kv => kv.Value)
                .Take(4).Select(kv => $"{kv.Key.x},{kv.Key.y}" +
                    (s_zoneDist.TryGetValue(kv.Key, out float dd) && dd >= 0f
                        ? $"@{dd:F0}m" : "") + $"×{kv.Value}"));
            string s = $"rpcDrops={s_drops} (noHandlers={s_noHandlers} hasHandlers={s_hasHandlers}) " +
                       $"owner[server={s_serverOwned} peer={s_peerOwned} none={s_unowned}] " +
                       $"| {top} | hash {hashes} | zones {zones}";
            s_drops = 0; s_noHandlers = 0; s_hasHandlers = 0;
            s_serverOwned = 0; s_peerOwned = 0; s_unowned = 0;
            s_byPrefab.Clear(); s_byHash.Clear();
            s_byZone.Clear(); s_zoneDist.Clear();
            return s;
        }
    }

    internal static class PassTimer
    {
        internal const int ZoneLoad = 0;
        internal const int CreateDestroy = 1;
        internal const int ReleaseZDOS = 2;
        internal const int NavSweep = 3;
        internal const int PeerSend = 4;
        private static readonly string[] s_names =
            { "zoneLoad", "createDestroy", "releaseZDOS", "navSweep", "peerSend" };

        private static readonly double[] s_totalMs = new double[5];
        private static readonly double[] s_maxMs = new double[5];
        private static readonly int[] s_calls = new int[5];

        private static readonly double s_msPerTick = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // v0.7.49: microseconds, for the per-invocation createSorted diagnostic.
        internal static long ElapsedUs(long startTicks) =>
            (long)((Now - startTicks) * s_msPerTick * 1000.0);

        internal static long Now => System.Diagnostics.Stopwatch.GetTimestamp();

        internal static void Record(int pass, long startTicks)
        {
            double ms = (Now - startTicks) * s_msPerTick;
            s_totalMs[pass] += ms;
            s_calls[pass]++;
            if (ms > s_maxMs[pass]) s_maxMs[pass] = ms;
        }

        /// <summary>One line, then reset. windowMs lets us show the share of
        /// wall-clock time our passes actually consumed.</summary>
        internal static string DrainSummary(double windowMs)
        {
            var sb = new System.Text.StringBuilder();
            double grand = 0;
            for (int i = 0; i < s_names.Length; i++)
            {
                if (s_calls[i] == 0) continue;
                sb.Append($"{s_names[i]}={s_totalMs[i]:F0}ms/{s_calls[i]} (max {s_maxMs[i]:F0}ms) ");
                grand += s_totalMs[i];
                s_totalMs[i] = 0; s_maxMs[i] = 0; s_calls[i] = 0;
            }
            if (sb.Length == 0) return null;
            sb.Append($"| ours {grand:F0}ms of {windowMs:F0}ms ({grand * 100.0 / windowMs:F0}%)");
            return sb.ToString();
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    internal static class ZoneSystem_Update_Timing_Patch
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) { __state = PassTimer.Now; }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) { PassTimer.Record(PassTimer.ZoneLoad, __state); }
    }


    /// <summary>
    /// v0.7.54: split our CreateDestroyObjects pass into its three phases.
    ///
    /// Needed before the round-robin refactor can be designed. All we have today
    /// is the total — `createDestroy=443ms/win (max 244ms)` — which cannot say
    /// whether the cost is the per-peer sector walks, vanilla's creation, or
    /// vanilla's removal. The three phases want different cadences and the right
    /// design depends on which dominates:
    ///
    ///   walk   — FindSectorObjects once per unique peer position, unioned.
    ///            Scales with peer count and dispersion. Sliceable per peer.
    ///   create — vanilla CreateObjects. Sorts the UNCREATED subset each call and
    ///            instantiates up to the budget. Drives post-teleport latency.
    ///            Sliceable, and the phase we may want running every tick.
    ///   remove — vanilla RemoveObjects. Iterates EVERY live instance and
    ///            destroys anything absent from the lists handed in, so it must
    ///            always see the full union. NOT sliceable. Likely the reason to
    ///            keep a stride.
    ///
    /// If remove dominates, decoupling the cadences (create every tick, remove
    /// strided) gets the latency win without the CPU cost, and the full
    /// round-robin refactor is not needed. If walk dominates, slicing is the
    /// answer and the refactor earns its risk.
    /// </summary>
    internal static class CreateDestroyPhases
    {
        private static readonly double s_msPerTick =
            1000.0 / System.Diagnostics.Stopwatch.Frequency;

        private static int s_passes;
        private static double s_walkMs, s_createMs, s_removeMs;
        private static double s_walkMax, s_createMax, s_removeMax;
        private static long s_nearSum, s_distantSum, s_peerSum;
        private static int s_nearMax, s_instancesMax;

        // m_instances is what RemoveObjects iterates in full on every call, so
        // its size is the scale factor for that phase.
        private static readonly FieldInfo s_instancesField =
            AccessTools.Field(typeof(ZNetScene), "m_instances");

        internal static int LiveInstances(ZNetScene scene)
        {
            var col = s_instancesField?.GetValue(scene) as System.Collections.ICollection;
            return col != null ? col.Count : -1;
        }

        internal static long Now => System.Diagnostics.Stopwatch.GetTimestamp();
        private static double Ms(long from) => (Now - from) * s_msPerTick;

        internal static void Record(long walkStart, long createStart, long removeStart,
                                    long doneAt, int peers, int near, int distant,
                                    int liveInstances)
        {
            double w = (createStart - walkStart) * s_msPerTick;
            double c = (removeStart - createStart) * s_msPerTick;
            double r = (doneAt - removeStart) * s_msPerTick;

            s_passes++;
            s_walkMs += w; s_createMs += c; s_removeMs += r;
            if (w > s_walkMax) s_walkMax = w;
            if (c > s_createMax) s_createMax = c;
            if (r > s_removeMax) s_removeMax = r;
            s_nearSum += near; s_distantSum += distant; s_peerSum += peers;
            if (near > s_nearMax) s_nearMax = near;
            if (liveInstances > s_instancesMax) s_instancesMax = liveInstances;
        }

        internal static string Drain()
        {
            if (s_passes == 0) return null;
            string s = $"cdPhases passes={s_passes} " +
                       $"walk={s_walkMs:F0}ms(max {s_walkMax:F1}) " +
                       $"create={s_createMs:F0}ms(max {s_createMax:F1}) " +
                       $"remove={s_removeMs:F0}ms(max {s_removeMax:F1}) | " +
                       $"peers~{s_peerSum / s_passes} near~{s_nearSum / s_passes}/{s_nearMax} " +
                       $"distant~{s_distantSum / s_passes} instancesMax={s_instancesMax}";
            s_passes = 0;
            s_walkMs = 0; s_createMs = 0; s_removeMs = 0;
            s_walkMax = 0; s_createMax = 0; s_removeMax = 0;
            s_nearSum = 0; s_distantSum = 0; s_peerSum = 0;
            s_nearMax = 0; s_instancesMax = 0;
            return s;
        }
    }



    /// <summary>
    /// v0.7.51 diagnostic (bug #6): name the zone and the object that is
    /// blocking player creation.
    ///
    /// Heimdall was stuck in a teleporter for 54 minutes on 2026-10-20, alone
    /// on the server, and two full relogs did not clear it — only a restart
    /// did. The dead-window diagnostic showed 10 dropped-RPC spans against
    /// their player ZDO with `uncreated=1`, so it is NOT the creation backlog.
    ///
    /// The mechanism is ZoneSystem.IsZoneReadyForType:
    ///     foreach (ZDO item in m_loadingObjectsInZones[zoneID])
    ///         if ((int)objectType &lt; (int)item.Type) return false;
    /// A Player is Prioritized (1), so anything Solid (2) or Terrain (3) still
    /// registered as "loading" in that zone blocks the player from ever being
    /// instantiated — and CreateObjectsSorted just `continue`s past it every
    /// frame, forever, without consuming budget. No GameObject means every RPC
    /// to that player dies silently at the FindInstance null check.
    ///
    /// Only LocationProxy and DungeonGenerator ever register as loading, and
    /// LocationProxy.SpawnLocation has a stuck path:
    ///     m_instance = ZoneSystem.instance.SpawnProxyLocation(...);
    ///     if (m_instance == null) return false;    // returns WITHOUT unsetting
    /// so a proxy that can never spawn keeps its zone blocked for its lifetime.
    /// That it survived a relog fits: the registry is keyed by ZONE, not player.
    ///
    /// Reports the longest-blocked zone and what is blocking it, because
    /// duration is what separates a normal load from the bug.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.IsZoneReadyForType))]
    internal static class ZoneSystem_IsZoneReadyForType_Diag_Patch
    {
        private static readonly FieldInfo s_loading =
            AccessTools.Field(typeof(ZoneSystem), "m_loadingObjectsInZones");

        private struct Block { public long FirstAt; public int Hits; public string Blockers; }
        private static readonly Dictionary<Vector2s, Block> s_blocked = new Dictionary<Vector2s, Block>();
        private static int s_denials;

        private static long NowMs => (long)(Time.realtimeSinceStartup * 1000f);

        static void Postfix(ZoneSystem __instance, Vector2s zoneID, ZDO.ObjectType objectType, bool __result)
        {
            if (__result) { s_blocked.Remove(zoneID); return; }
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // Only Prioritized matters here: that is the band a Player sits in.
            if (objectType != ZDO.ObjectType.Prioritized) return;

            s_denials++;
            s_blocked.TryGetValue(zoneID, out var b);
            if (b.FirstAt == 0) b.FirstAt = NowMs;
            b.Hits++;

            // Resolve the blockers only on first sight — this runs per ZDO per
            // frame and GetPrefab is not free.
            if (b.Blockers == null)
            {
                b.Blockers = "?";
                var map = s_loading?.GetValue(__instance) as Dictionary<Vector2s, List<ZDO>>;
                if (map != null && map.TryGetValue(zoneID, out var list) && list != null)
                {
                    var names = new List<string>();
                    var scene = ZNetScene.instance;
                    foreach (var z in list)
                    {
                        if (z == null) continue;
                        var pf = scene?.GetPrefab(z.GetPrefab());
                        names.Add($"{(pf != null ? pf.name : "?")}/{z.Type}");
                        if (names.Count >= 4) break;
                    }
                    if (names.Count > 0) b.Blockers = string.Join(",", names);
                }
            }
            s_blocked[zoneID] = b;
        }

        internal static string Drain()
        {
            if (s_denials == 0) return null;
            long now = NowMs; long worst = 0; string what = "?"; string zone = "?";
            foreach (var kv in s_blocked)
            {
                long age = now - kv.Value.FirstAt;
                if (age > worst) { worst = age; what = kv.Value.Blockers; zone = $"{kv.Key.x},{kv.Key.y}"; }
            }
            string s = $"zoneBlock denials={s_denials} zonesBlocked={s_blocked.Count} " +
                       $"longest={worst / 1000}s zone={zone} by={what}";
            s_denials = 0;
            return s;
        }
    }

    /// <summary>
    /// v0.7.51 diagnostic (bug #6): mark teleports server-side.
    ///
    /// Vanilla logs "Teleporting &lt;name&gt;" in TeleportWorld.Teleport, but that
    /// is reached from Interact and runs on the CLIENT that pressed the key —
    /// and Player.TeleportTo bounces straight to the owner if it is not one, so
    /// the whole sequence is client-side. Confirmed absent from our log.
    ///
    /// A position jump is the only reliable server-side signal: nothing moves
    /// hundreds of metres in one tick legitimately. Catches deaths and respawns
    /// too, which matters because respawns were what interrupted the dungeon
    /// window in [[valheim-timeline-table]].
    ///
    /// The point is correlation: pairing "Teleport peer=X" against a
    /// "DeadWindow prefab=Player" that starts moments later is what turns bug
    /// #6 from a strong hypothesis into a confirmed trigger.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "Update")]
    internal static class ZoneSystem_Update_TeleportMark_Patch
    {
        private const float JumpMetres = 100f;
        private static readonly Dictionary<long, Vector3> s_lastPos = new Dictionary<long, Vector3>();

        static void Postfix()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var peers = ZNet.instance.GetPeers();
            if (peers == null) return;
            foreach (var peer in peers)
            {
                if (peer == null) continue;
                var now = peer.m_refPos;
                if (s_lastPos.TryGetValue(peer.m_uid, out var prev))
                {
                    float d = Utils.DistanceXZ(prev, now);
                    if (d >= JumpMetres)
                    {
                        Plugin.Log.LogWarning(
                            $"[{Plugin.Stamp}] [Diag] Teleport peer={peer.m_playerName} " +
                            $"from=world({prev.x:F0},{prev.z:F0}) to=world({now.x:F0},{now.z:F0}) " +
                            $"dist={d:F0}m zone={ZoneSystem.GetZone(now).x},{ZoneSystem.GetZone(now).y}");
                        TeleportAreaProbe.Arm(peer.m_uid, peer.m_playerName, now);
                    }
                }
                s_lastPos[peer.m_uid] = now;
            }
        }
    }

    /// <summary>
    /// v0.7.50 diagnostic: the post-teleport dead window.
    ///
    /// User report 2026-10-20: after teleporting, "something seemed off" and
    /// chests could not be opened for at least a few seconds. That is this,
    /// in vanilla ZRoutedRpc.HandleRoutedRPC:
    ///
    ///     ZDO zDO = ZDOMan.instance.GetZDO(data.m_targetZDO);
    ///     if (zDO != null) {
    ///         ZNetView v = ZNetScene.instance.FindInstance(zDO);
    ///         if (v != null) v.HandleRoutedRPC(data);
    ///         // else: dropped, no log, no response
    ///     }
    ///
    /// Both exits are silent. A chest only opens if WE have instantiated its
    /// GameObject, because Container.RPC_RequestOpen runs on the owner and
    /// needs a live component to receive it. Until the per-frame creation
    /// budget works down through Terrain, then Solid, to that chest, the
    /// request hits nothing and the client waits forever for a reply.
    ///
    /// Note this sits one level ABOVE the v0.7.43 drop diagnostic, which only
    /// fires once a ZNetView exists but lacks the handler. That one could never
    /// see this class of failure.
    ///
    /// The number that matters is the DEAD WINDOW: how long a ZDO keeps
    /// refusing interaction. So first failure per ZDO is timestamped, and when
    /// an RPC for it finally lands the span is reported. Spans over
    /// DeadWindowReportMs are logged individually; everything is counted.
    ///
    /// Affects every interaction RPC, not just containers — damage, pickup,
    /// rename, hit. `hash` tells them apart.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
    internal static class ZRoutedRpc_HandleRoutedRPC_DeadWindow_Patch
    {
        private const long DeadWindowReportMs = 400;

        private static int s_noZdo, s_noInstance, s_ok, s_spans;
        private static long s_spanMsMax, s_spanMsSum;
        private struct Pend { public long At; public string Prefab; }
        private static readonly Dictionary<ZDOID, Pend> s_firstFail = new Dictionary<ZDOID, Pend>();
        private static readonly Dictionary<int, int> s_byHash = new Dictionary<int, int>();
        private static readonly Dictionary<string, int> s_byPrefab = new Dictionary<string, int>();

        // ---- v0.7.58: identify the silently-dropped RPCs -------------------
        //
        // Added 2026-10-25 after a phantom item on the ground. The server log
        // proved the drop happened (noZdo=4 at 19:44:07, seconds after the
        // pickup that emptied the stand) but could not say WHICH rpc or which
        // object, because the counter carried no identity. Closing that gap is
        // the whole point of this block — behaviour is unchanged.

        // RoutedRPCData carries only m_methodHash, and vanilla keeps no reverse
        // map: ZNetView.Register discards the name after hashing it. So an
        // unexplained drop meant brute-forcing the hash offline against the
        // decompile, which is how 1130726949 was identified as RPC_Damage.
        // Harvested once from build 25730807 — every string literal and
        // nameof() handed to Register/Invoke/InvokeRPC, plus every RPC_* method
        // name. Registration style varies by call site (some pass "Damage",
        // some nameof(RPC_Damage)), so both forms are kept. An unknown hash
        // still prints raw, so a name missing here costs nothing but clarity,
        // and a new game build only needs the list re-harvested.
        private static readonly string[] s_rpcNames = {
            
"AddSaddle", "AdminList", "Alert", "Backward", "Ban", "CharacterID", 
            "ChatMessage", "CheckMaterial", "CheckSpawn", "ClientHandshake", "ClosedCaptions", "CollectResources", 
            "Command", "Controls", "DelayedAttachEffects_Step1", "DelayedAttachEffects_Step2", "DelayedAttachEffects_Step3", "DelayedCrossFadeStart", 
            "DelayedPowerActivation", "DelayedSpawnBoss", "DelayedTap", "DelayedTriggerContinous", "DepthOfField", "DestroyZDO", 
            "DirectionalSoundIndicators", "DisableForcefield", "Disconnect", "EatUpdate", "EnableDamage", "Error", 
            "Explode", "FadeIn", "FallUpdate", "FlashShield", "Forward", "Hide", 
            "HideDreamText", "HideMarker", "HideZZZ", "HistoricalPlayerList", "Hit", "HitTerrain", 
            "IdleEffect", "InvertMouse", "Kick", "Kicked", "Leave", "LoadMainSceneIfBackendSelected", 
            "LocationIcons", "MapData", "Message", "MotionBlur", "NetTime", "OnDeath", 
            "OnNearProjectileHit", "OnTargeted", "PeerInfo", "Pick", "Pickup", "PickupUpdate", 
            "PlayerID", "PlayerList", "PrintBanned", "RPC_AddAdrenaline", "RPC_AddAmmo", "RPC_AddFuel", 
            "RPC_AddFuelAmount", "RPC_AddItem", "RPC_AddNoise", "RPC_AddOre", "RPC_AddSaddle", "RPC_AddStatusEffect", 
            "RPC_AdminList", "RPC_Alert", "RPC_AnimateLever", "RPC_AnimateLeverReturn", "RPC_ApplyOperation", "RPC_Attach", 
            "RPC_Attack", "RPC_Backward", "RPC_Ban", "RPC_BossSpawnInitiated", "RPC_CharacterID", "RPC_ChatMessage", 
            "RPC_ClearCachedSupport", "RPC_ClientHandshake", "RPC_Command", "RPC_ConsoleResetRandomEvent", "RPC_ConsoleStartRandomEvent", "RPC_Controls", 
            "RPC_CreateFragments", "RPC_Damage", "RPC_DamageText", "RPC_DestroyAttachment", "RPC_DestroyZDO", "RPC_Disconnect", 
            "RPC_DiscoverClosestLocation", "RPC_DiscoverLocationResponse", "RPC_Discovered", "RPC_Drain", "RPC_DropArrows", "RPC_DropItem", 
            "RPC_DropItemByName", "RPC_EatConfirmation", "RPC_EmptyProcessed", "RPC_Error", "RPC_Extract", "RPC_FlashShield", 
            "RPC_Forward", "RPC_FreezeFrame", "RPC_GlobalKeys", "RPC_Grow", "RPC_Heal", "RPC_HealthChanged", 
            "RPC_Hide", "RPC_HistoricalPlayerList", "RPC_Hit", "RPC_HitNow", "RPC_HitWhileDodging", "RPC_IncinerateRespons", 
            "RPC_Kick", "RPC_Kicked", "RPC_Left", "RPC_LocationIcons", "RPC_MakePiece", "RPC_MapData", 
            "RPC_Message", "RPC_NetTime", "RPC_Nibble", "RPC_OnDeath", "RPC_OnEat", "RPC_OnHit", 
            "RPC_OnLegUse", "RPC_OnNearProjectileHit", "RPC_OnStateChanged", "RPC_OnTargeted", "RPC_OpenResponse", "RPC_PeerInfo", 
            "RPC_PeerRealtimeMultiplaying", "RPC_Pick", "RPC_Pickup", "RPC_Ping", "RPC_PlayMusic", "RPC_PlayerID", 
            "RPC_PlayerList", "RPC_Pong", "RPC_PrintBanned", "RPC_ProjectileHit", "RPC_RegisterKill", "RPC_ReleaseControl", 
            "RPC_RemoteCommand", "RPC_RemotePrint", "RPC_Remove", "RPC_RemoveBossSpawnInventoryItems", "RPC_RemoveDoneItem", "RPC_RemoveGlobalKey", 
            "RPC_RemoveSaddle", "RPC_Repair", "RPC_RequestActiveEventsList", "RPC_RequestControl", "RPC_RequestDenied", "RPC_RequestIncinerate", 
            "RPC_RequestOpen", "RPC_RequestOwn", "RPC_RequestPickup", "RPC_RequestRespons", "RPC_RequestStack", "RPC_RequestStartEvent", 
            "RPC_RequestStateChange", "RPC_RequestStopEvent", "RPC_RequestTakeAll", "RPC_RequestValidSimulationDistance", "RPC_RequestZDO", "RPC_ResetCloth", 
            "RPC_RoutedRPC", "RPC_Rudder", "RPC_Save", "RPC_SavePlayerProfile", "RPC_Say", "RPC_ServerHandshake", 
            "RPC_ServerList", "RPC_ServerSyncedPlayerData", "RPC_SetAggravated", "RPC_SetAreaHealth", "RPC_SetConnected", "RPC_SetConnection", 
            "RPC_SetDreamCinematic", "RPC_SetEvent", "RPC_SetFuel", "RPC_SetFuelAmount", "RPC_SetGlobalKey", "RPC_SetLoadedVisual", 
            "RPC_SetName", "RPC_SetOwner", "RPC_SetPicked", "RPC_SetPose", "RPC_SetSaddle", "RPC_SetSlotVisual", 
            "RPC_SetSnow", "RPC_SetStayTTL", "RPC_SetTag", "RPC_SetTamed", "RPC_SetTarget", "RPC_SetTrigger", 
            "RPC_SetVisualItem", "RPC_Shake", "RPC_Shoot", "RPC_ShowMessage", "RPC_Sleep", "RPC_SpawnBoss", 
            "RPC_SpawnObject", "RPC_StackResponse", "RPC_Stagger", "RPC_StateChanged", "RPC_Step", "RPC_Stop", 
            "RPC_TakeAllResponse", "RPC_Tap", "RPC_TeleportPlayer", "RPC_TeleportTo", "RPC_ToggleEnabled", "RPC_ToggleOn", 
            "RPC_TogglePermitted", "RPC_Trigger", "RPC_TryEat", "RPC_UnSummon", "RPC_Unban", "RPC_UpdateClientEventsList", 
            "RPC_UpdateEffects", "RPC_UpdateMaterial", "RPC_UpdateVisual", "RPC_UseDoor", "RPC_UseEitr", "RPC_UseStamina", 
            "RPC_ValidatedSimulationDistance", "RPC_Wakeup", "RPC_ZDOData", "RegisterServer2", "ReleaseControl", "RemotePrint", 
            "RemoveGlobalKey", "RemoveInitVel", "RemoveSaddle", "RequestControl", "RequestPickup", "RequestRespons", 
            "RequestServerlist2", "RequestZDO", "ResetHighlight", "ResetHitTimer", "RoutedRPC", "Rudder", 
            "Save", "SavePlayerProfile", "Say", "ServerHandshake", "ServerSyncedPlayerData", "SetAggravated", 
            "SetEvent", "SetGlobalKey", "SetName", "SetOwner", "SetPlayed", "SetSaddle", 
            "SetTrigger", "SetVisualItem", "Shoot", "ShowDreamText", "ShowMessage", "SleepStart", 
            "SleepStop", "SpawnObject", "Speak", "Stats", "Step", "Stop", 
            "StopAOE", "StopConnectionEffect", "StopConnectionEffects", "StopInRangeEffect", "StopListeningToLogMsgs", "ToggleEnabled", 
            "TogglePermitted", "ToggleRun", "Trigger", "TrySpawn", "Unban", "UnregisterServer", 
            "UpdateAttach", "UpdateAttachments", "UpdateOrientation", "UseDoor", "UseEitr", "UseStamina", 
            "ZDOData", "_RequestRespawn", "discovered", 
        };

        private static Dictionary<int, string> s_hashNames;

        internal static string RpcName(int hash)
        {
            if (s_hashNames == null)
            {
                s_hashNames = new Dictionary<int, string>(s_rpcNames.Length);
                foreach (var n in s_rpcNames) s_hashNames[n.GetStableHashCode()] = n;
            }
            return s_hashNames.TryGetValue(hash, out string name) ? name : hash.ToString();
        }

        // Keyed by ZDOID, which is a struct with value equality — safe to hold
        // across ticks, unlike a ZDO reference, which ZDOPool recycles.
        private struct GhostRec { public int Count; public long FirstAt; public int Hash; }
        private static readonly Dictionary<ZDOID, GhostRec> s_ghosts = new Dictionary<ZDOID, GhostRec>();
        private static readonly Dictionary<int, int> s_noZdoByHash = new Dictionary<int, int>();
        private const long GhostExpiryMs = 600000;

        /// <summary>
        /// The target ZDO does not exist on the server, so HandleRoutedRPC is
        /// about to drop this with no log, no error and no reply — the sender
        /// waits forever. Benign races do happen (a client acting on something
        /// destroyed a frame earlier), and they produce exactly ONE of these,
        /// so a single occurrence stays quiet. A phantom the player keeps
        /// clicking repeats against the same ZDOID, which is the signature
        /// worth a line: the repeat count and age are what separate the two.
        /// </summary>
        private static void NoteNoZdo(ZRoutedRpc.RoutedRPCData d)
        {
            if (s_noZdoByHash.TryGetValue(d.m_methodHash, out int hc)) s_noZdoByHash[d.m_methodHash] = hc + 1;
            else if (s_noZdoByHash.Count < 32) s_noZdoByHash[d.m_methodHash] = 1;

            long now = NowMs;
            bool known = s_ghosts.TryGetValue(d.m_targetZDO, out var rec);
            if (!known)
            {
                if (s_ghosts.Count >= 4096) return;   // counted above either way
                rec.FirstAt = now;
            }
            rec.Count++;
            rec.Hash = d.m_methodHash;
            s_ghosts[d.m_targetZDO] = rec;

            GhostDestroyRetry.MaybeRetry(d.m_targetZDO, rec.Count,
                                        RpcName(d.m_methodHash), d.m_senderPeerID);

            if (rec.Count == 2 || rec.Count % 5 == 0)
                Plugin.Log.LogWarning(
                    $"[{Plugin.Stamp}] [Diag] GhostRPC rpc={RpcName(d.m_methodHash)} " +
                    $"zdo={d.m_targetZDO} sender={d.m_senderPeerID} n={rec.Count} " +
                    $"age={(now - rec.FirstAt) / 1000}s — server holds no ZDO, dropped silently");
        }

        // A ghost the player gives up on would otherwise sit here forever.
        private static string DrainGhosts()
        {
            if (s_ghosts.Count == 0) return "";
            long now = NowMs;
            int worstN = 0; long worstAge = 0; int worstHash = 0;
            List<ZDOID> stale = null;
            foreach (var kv in s_ghosts)
            {
                if (now - kv.Value.FirstAt > GhostExpiryMs)
                {
                    (stale ?? (stale = new List<ZDOID>())).Add(kv.Key);
                    continue;
                }
                if (kv.Value.Count > worstN)
                {
                    worstN = kv.Value.Count;
                    worstAge = now - kv.Value.FirstAt;
                    worstHash = kv.Value.Hash;
                }
            }
            if (stale != null) foreach (var id in stale) s_ghosts.Remove(id);
            if (worstN < 2) return $" ghosts={s_ghosts.Count}";
            return $" ghosts={s_ghosts.Count} worst={worstN}x{worstAge / 1000}s({RpcName(worstHash)})";
        }

        private static long NowMs => (long)(Time.realtimeSinceStartup * 1000f);

        static void Prefix(ZRoutedRpc.RoutedRPCData __0)
        {
            if (__0 == null) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__0.m_targetZDO.IsNone()) return;   // global RPC, not an object interaction


            var zdo = ZDOMan.instance?.GetZDO(__0.m_targetZDO);
            if (zdo == null)
            {
                s_noZdo++;
                // Before the generic ghost bookkeeping: if the replay for this
                // id is still queued, the first destroy demonstrably missed
                // this peer and the second copy is the one that matters.
                DuplicateDestroySend.NoteGhostWhilePending(
                    __0.m_targetZDO, RpcName(__0.m_methodHash), __0.m_senderPeerID);
                NoteNoZdo(__0);
                return;
            }

            var scene = ZNetScene.instance;
            var view = scene?.FindInstance(zdo);
            if (view != null)
            {
                s_ok++;
                // Did this ZDO just come back to life? Report how long it was dead.
                if (s_firstFail.TryGetValue(__0.m_targetZDO, out var pend))
                {
                    long span = NowMs - pend.At;
                    s_firstFail.Remove(__0.m_targetZDO);
                    s_spans++; s_spanMsSum += span;
                    if (span > s_spanMsMax) s_spanMsMax = span;
                    if (span >= DeadWindowReportMs)
                    {
                        string nm = "?";
                        if (scene != null)
                        {
                            var pf = scene.GetPrefab(zdo.GetPrefab());
                            if (pf != null) nm = pf.name;
                        }
                        Plugin.Log.LogWarning(
                            $"[{Plugin.Stamp}] [Diag] DeadWindow {span}ms prefab={nm} " +
                            $"uncreated={ZNetScene_CreateObjectsSorted_Diag_Patch.s_lastUncreated} " +
                            $"budget={ZNetScene_CreateObjectsSorted_Diag_Patch.s_lastBudget} " +
                            $"handed={ZNetScene_CreateObjectsSorted_Diag_Patch.s_lastHanded} " +
                            $"rpc={RpcName(__0.m_methodHash)} zdo={__0.m_targetZDO}");
                    }
                }
                return;
            }

            // No GameObject: the interaction is being silently thrown away.
            s_noInstance++;

            string pn = "?";
            if (scene != null)
            {
                var pf = scene.GetPrefab(zdo.GetPrefab());
                if (pf != null) pn = pf.name;
            }
            if (s_byPrefab.TryGetValue(pn, out int pc)) s_byPrefab[pn] = pc + 1;
            else if (s_byPrefab.Count < 32) s_byPrefab[pn] = 1;

            if (s_byHash.TryGetValue(__0.m_methodHash, out int hc)) s_byHash[__0.m_methodHash] = hc + 1;
            else if (s_byHash.Count < 32) s_byHash[__0.m_methodHash] = 1;

            // v0.7.51: carries the prefab so Drain can name the OLDEST stuck
            // object. A permanently stuck one never emits a span — recovery is
            // what produces the measurement — so without this the worst case is
            // the one case that stays invisible. Heimdall sat stuck for 54
            // minutes on 2026-10-20 and the spans stopped 16 minutes before the
            // restart, which read as "resolved" and meant the opposite.
            if (!s_firstFail.ContainsKey(__0.m_targetZDO) && s_firstFail.Count < 4096)
                s_firstFail[__0.m_targetZDO] = new Pend { At = NowMs, Prefab = pn };
        }

        // v0.7.51: names the longest-stuck object, which is the only way a
        // never-recovering one becomes visible.
        private static string OldestPending()
        {
            if (s_firstFail.Count == 0) return "";
            long now = NowMs; long worst = 0; string what = "?";
            foreach (var kv in s_firstFail)
            {
                long age = now - kv.Value.At;
                if (age > worst) { worst = age; what = kv.Value.Prefab; }
            }
            return $" oldestStuck={worst / 1000}s({what})";
        }

        internal static string Drain()
        {
            if (s_noInstance == 0 && s_noZdo == 0) return null;
            string top = string.Join(" ", s_byPrefab.OrderByDescending(kv => kv.Value)
                .Take(5).Select(kv => $"{kv.Key}x{kv.Value}"));
            // v0.7.58: names, not raw hashes, and the two failure modes are
            // reported separately — they mean different things. noInstance is
            // "we have the object but never built it"; noZdo is "we have no
            // record of it at all, yet a client is still interacting with it".
            string hashes = string.Join(" ", s_byHash.OrderByDescending(kv => kv.Value)
                .Take(4).Select(kv => $"{RpcName(kv.Key)}x{kv.Value}"));
            string ghostHashes = string.Join(" ", s_noZdoByHash.OrderByDescending(kv => kv.Value)
                .Take(4).Select(kv => $"{RpcName(kv.Key)}x{kv.Value}"));
            string s = $"deadRPC noInstance={s_noInstance} noZdo={s_noZdo} ok={s_ok} " +
                       $"spans={s_spans} spanMs(mean/max)=" +
                       $"{(s_spans > 0 ? s_spanMsSum / s_spans : 0)}/{s_spanMsMax} " +
                       $"pending={s_firstFail.Count}{DrainGhosts()} | {top} | " +
                       $"noInstance rpc {hashes} | noZdo rpc {ghostHashes}";
            s_noZdo = 0; s_noInstance = 0; s_ok = 0;
            s_spans = 0; s_spanMsSum = 0; s_spanMsMax = 0;
            s_byHash.Clear(); s_byPrefab.Clear(); s_noZdoByHash.Clear();
            return s;
        }
    }

    /// <summary>
    /// v0.7.49 diagnostic: attribute the travel-lag hitches.
    ///
    /// Established 2026-10-20 from the 2026-10-19 group night: felt lag tracks
    /// `hitch/win` (1.16 travelling vs 0.18 stationary, 6x) and the only pass
    /// that scales with it is createDestroy (max single invocation 244ms
    /// travelling vs 58ms in a stationary battle). Stationary battles are
    /// smooth however large; travel is not.
    ///
    /// The suspected chain, from ZNetScene.CreateObjectsSorted:
    ///     int num = Mathf.Max(m_tempCurrentObjects2.Count / 100, maxCreatedPerFrame);
    ///     m_tempCurrentObjects2.Sort(ZDOCompare);
    /// 1. the per-frame creation budget SCALES with the backlog and has no
    ///    ceiling — 10k uncreated means 100/frame, 50k means 500/frame;
    /// 2. the whole uncreated list is sorted EVERY frame, O(n log n), even when
    ///    only ten objects get created;
    /// 3. our CreateDestroyObjects patch feeds it the UNION of every peer's
    ///    sector walk, where vanilla only ever has one local player's area — so
    ///    both costs scale with how dispersed the party is.
    ///
    /// This logs the three numbers that would confirm or refute it: the size of
    /// the list we hand over, how much of it is uncreated (which drives both
    /// the sort and the budget), and how many objects actually got made. If
    /// `uncreated` spikes while travelling and tracks the frame cost, the chain
    /// holds and the fix is the deferred round-robin refactor (BPTODO) — feed
    /// one peer's area per frame instead of the union.
    ///
    /// Also splits create from remove, since our pass invokes both and only the
    /// total was timed before.
    /// </summary>
    [HarmonyPatch(typeof(ZNetScene), "CreateObjectsSorted")]
    internal static class ZNetScene_CreateObjectsSorted_Diag_Patch
    {
        private static readonly FieldInfo s_temp2 =
            AccessTools.Field(typeof(ZNetScene), "m_tempCurrentObjects2");

        internal static int s_calls;
        internal static long s_usTotal;
        internal static int s_usMax;
        internal static int s_handedMax, s_uncreatedMax, s_createdMax, s_budgetMax;
        internal static long s_handedSum, s_uncreatedSum, s_createdSum;
        // Snapshot of the worst single invocation, which is the one that hitches.
        internal static int s_worstHanded, s_worstUncreated, s_worstCreated, s_worstBudget;

        // v0.7.50: live state from the MOST RECENT invocation, so the dead-window
        // diagnostic can stamp the backlog that caused it onto its own line
        // instead of relying on a 20s rollup to correlate after the fact.
        internal static int s_lastHanded, s_lastUncreated, s_lastBudget;

        static void Prefix(out long __state) { __state = PassTimer.Now; }

        static void Postfix(ZNetScene __instance, List<ZDO> currentNearObjects,
                            int maxCreatedPerFrame, ref int created, long __state)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            int us = (int)PassTimer.ElapsedUs(__state);
            // The list is cleared at the START of the next call, so reading it
            // in a Postfix still shows what this invocation worked on.
            var temp2 = s_temp2?.GetValue(__instance) as List<ZDO>;
            int uncreated = temp2 != null ? temp2.Count : -1;
            int handed = currentNearObjects != null ? currentNearObjects.Count : -1;
            // DO NOT "FIX" ZNetScene.InLoadingScreen — the null local player is
            // load-bearing in our favour here:
            //     if (!(Player.m_localPlayer == null))
            //         return Player.m_localPlayer.IsTeleporting();
            //     return true;                    // headless server: ALWAYS true
            // A client gets maxCreatedPerFrame=10 normally and 100 only while
            // teleporting; we get 100 permanently. Measured 2026-10-23: 100 in
            // all 4314 windows of a 24h session, including with zero peers.
            // That free 10x is roughly what offsets DebugCreateDestroyStride=8,
            // so making this behave "correctly" like a client would slow object
            // creation tenfold. If it ever changes, re-check the stride too.
            int budget = uncreated > 0
                ? System.Math.Max(uncreated / 100, maxCreatedPerFrame)
                : maxCreatedPerFrame;

            s_calls++;
            s_usTotal += us;
            s_handedSum += handed > 0 ? handed : 0;
            s_uncreatedSum += uncreated > 0 ? uncreated : 0;
            s_createdSum += created;
            if (handed > s_handedMax) s_handedMax = handed;
            if (uncreated > s_uncreatedMax) s_uncreatedMax = uncreated;
            if (created > s_createdMax) s_createdMax = created;
            if (budget > s_budgetMax) s_budgetMax = budget;
            s_lastHanded = handed; s_lastUncreated = uncreated; s_lastBudget = budget;
            if (us > s_usMax)
            {
                s_usMax = us;
                s_worstHanded = handed; s_worstUncreated = uncreated;
                s_worstCreated = created; s_worstBudget = budget;
            }
        }

        internal static string Drain()
        {
            if (s_calls == 0) return null;
            string s = $"createSorted calls={s_calls} " +
                       $"ms={s_usTotal / 1000} max={s_usMax / 1000}.{(s_usMax % 1000) / 100}ms " +
                       $"handed(mean/max)={s_handedSum / s_calls}/{s_handedMax} " +
                       $"uncreated(mean/max)={s_uncreatedSum / s_calls}/{s_uncreatedMax} " +
                       $"created(mean/max)={s_createdSum / s_calls}/{s_createdMax} " +
                       $"budgetMax={s_budgetMax} | worst: handed={s_worstHanded} " +
                       $"uncreated={s_worstUncreated} created={s_worstCreated} budget={s_worstBudget}";
            s_calls = 0; s_usTotal = 0; s_usMax = 0;
            s_handedMax = 0; s_uncreatedMax = 0; s_createdMax = 0; s_budgetMax = 0;
            s_handedSum = 0; s_uncreatedSum = 0; s_createdSum = 0;
            s_worstHanded = 0; s_worstUncreated = 0; s_worstCreated = 0; s_worstBudget = 0;
            return s;
        }
    }

    [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
    internal static class ZNetScene_CreateDestroyObjects_Timing_Patch
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) { __state = PassTimer.Now; }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) { PassTimer.Record(PassTimer.CreateDestroy, __state); }
    }

    [HarmonyPatch(typeof(ZDOMan), "ReleaseZDOS")]
    internal static class ZDOMan_ReleaseZDOS_Timing_Patch
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) { __state = PassTimer.Now; }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) { PassTimer.Record(PassTimer.ReleaseZDOS, __state); }
    }


    /// <summary>
    /// v0.7.2: daily digest. The per-event diagnostics were drowning the log —
    /// the 8-peer session on 2026-09-22 wrote ~4000 lines in under an hour
    /// (1483 terrain ownership changes, 789 terrain loads, 578 heals, 578
    /// Awakes, ~440 status effects, 358 ship ticks) and LogOutput.log reached
    /// 106 MB. Deleting them would lose the trail on bugs that are still open
    /// (stuck chests, unpickupable drops) and on the terrain zone-load race,
    /// so instead every event is counted here and summarised once a day at
    /// Plugin.DigestHour:DigestMinute — just before TimedLogCopy.ahk archives
    /// the log at 05:58 and the box reboots at 06:00.
    ///
    /// All callers run on Unity's main thread (Harmony patches on Update /
    /// FixedUpdate / RPC handlers), so the dictionaries need no locking —
    /// matching the unsynchronised throttle dictionaries the diagnostics
    /// already use. Counters use Interlocked anyway since it costs nothing.
    ///
    /// Sample dictionaries are capped at SampleCap keys so a pathological day
    /// can't grow memory; overflow is lumped into "+N more".
    /// </summary>
    internal static class DailyDigest
    {
        private const int SampleCap = 16;
        private static DateTime s_windowStart = DateTime.Now;

        // Terrain (zone-load race + dig propagation).
        private static int s_heals, s_loads, s_awakes, s_awakesNoHmap;
        private static int s_applyOps, s_applyOpsLost;
        private static int s_ownerToServer, s_ownerToPeer, s_ownerFromUnowned;
        private static readonly Dictionary<string, int> s_healSectors = new Dictionary<string, int>();

        // Wet / Tar status effects on mobs.
        private static int s_wet, s_tar, s_liquidSuspicious;
        private static float s_maxLiquidLevel;
        private static readonly Dictionary<string, int> s_statusPrefabs = new Dictionary<string, int>();

        // Boats.
        private static int s_shipSamples, s_shipServerOwnedWithPlayers;
        private static float s_shipMaxDepthBelowWater;

        // Carts.
        private static int s_cartGrants, s_cartDenies, s_cartIgnored, s_cartAttaches;
        private static int s_cartReclaimedDeadOwner;
        private static readonly Dictionary<string, int> s_cartDetachReasons = new Dictionary<string, int>();

        // Containers / item drops (stuck chest + pickup race, both still open).
        private static readonly Dictionary<string, int> s_containerOpens = new Dictionary<string, int>();
        private static int s_containerRace, s_containerUnowned, s_containerAbsent;
        private static int s_itemDropRace, s_itemDropUnowned, s_itemDropAbsent;

        internal static void RecordTerrainHeal(Vector3 pos)
        {
            System.Threading.Interlocked.Increment(ref s_heals);
            Bump(s_healSectors, ZoneSystem.GetZone(pos).ToString());
        }

        internal static void RecordTerrainLoad()
        {
            System.Threading.Interlocked.Increment(ref s_loads);
        }

        internal static void RecordTerrainAwake(bool hmapFound)
        {
            System.Threading.Interlocked.Increment(ref s_awakes);
            if (!hmapFound) System.Threading.Interlocked.Increment(ref s_awakesNoHmap);
        }

        internal static void RecordApplyOperation(bool willSave)
        {
            System.Threading.Interlocked.Increment(ref s_applyOps);
            // willSave=false is a dig silently thrown away — the bug v0.5.22 fixed.
            if (!willSave) System.Threading.Interlocked.Increment(ref s_applyOpsLost);
        }

        internal static void RecordTerrainOwnerChange(long prevOwner, long newOwner)
        {
            long sessionId = ZDOMan.GetSessionID();
            if (newOwner == sessionId) System.Threading.Interlocked.Increment(ref s_ownerToServer);
            else System.Threading.Interlocked.Increment(ref s_ownerToPeer);
            if (prevOwner == 0) System.Threading.Interlocked.Increment(ref s_ownerFromUnowned);
        }

        internal static void RecordStatusEffect(bool isWet, string prefab, float liquidLevel, bool suspicious)
        {
            if (isWet) System.Threading.Interlocked.Increment(ref s_wet);
            else System.Threading.Interlocked.Increment(ref s_tar);
            if (suspicious) System.Threading.Interlocked.Increment(ref s_liquidSuspicious);
            if (liquidLevel > s_maxLiquidLevel) s_maxLiquidLevel = liquidLevel;
            Bump(s_statusPrefabs, prefab);
        }

        internal static void RecordShipSample(bool serverOwnedWithPlayers, float depthBelowWater)
        {
            System.Threading.Interlocked.Increment(ref s_shipSamples);
            if (serverOwnedWithPlayers)
                System.Threading.Interlocked.Increment(ref s_shipServerOwnedWithPlayers);
            // Hull depth is the old lunge symptom: >2m means it sank again.
            if (depthBelowWater > s_shipMaxDepthBelowWater) s_shipMaxDepthBelowWater = depthBelowWater;
        }

        internal static void RecordCartRequestOwn(string outcome)
        {
            if (outcome == "GRANT") System.Threading.Interlocked.Increment(ref s_cartGrants);
            else if (outcome == "DENY") System.Threading.Interlocked.Increment(ref s_cartDenies);
            else System.Threading.Interlocked.Increment(ref s_cartIgnored);
        }

        internal static void RecordCartAttach()
        {
            System.Threading.Interlocked.Increment(ref s_cartAttaches);
        }

        internal static void RecordCartDetach(string reason)
        {
            Bump(s_cartDetachReasons, reason);
        }

        internal static void RecordCartReclaimedDeadOwner()
        {
            // The expected path: puller disconnected mid-pull, server cleans up.
            // A LIVE owner being reclaimed still warns immediately (regression).
            System.Threading.Interlocked.Increment(ref s_cartReclaimedDeadOwner);
        }

        internal static void RecordContainerOpen(string prefab)
        {
            Bump(s_containerOpens, prefab);
        }

        internal static void RecordReclaims(
            int containerRace, int containerUnowned, int containerAbsent,
            int itemRace, int itemUnowned, int itemAbsent)
        {
            System.Threading.Interlocked.Add(ref s_containerRace, containerRace);
            System.Threading.Interlocked.Add(ref s_containerUnowned, containerUnowned);
            System.Threading.Interlocked.Add(ref s_containerAbsent, containerAbsent);
            System.Threading.Interlocked.Add(ref s_itemDropRace, itemRace);
            System.Threading.Interlocked.Add(ref s_itemDropUnowned, itemUnowned);
            System.Threading.Interlocked.Add(ref s_itemDropAbsent, itemAbsent);
        }

        /// <summary>
        /// Write the block and reset. `trigger` records why it fired (the daily
        /// schedule, or a clean shutdown) so a short window is self-explaining.
        /// </summary>
        internal static void Emit(string trigger)
        {
            var now = DateTime.Now;
            var span = now - s_windowStart;

            var lines = new List<string>();
            if (s_heals + s_loads + s_awakes + s_applyOps + s_ownerToServer + s_ownerToPeer > 0)
            {
                lines.Add(
                    $"terrain: heals={s_heals} loads={s_loads} awake={s_awakes} (noHmap={s_awakesNoHmap}) " +
                    $"applyOps={s_applyOps} (LOST={s_applyOpsLost}) " +
                    $"ownerChanges={s_ownerToServer + s_ownerToPeer} " +
                    $"(toServer={s_ownerToServer} toPeer={s_ownerToPeer} fromUnowned={s_ownerFromUnowned})");
                if (s_healSectors.Count > 0)
                    lines.Add($"terrain heal sectors: {Top(s_healSectors, 6)}");
            }
            if (s_wet + s_tar > 0)
            {
                lines.Add(
                    $"status: wet={s_wet} tar={s_tar} suspicious={s_liquidSuspicious} " +
                    $"maxLevel={s_maxLiquidLevel:F1} | {Top(s_statusPrefabs, 6)}");
            }
            if (s_shipSamples > 0)
            {
                lines.Add(
                    $"boats: samples={s_shipSamples} serverOwnedWithPlayers={s_shipServerOwnedWithPlayers} " +
                    $"maxDepthBelowWater={s_shipMaxDepthBelowWater:F2}m");
            }
            if (s_cartGrants + s_cartDenies + s_cartIgnored + s_cartAttaches + s_cartDetachReasons.Count > 0)
            {
                lines.Add(
                    $"carts: grants={s_cartGrants} denies={s_cartDenies} ignored={s_cartIgnored} " +
                    $"attaches={s_cartAttaches} reclaimedFromDeadOwner={s_cartReclaimedDeadOwner} | " +
                    $"detach: {Top(s_cartDetachReasons, 6)}");
            }
            if (s_containerRace + s_containerUnowned + s_containerAbsent + s_containerOpens.Count > 0)
            {
                lines.Add(
                    $"containers: opens={Top(s_containerOpens, 4)} | " +
                    $"reclaims livePeer(RACE)={s_containerRace} unowned={s_containerUnowned} " +
                    $"absentPeer={s_containerAbsent}");
            }
            if (s_itemDropRace + s_itemDropUnowned + s_itemDropAbsent > 0)
            {
                lines.Add(
                    $"itemDrops: reclaims livePeer(RACE)={s_itemDropRace} " +
                    $"unowned={s_itemDropUnowned} absentPeer={s_itemDropAbsent}");
            }

            Plugin.Log.LogMessage(
                $"===[DIGEST {trigger}]=== {now:yyyy-MM-dd HH:mm} · window {span.TotalHours:F1}h " +
                $"since {s_windowStart:yyyy-MM-dd HH:mm}" + (lines.Count == 0 ? " · nothing recorded" : ""));
            foreach (var line in lines) Plugin.Log.LogMessage($"[Digest] {line}");

            Reset(now);
        }

        private static void Reset(DateTime now)
        {
            s_windowStart = now;
            s_heals = s_loads = s_awakes = s_awakesNoHmap = s_applyOps = s_applyOpsLost = 0;
            s_ownerToServer = s_ownerToPeer = s_ownerFromUnowned = 0;
            s_wet = s_tar = s_liquidSuspicious = 0;
            s_maxLiquidLevel = 0f;
            s_shipSamples = s_shipServerOwnedWithPlayers = 0;
            s_shipMaxDepthBelowWater = 0f;
            s_cartGrants = s_cartDenies = s_cartIgnored = s_cartAttaches = s_cartReclaimedDeadOwner = 0;
            s_containerRace = s_containerUnowned = s_containerAbsent = 0;
            s_itemDropRace = s_itemDropUnowned = s_itemDropAbsent = 0;
            s_healSectors.Clear();
            s_statusPrefabs.Clear();
            s_cartDetachReasons.Clear();
            s_containerOpens.Clear();
        }

        /// <summary>Count a key, ignoring new keys once SampleCap is reached.</summary>
        private static void Bump(Dictionary<string, int> counts, string key)
        {
            if (counts.TryGetValue(key, out int seen)) counts[key] = seen + 1;
            else if (counts.Count < SampleCap) counts[key] = 1;
            else
            {
                counts.TryGetValue("(other)", out int other);
                counts["(other)"] = other + 1;
            }
        }

        /// <summary>"key×count key×count …", busiest first, plus an overflow note.</summary>
        private static string Top(Dictionary<string, int> counts, int take)
        {
            if (counts.Count == 0) return "none";
            var ordered = counts.OrderByDescending(kv => kv.Value).ToList();
            var shown = string.Join(" ", ordered.Take(take).Select(kv => $"{kv.Key}×{kv.Value}"));
            int rest = ordered.Count - take;
            return rest > 0 ? $"{shown} (+{rest} more)" : shown;
        }
    }










    /// <summary>
    /// v0.7.69 FIX (bug #11): run the pheromone mechanic server-side.
    ///
    /// THE BUG. Character.UpdatePheromones runs inside `if (zDO.IsOwner())`,
    /// so on this server it runs for every creature we own. It then walks
    /// Player.GetAllPlayers() and reads GetSEMan().GetStatusEffects() — a list
    /// populated ONLY on the machine owning that player, because
    /// AddStatusEffect applies locally under `if (m_nview.IsOwner())` and
    /// otherwise RPCs to the owner. Our server owns the creature while the
    /// client owns the player, so the list is always empty and the condition
    /// can never be true. MonsterAI's flee predicate has the identical flaw.
    /// Vanilla gets away with it because one machine normally owns both; we
    /// split them, so it never fires.
    ///
    /// There is no replicated status-effect state to read: no ZDO field exists
    /// (ZDOVars has none), the effects in this family declare no attribute bit,
    /// the registration RPC is sent only in the !IsOwner() branch, the player
    /// inventory is not in the player's ZDO, and ConsumeItem is entirely local.
    /// All verified before settling on the signal below.
    ///
    /// THE SIGNAL. These effects' start-effect prefabs carry a ZNetView, so
    /// drinking one creates a ZDO we receive through ordinary sync. Measured
    /// against the log history: a single distinct ZDO, re-sent continuously
    /// across consecutive census windows at a changing distance — one
    /// persistent object that follows the player for the effect's duration.
    ///
    /// So the window is NOT a timer: it is that ZDO's lifetime. It appears when
    /// the effect starts and is destroyed when the effect ends, however it ends
    /// — expiry, death, or logout. Rebuilding the window list from live ZDOs
    /// each pass therefore needs no expiry bookkeeping and cannot leak a
    /// window. m_ttl is kept only as a sanity cap, because a lost destroy
    /// broadcast is a real failure mode here (see bug 9).
    ///
    /// RANGE. Vanilla applies no distance test at all — it iterates every
    /// player. On a client that is implicitly bounded, because only nearby
    /// players are instantiated. On this server every player is instantiated,
    /// so without a range check one potion would alert matching creatures
    /// world-wide. The radius restores vanilla's de facto bound.
    ///
    /// Everything stays server-authoritative: no ownership is handed to any
    /// client, so this cannot reintroduce the churn that caused bugs 2 and 8.
    /// </summary>
    internal static class PheromoneServerSide
    {
        internal struct Spec
        {
            public string TargetName;   // Character.m_name of the creature affected
            public bool Flee;
            public float Ttl;
            public string EffectName;   // diagnostics only
            // v0.7.71: the four authored values vanilla never reads.
            public float SpawnChance;
            public int MinLevel;
            public int MaxInstances;
            public float LevelUpMult;
        }

        internal struct Window
        {
            public Spec Spec;
            public Vector3 Pos;
            public ZDOID Player;
        }

        // marker prefab hash -> what drinking it means. Built from the asset
        // database at startup, so a future potion in this family needs no code.
        private static readonly Dictionary<int, Spec> s_markers = new Dictionary<int, Spec>();
        private static readonly List<Window> s_windows = new List<Window>();
        private static bool s_scanned;

        internal static int s_alerts, s_fleeGrants;


        /// <summary>
        /// Marker prefabs shared with unrelated effects are useless as a signal
        /// — one of them is the generic potion sound, which every health potion
        /// plays. Only prefabs unique to a single pheromone effect are kept.
        /// </summary>
        internal static void MaybeScan()
        {
            if (s_scanned) return;
            var db = ObjectDB.instance;
            if (db == null || db.m_StatusEffects == null || db.m_StatusEffects.Count == 0) return;
            s_scanned = true;

            var pheromoneUses = new Dictionary<int, int>();   // hash -> how many pheromone effects use it
            var otherUses = new HashSet<int>();               // used by a non-pheromone effect
            var candidate = new Dictionary<int, Spec>();

            foreach (var se in db.m_StatusEffects)
            {
                if (se == null) continue;
                var stats = se as SE_Stats;
                var target = (stats != null) ? stats.m_pheromoneTarget : null;
                bool isPheromone = target != null;

                var list = (se.m_startEffects != null) ? se.m_startEffects.m_effectPrefabs : null;
                if (list == null) continue;

                foreach (var ed in list)
                {
                    if (ed == null || ed.m_prefab == null) continue;
                    if (ed.m_prefab.GetComponent<ZNetView>() == null) continue;   // invisible to us
                    int hash = ed.m_prefab.name.GetStableHashCode();

                    if (!isPheromone) { otherUses.Add(hash); continue; }

                    int n;
                    pheromoneUses.TryGetValue(hash, out n);
                    pheromoneUses[hash] = n + 1;

                    var ch = target.GetComponent<Character>();
                    if (ch == null) continue;
                    candidate[hash] = new Spec
                    {
                        TargetName = ch.m_name,
                        Flee = stats.m_pheromoneFlee,
                        Ttl = se.m_ttl,
                        EffectName = se.name,
                        SpawnChance = stats.m_pheromoneSpawnChanceOverride,
                        MinLevel = stats.m_pheromoneSpawnMinLevel,
                        MaxInstances = stats.m_pheromoneMaxInstanceOverride,
                        LevelUpMult = stats.m_pheromoneLevelUpMultiplier
                    };
                }
            }

            foreach (var kv in candidate)
            {
                if (otherUses.Contains(kv.Key)) continue;          // shared with unrelated effects
                if (pheromoneUses[kv.Key] != 1) continue;          // ambiguous between two effects
                s_markers[kv.Key] = kv.Value;
            }
        }

        /// <summary>
        /// Rebuilt from scratch each pass, so a window exists exactly while its
        /// marker ZDO does. Fed from the create/destroy pass, which already
        /// walks every sector ZDO near a player.
        /// </summary>
        internal static void BeginPass()
        {
            s_windows.Clear();
        }

        internal static void Offer(ZDO zdo)
        {
            if (zdo == null || s_markers.Count == 0) return;
            Spec spec;
            if (!s_markers.TryGetValue(zdo.GetPrefab(), out spec)) return;

            // Sanity cap: a marker whose destroy broadcast was lost would
            // otherwise hold a window open forever.
            long created = ZDOExtraData.GetTimeCreated(zdo.m_uid);
            if (created > 0L && ZNet.instance != null)
            {
                double age = ZNet.instance.GetTimeSeconds() - created;
                if (age > spec.Ttl + 30.0) return;
            }

            var pos = zdo.GetPosition();
            s_windows.Add(new Window { Spec = spec, Pos = pos, Player = NearestPlayer(pos) });
        }

        // The marker follows its drinker, so the closest player owns the
        // window. Generous radius: the object may lag the player slightly.
        private static ZDOID NearestPlayer(Vector3 pos)
        {
            var players = Player.GetAllPlayers();
            if (players == null) return ZDOID.None;
            float best = 36f;
            ZDOID who = ZDOID.None;
            foreach (var p in players)
            {
                if (p == null) continue;
                float d = Utils.DistanceSqr(p.transform.position, pos);
                if (d < best) { best = d; who = p.GetZDOID(); }
            }
            return who;
        }

        internal static bool TryMatch(string creatureName, Vector3 creaturePos)
        {
            if (s_windows.Count == 0 || string.IsNullOrEmpty(creatureName)) return false;
            float r2 = Plugin.PheromoneRadius * Plugin.PheromoneRadius;
            foreach (var w in s_windows)
            {
                if (w.Spec.Flee) continue;                 // flee is the AI predicate's job
                if (w.Spec.TargetName != creatureName) continue;
                if (Utils.DistanceSqr(w.Pos, creaturePos) > r2) continue;
                return true;
            }
            return false;
        }

        // v0.7.71: spawn-side lookup. Unlike TryMatch this does not skip flee
        // windows - a repellent authoring spawn values should honour them too -
        // and it hands back the spec so the caller can apply them.
        // v0.7.72: is this creature the target of ANY registered effect,
        // regardless of whether a window is currently open?
        internal static bool IsTargetCreature(string creatureName)
        {
            if (string.IsNullOrEmpty(creatureName)) return false;
            foreach (var kv in s_markers)
                if (kv.Value.TargetName == creatureName) return true;
            return false;
        }

        internal static bool TryMatchSpawn(string creatureName, Vector3 pos, out Spec spec)
        {
            spec = default(Spec);
            if (s_windows.Count == 0 || string.IsNullOrEmpty(creatureName)) return false;
            float r2 = Plugin.PheromoneRadius * Plugin.PheromoneRadius;
            foreach (var w in s_windows)
            {
                if (w.Spec.TargetName != creatureName) continue;
                if (Utils.DistanceSqr(w.Pos, pos) > r2) continue;
                spec = w.Spec;
                return true;
            }
            return false;
        }

        internal static bool HasFleeWindow(ZDOID player, string creatureName)
        {
            if (s_windows.Count == 0 || player.IsNone()) return false;
            foreach (var w in s_windows)
            {
                if (!w.Spec.Flee) continue;
                if (w.Spec.TargetName != creatureName) continue;
                if (w.Player == player) return true;
            }
            return false;
        }

        internal static string Drain()
        {
            if (s_markers.Count == 0) return null;
            // Only speaks up while an effect is actually in play, so this is
            // silent on a normal server and says what happened when it matters.
            if (s_windows.Count == 0 && s_alerts == 0 && s_fleeGrants == 0) return null;
            return $"pheromone markers={s_markers.Count} windows={s_windows.Count} " +
                   $"alerts={System.Threading.Interlocked.Exchange(ref s_alerts, 0)} " +
                   $"fleeGrants={System.Threading.Interlocked.Exchange(ref s_fleeGrants, 0)}";
        }
    }

    /// <summary>
    /// v0.7.69: replaces the vanilla body, which is provably a no-op here — it
    /// reads a status-effect list that is always empty on this machine — and
    /// reproduces it against our window registry instead. Vanilla's own
    /// per-creature timer field is reused so the cadence stays identical, and
    /// m_pheromoneLoveEffect.HasEffects() is still the gate, so only creature
    /// prefabs authored for this do any work.
    /// </summary>
    [HarmonyPatch(typeof(Character), "UpdatePheromones")]
    internal static class Character_UpdatePheromones_Patch
    {
        private static readonly FieldInfo s_timerField =
            AccessTools.Field(typeof(Character), "m_pheromoneTimer");

        static bool Prefix(Character __instance, float dt)
        {
            if (!Plugin.DebugPheromoneServerSide) return true;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
            if (__instance == null || s_timerField == null) return true;
            // Vanilla gates on this too: creatures with no effect configured do
            // no work and cannot be alerted by this mechanic.
            if (!__instance.m_pheromoneLoveEffect.HasEffects()) return false;

            float timer = (float)s_timerField.GetValue(__instance);
            if (timer < 0f) return false;
            timer -= dt;
            if (timer > 0f) { s_timerField.SetValue(__instance, timer); return false; }
            s_timerField.SetValue(__instance, 5f);

            if (!PheromoneServerSide.TryMatch(__instance.m_name, __instance.transform.position))
                return false;

            __instance.m_pheromoneLoveEffect.Create(
                __instance.transform.position, __instance.transform.rotation);
            var ai = __instance.GetBaseAI() as MonsterAI;
            if (ai != null)
            {
                ai.Alert();
                System.Threading.Interlocked.Increment(ref PheromoneServerSide.s_alerts);
            }
            return false;
        }
    }

    /// <summary>
    /// v0.7.69: the flee half. Additive rather than a replacement — vanilla's
    /// answer still wins if it ever manages to find something, so this can only
    /// turn a false into a true.
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "PheromoneFleeCheck")]
    internal static class MonsterAI_PheromoneFleeCheck_Patch
    {
        static void Postfix(MonsterAI __instance, Character target, ref bool __result)
        {
            if (__result || !Plugin.DebugPheromoneServerSide) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__instance == null || !(target is Player)) return;

            var self = __instance.GetComponent<Character>();
            if (self == null) return;
            if (!PheromoneServerSide.HasFleeWindow(target.GetZDOID(), self.m_name)) return;

            __result = true;
            System.Threading.Interlocked.Increment(ref PheromoneServerSide.s_fleeGrants);
        }
    }

    /// <summary>
    /// v0.7.71 (bug #11, second half): implement the spawn and level behaviour
    /// the asset specifies and the game never reads.
    ///
    /// NOT a vanilla restoration — this is deliberately BEYOND vanilla, and the
    /// distinction matters for future maintenance. The effect authors four
    /// values (spawn chance, instance cap, minimum level, level-up multiplier)
    /// and nothing consumes them. Verified at IL level against the running
    /// assembly: 34 occurrences of "pheromone" in 637k lines of IL, and of the
    /// four fields only their declarations plus one constructor store of a
    /// default. No ldfld anywhere. The same search finds 4 and 2 references for
    /// the two fields that DO work, so the method demonstrably finds reads.
    ///
    /// Consequence: if the developers ever implement these, our values will
    /// stack with theirs and the result will be wrong rather than merely
    /// duplicated. That is an accepted risk, taken knowingly — see the audit
    /// note in [[valheim-vanilla-decompile-snapshot]]. The check is cheap: grep
    /// the IL for a ldfld on any of the four.
    ///
    /// SCOPE. Applied only while a window is open AND only to spawn systems
    /// within the pheromone radius of that window, so a potion affects spawning
    /// near its drinker rather than world-wide. Same scoping approach as the
    /// event-spawn work in v0.7.52.
    ///
    /// SAFETY. SpawnData objects are shared, persistent configuration loaded
    /// from the spawn list. Mutating one and failing to restore it would alter
    /// the world permanently, so the override is strictly scoped: applied
    /// immediately before the vanilla call and restored in a finally, the same
    /// pattern as the sort-origin override in v0.7.55. Interval bookkeeping is
    /// keyed by prefab name in the ZDO rather than by SpawnData identity, so
    /// touching these fields cannot disturb spawn timing.
    ///
    /// Each override takes the MAXIMUM of the authored value and what the
    /// spawner already specifies. The fields are named "override", so
    /// replacement is the authored intent, but a spawner whose chance already
    /// exceeds 25 would then have its rate REDUCED by drinking a potion meant
    /// to raise it. Max keeps the intent and removes that edge.
    /// </summary>
    internal static class PheromoneSpawnOverride
    {
        private struct Saved
        {
            public SpawnSystem.SpawnData Data;
            public float Chance;
            public int MaxSpawned;
            public int MinLevel;
        }

        private static readonly List<Saved> s_saved = new List<Saved>();
        private static readonly Dictionary<int, string> s_prefabCreatureName = new Dictionary<int, string>();

        internal static int s_entriesBoosted, s_levelBoosts;

        private static readonly HashSet<string> s_radiusNoted = new HashSet<string>();

        /// <summary>
        /// v0.7.72: PheromoneRadius is set to vanilla's 80m outer spawn ring,
        /// but a spawner may declare its own radii. Reporting what they really
        /// are, once per creature, so the constant can be corrected from data
        /// rather than left on an assumption.
        /// </summary>
        private static void NoteSpawnRadius(string creature, SpawnSystem.SpawnData spawner)
        {
            if (!PheromoneServerSide.IsTargetCreature(creature)) return;
            string key = creature + "|" + spawner.m_name;
            if (s_radiusNoted.Count > 64 || !s_radiusNoted.Add(key)) return;

        }

        // SpawnData.m_prefab -> that creature's m_name, cached. Instance id is
        // stable for a loaded prefab, so this is safe as a key.
        private static string CreatureName(GameObject prefab)
        {
            if (prefab == null) return null;
            int key = prefab.GetInstanceID();
            string name;
            if (s_prefabCreatureName.TryGetValue(key, out name)) return name;
            var ch = prefab.GetComponent<Character>();
            name = (ch != null) ? ch.m_name : null;
            s_prefabCreatureName[key] = name;
            return name;
        }

        internal static void Begin(List<SpawnSystem.SpawnData> spawners, Vector3 spawnSystemPos)
        {
            s_saved.Clear();
            if (!Plugin.DebugPheromoneSpawnBoost) return;
            if (spawners == null || spawners.Count == 0) return;

            foreach (var spawner in spawners)
            {
                if (spawner == null || spawner.m_prefab == null) continue;
                string creature = CreatureName(spawner.m_prefab);
                if (string.IsNullOrEmpty(creature)) continue;

                // v0.7.72: report what this spawner actually declares, so
                // PheromoneRadius can be checked against data rather than
                // assumed from vanilla defaults.
                NoteSpawnRadius(creature, spawner);

                PheromoneServerSide.Spec spec;
                if (!PheromoneServerSide.TryMatchSpawn(creature, spawnSystemPos, out spec)) continue;
                if (spec.SpawnChance <= 0f && spec.MaxInstances <= 0 && spec.MinLevel <= 0) continue;

                s_saved.Add(new Saved
                {
                    Data = spawner,
                    Chance = spawner.m_spawnChance,
                    MaxSpawned = spawner.m_maxSpawned,
                    MinLevel = spawner.m_minLevel
                });

                if (spec.SpawnChance > 0f)
                    spawner.m_spawnChance = Mathf.Max(spawner.m_spawnChance, spec.SpawnChance);
                if (spec.MaxInstances > 0)
                    spawner.m_maxSpawned = Mathf.Max(spawner.m_maxSpawned, spec.MaxInstances);
                if (spec.MinLevel > 0)
                {
                    spawner.m_minLevel = Mathf.Max(spawner.m_minLevel, spec.MinLevel);
                    // m_maxLevel below m_minLevel would make the level loop
                    // never run, silently losing the star the override asks for.
                    if (spawner.m_maxLevel < spawner.m_minLevel)
                        spawner.m_maxLevel = spawner.m_minLevel;
                }
                System.Threading.Interlocked.Increment(ref s_entriesBoosted);
            }
        }

        internal static void End()
        {
            for (int i = 0; i < s_saved.Count; i++)
            {
                var s = s_saved[i];
                if (s.Data == null) continue;
                s.Data.m_spawnChance = s.Chance;
                s.Data.m_maxSpawned = s.MaxSpawned;
                s.Data.m_minLevel = s.MinLevel;
            }
            s_saved.Clear();
        }

        internal static string Drain()
        {
            int e = System.Threading.Interlocked.Exchange(ref s_entriesBoosted, 0);
            int l = System.Threading.Interlocked.Exchange(ref s_levelBoosts, 0);
            if (e == 0 && l == 0) return null;
            return $"pheromoneSpawn entriesBoosted={e} levelBoosts={l}";
        }
    }

    /// <summary>
    /// v0.7.71: the level-up multiplier, which has no SpawnData field to
    /// override — vanilla computes the chance in this method instead. Scaling
    /// the result is the only place it can be applied.
    /// </summary>
    [HarmonyPatch(typeof(SpawnSystem), "GetLevelUpChance",
        new System.Type[] { typeof(Vector3), typeof(SpawnSystem.SpawnData) })]
    internal static class SpawnSystem_GetLevelUpChance_Patch
    {
        static void Postfix(Vector3 position, SpawnSystem.SpawnData creature, ref float __result)
        {
            if (!Plugin.DebugPheromoneSpawnBoost) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (creature == null || creature.m_prefab == null) return;

            var ch = creature.m_prefab.GetComponent<Character>();
            if (ch == null) return;

            PheromoneServerSide.Spec spec;
            if (!PheromoneServerSide.TryMatchSpawn(ch.m_name, position, out spec)) return;
            if (spec.LevelUpMult <= 1f) return;

            __result *= spec.LevelUpMult;
            System.Threading.Interlocked.Increment(ref PheromoneSpawnOverride.s_levelBoosts);
        }
    }


    /// <summary>
    /// v0.7.80 FIX (bug #9): retry the destroy for a phantom object.
    ///
    /// THE BUG, captured 2026-10-26 on a hive harvest. `SendDestroyed` packs
    /// the whole destroy list into ONE package, broadcasts it once with no
    /// acknowledgement and no retry, and clears the list BEFORE the send. So a
    /// single lost packet loses every destroy it carried. Measured that day:
    /// ~60 honey picked up fine, four ghosts appearing together — one dropped
    /// packet carrying four ids, which is why phantoms always arrive in
    /// clusters rather than singly.
    ///
    /// The client keeps rendering an object whose ZDO we no longer have, so
    /// every interaction with it dies at the ZDO-null check in
    /// `ZRoutedRpc.HandleRoutedRPC` with no log and no reply. The item itself
    /// did transfer; there is simply nothing left to pick up.
    ///
    /// THE FIX. A repeated RPC for a ZDO we do not hold is proof the sender
    /// still believes in it, so re-broadcast the destroy. Safety rests on one
    /// precise guard:
    ///
    ///     uid.UserID == ZDOMan.GetSessionID()
    ///
    /// `m_sessionID` is `Utils.GenerateUID()` per ZDOMan, i.e. fresh on every
    /// server start, and ids come from `m_nextUid++` so they are never reused
    /// within a session. Therefore a matching UserID means WE created it during
    /// THIS run; since we no longer hold it, we destroyed it. It cannot be a
    /// client-created ZDO (different UserID), cannot be an earlier session's
    /// (different UserID), and cannot be live (GetZDO would have found it —
    /// there is no transient-null path for a ZDO still in the world data).
    /// Peers lacking the id ignore the message: `HandleDestroyedZDO` returns
    /// immediately when `GetZDO(uid) == null`.
    ///
    /// ⚠️ WHY THIS LOGS LOUDLY. If the server ever destroys something in error,
    /// this would make clients agree with the mistake instead of leaving a
    /// visible phantom as evidence — curing a symptom while hiding its cause.
    /// So every retry is a warning naming the prefab. An `RPC_RequestOwn` line
    /// means a pickup ghost and the item should already be in that player's
    /// inventory; any other rpc means a client was interacting with something
    /// else we destroyed, where nothing transferred. We cannot verify the
    /// inventory ourselves — player inventories are not in the ZDO.
    /// </summary>
    internal static class GhostDestroyRetry
    {
        // Three strikes: a benign race produces exactly one, so this cannot
        // fire on a client acting a frame after a legitimate destroy.
        private const int RetryAfterHits = 3;
        private const float NameMemorySeconds = 120f;

        private static readonly FieldInfo s_destroySendList =
            AccessTools.Field(typeof(ZDOMan), "m_destroySendList");

        private struct Gone { public string Prefab; public float At; }
        private static readonly Dictionary<ZDOID, Gone> s_recentlyDestroyed =
            new Dictionary<ZDOID, Gone>();
        private static readonly HashSet<ZDOID> s_retried = new HashSet<ZDOID>();

        internal static int s_retries;

        /// <summary>
        /// Once a ZDO is destroyed its prefab is unrecoverable, so the name is
        /// captured at destroy time or the warning can only print an id.
        /// </summary>
        internal static void NoteDestroyed(ZDO zdo)
        {
            if (zdo == null) return;
            if (s_recentlyDestroyed.Count > 4096) return;
            string name = "?";
            var scene = ZNetScene.instance;
            if (scene != null)
            {
                var pf = scene.GetPrefab(zdo.GetPrefab());
                if (pf != null) name = pf.name;
            }
            s_recentlyDestroyed[zdo.m_uid] = new Gone { Prefab = name, At = Time.time };
        }

        private static void Sweep()
        {
            if (s_recentlyDestroyed.Count == 0) return;
            List<ZDOID> stale = null;
            foreach (var kv in s_recentlyDestroyed)
                if (Time.time - kv.Value.At > NameMemorySeconds)
                    (stale ?? (stale = new List<ZDOID>())).Add(kv.Key);
            if (stale == null) return;
            foreach (var id in stale) { s_recentlyDestroyed.Remove(id); s_retried.Remove(id); }
        }

        /// <summary>
        /// Called from the ghost path once a ZDOID has been asked for enough
        /// times to rule out a race.
        /// </summary>
        internal static void MaybeRetry(ZDOID uid, int hits, string rpcName, long sender)
        {
            if (!Plugin.DebugGhostDestroyRetry) return;
            if (hits < RetryAfterHits) return;
            if (ZDOMan.instance == null) return;
            if (uid.UserID != ZDOMan.GetSessionID()) return;   // not ours; never touch it
            if (!s_retried.Add(uid)) return;                   // one retry per object

            var list = s_destroySendList?.GetValue(ZDOMan.instance) as List<ZDOID>;
            if (list == null)
            {
                Plugin.Log.LogError($"[{Plugin.Stamp}] [GhostFix] cannot reach the destroy "
                                    + "list; retry skipped");
                return;
            }
            list.Add(uid);
            System.Threading.Interlocked.Increment(ref s_retries);

            Gone gone;
            string what = s_recentlyDestroyed.TryGetValue(uid, out gone) ? gone.Prefab : "?";
            string age = s_recentlyDestroyed.TryGetValue(uid, out gone)
                ? $"{Time.time - gone.At:F0}s ago" : "not seen destroyed";
            Plugin.Log.LogWarning(
                $"[{Plugin.Stamp}] [GhostFix] RE-SENT destroy for {what} zdo={uid} "
                + $"rpc={rpcName} sender={sender} hits={hits} destroyed={age} "
                + "— a phantom WOULD have persisted here"
                + (rpcName == "RPC_RequestOwn"
                    ? "; the item did transfer, so it should already be in that player's inventory"
                    : "; this was not a pickup, so nothing transferred"));

            Sweep();
        }

        internal static string Drain()
        {
            int n = System.Threading.Interlocked.Exchange(ref s_retries, 0);
            if (n == 0) return null;
            return $"ghostFix retries={n} namesHeld={s_recentlyDestroyed.Count}";
        }
    }

    /// <summary>
    /// v0.7.80: capture the prefab name at destroy time for the retry warning.
    /// `ZDOMan.DestroyZDO` is where we still have the ZDO; by the time a client
    /// asks about it, it is gone and unnameable.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "DestroyZDO")]
    internal static class ZDOMan_DestroyZDO_GhostNames_Patch
    {
        static void Prefix(ZDO zdo)
        {
            if (!Plugin.DebugGhostDestroyRetry) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            GhostDestroyRetry.NoteDestroyed(zdo);
        }
    }

    /// <summary>
    /// v0.7.81 FIX (bug #9, prevention): send every destroy twice.
    ///
    /// `ZDOMan.SendDestroyed` packs the whole destroy list into one ZPackage,
    /// broadcasts it once with no acknowledgement and no retry, and clears the
    /// list BEFORE the send. One lost packet therefore loses every destroy it
    /// carried, which is why phantoms arrive in clusters — measured 2026-10-26
    /// as ~60 successful pickups alongside four phantoms appearing together
    /// from a single hive harvest.
    ///
    /// Packet loss on separate sends is independent, so repeating the broadcast
    /// once takes the failure rate from p to roughly p squared. Unlike the
    /// v0.7.80 retry, which reacts after a player has poked a phantom three
    /// times, this prevents the phantom ever appearing.
    ///
    /// SAFETY — a duplicate destroy cannot destroy the wrong thing, for three
    /// independent reasons, all checked in 1.0.20:
    ///   1. `HandleDestroyedZDO` returns immediately when `GetZDO(uid)` is
    ///      null, so a peer that already applied the first copy, or never had
    ///      the object, does nothing. The operation is idempotent.
    ///   2. Ids are allocated `m_nextUid++` with a collision loop in
    ///      `CreateNewZDO`, so they are monotonic and never recycled within a
    ///      session. `HandleDestroyedZDO` also pushes `m_nextUid` past any id
    ///      it destroys, i.e. vanilla guards against reuse itself.
    ///   3. `m_sessionID` is `Utils.GenerateUID()` per ZDOMan, so an id from an
    ///      earlier run cannot collide with this run's.
    /// The only dangerous operation would be destroying an id we never
    /// destroyed; this re-sends exactly the list vanilla already broadcast.
    ///
    /// Cost is one extra small packet per destroy batch, and only when
    /// something was actually destroyed.
    /// </summary>
    internal static class DuplicateDestroySend
    {
        // Long enough that the copies are unlikely to share a loss event,
        // short enough that no client has time to render a phantom.
        private const float ResendDelaySeconds = 1.0f;

        private static readonly List<ZDOID> s_pending = new List<ZDOID>();
        private static float s_dueAt;

        internal static int s_batches, s_ids;

        /// <summary>
        /// v0.7.83 (bug #10): the one thing the server CAN see about whether the
        /// duplicate send earns its keep.
        ///
        /// We cannot observe which copy a client applied. But a routed RPC for
        /// a ZDO we have already destroyed, arriving while the replay is still
        /// pending, means that client acted on an object it still believes in
        /// AFTER the first destroy went out. The first copy did not reach it,
        /// and the replay about to go is the one that removes the phantom.
        ///
        /// A count here that tracks destroyResend batches is the duplicate send
        /// doing real work. Zero over a long session with phantoms still being
        /// reported means the loss is NOT in this window and the second copy is
        /// not the fix.
        /// </summary>
        internal static int s_replayNeeded;

        internal static bool IsPending(ZDOID uid)
        {
            if (!Plugin.DebugDuplicateDestroySend) return false;
            if (s_pending.Count == 0) return false;
            for (int i = 0; i < s_pending.Count; i++) if (s_pending[i] == uid) return true;
            return false;
        }

        internal static void NoteGhostWhilePending(ZDOID uid, string rpcName, long sender)
        {
            if (!IsPending(uid)) return;
            System.Threading.Interlocked.Increment(ref s_replayNeeded);
            Plugin.Log.LogWarning(
                $"[{Plugin.Stamp}] [Diag] resendNeeded zdo={uid} rpc={rpcName ?? "?"} " +
                $"sender={sender} — first destroy did not reach this peer; the " +
                $"queued second copy is what removes the phantom");
        }

        /// <summary>
        /// Captured in a Prefix because `SendDestroyed` clears the list before
        /// sending, so a Postfix would see nothing.
        /// </summary>
        internal static void Capture(List<ZDOID> list)
        {
            if (!Plugin.DebugDuplicateDestroySend) return;
            if (list == null || list.Count == 0) return;
            if (s_pending.Count > 4096) return;
            s_pending.AddRange(list);
            s_dueAt = Time.time + ResendDelaySeconds;
        }

        internal static void Poll()
        {
            if (!Plugin.DebugDuplicateDestroySend) return;
            if (s_pending.Count == 0 || Time.time < s_dueAt) return;
            if (ZRoutedRpc.instance == null) { s_pending.Clear(); return; }

            // Same wire format vanilla uses: a count followed by the ids.
            var pkg = new ZPackage();
            pkg.Write(s_pending.Count);
            foreach (var id in s_pending) pkg.Write(id);
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, "DestroyZDO", pkg);

            System.Threading.Interlocked.Increment(ref s_batches);
            System.Threading.Interlocked.Add(ref s_ids, s_pending.Count);
            s_pending.Clear();
        }

        internal static string Drain()
        {
            int b = System.Threading.Interlocked.Exchange(ref s_batches, 0);
            int n = System.Threading.Interlocked.Exchange(ref s_ids, 0);
            int rn = System.Threading.Interlocked.Exchange(ref s_replayNeeded, 0);
            if (b == 0 && rn == 0) return null;
            return $"destroyResend batches={b} ids={n} replayNeeded={rn}";
        }
    }

    /// <summary>
    /// v0.7.81: capture the destroy list before vanilla clears it.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "SendDestroyed")]
    internal static class ZDOMan_SendDestroyed_Duplicate_Patch
    {
        private static readonly FieldInfo s_destroySendList =
            AccessTools.Field(typeof(ZDOMan), "m_destroySendList");

        static void Prefix(ZDOMan __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__instance == null || s_destroySendList == null) return;
            var list = s_destroySendList.GetValue(__instance) as List<ZDOID>;
            DuplicateDestroySend.Capture(list);
        }
    }
    /// <summary>
    /// v0.7.83 diagnostic (bug #7): run vanilla's own IsAreaReady against a
    /// teleport destination, from the server, and name the ZDO that fails it.
    ///
    /// The swirl is cleared only inside Player.UpdateTeleport, behind
    /// `!ZNetScene.instance.IsAreaReady(m_teleportTargetPos)`, and there is no
    /// timeout past that gate — the 15s "$msg_portal_blocked" abort sits INSIDE
    /// the branch only reached once the area is already ready. So one ZDO that
    /// never gets an instance hangs the player forever with nothing to recover
    /// it. That check runs on the CLIENT, which we cannot observe.
    ///
    /// What we CAN do is ask the same question with the same vanilla method on
    /// our side. The two machines do not have identical ZDO sets, so this is
    /// corroboration rather than proof, but it splits the search cleanly:
    ///
    ///   server says NOT ready + names a ZDO  → we have a concrete suspect,
    ///       and the flags below say whether it could also block a client.
    ///   server says ready while the player is still spiralling → the blocker
    ///       is client-local, which bounds the bug to the half we cannot see
    ///       and is worth knowing for its own sake.
    ///
    /// Flags logged per blocker, and why each one matters:
    ///   persist=False → NOT saved to the world, so a server restart deletes
    ///       it. That is the only shape that explains Heimdall 2026-10-20:
    ///       survived the client's relogs (we keep re-sending it) but died
    ///       with the restart. A persistent object cannot explain that.
    ///   distant=True → IsAreaReady collects candidates with FindObjects,
    ///       which copies the whole sector list WITHOUT filtering the Distant
    ///       flag, while a client instantiates distant objects through the
    ///       separate CreateDistantObjects path. So a Distant ZDO inside the
    ///       3x3 is REQUIRED by the readiness check but created by a different
    ///       route — a structural suspect regardless of this incident.
    ///   type= → IsZoneReadyForType blocks any ZDO ranking BELOW something
    ///       registered as loading in its zone, so a high type here means the
    ///       zone-loading registry is the likely reason it has no instance.
    ///   created=True with no instance → the Created/m_instances desync:
    ///       CreateObjectsSorted skips it (`if (!zdo.Created)`) while
    ///       FindInstance returns nothing, so it is never retried. Permanent.
    ///
    /// Scope: IsAreaReady uses SimulationDistance(1, 0) — the 3x3 zones at 64m
    /// each, a 192m box around the destination.
    /// </summary>
    internal static class TeleportAreaProbe
    {
        private const float ReportEverySeconds = 5f;
        private const float GiveUpSeconds = 180f;
        private const int MaxBlockersLogged = 4;

        private struct Watch
        {
            public Vector3 Target;
            public string Peer;
            public float Armed;
            public float LastReport;
            public int Reports;
        }

        private static readonly Dictionary<long, Watch> s_watching = new Dictionary<long, Watch>();
        private static readonly List<ZDO> s_buffer = new List<ZDO>();
        private static readonly List<long> s_done = new List<long>();
        // v0.7.85: write-backs are deferred. Assigning to an EXISTING key still
        // bumps Dictionary's version counter on Unity's Mono, which invalidates a
        // live enumerator — so touching s_watching inside the foreach threw
        // InvalidOperationException on every teleport. s_done already deferred
        // removals; this does the same for updates.
        private static readonly List<KeyValuePair<long, Watch>> s_updates =
            new List<KeyValuePair<long, Watch>>();

        internal static int s_armed, s_clearedFast, s_blockedReports;

        /// <summary>
        /// Armed from the teleport marker, which already detects the position
        /// jump. Re-arming the same peer restarts the window rather than
        /// stacking, since a second teleport supersedes the first.
        /// </summary>
        internal static void Arm(long uid, string peerName, Vector3 target)
        {
            if (!Plugin.DebugTeleportAreaProbe) return;
            s_watching[uid] = new Watch
            {
                Target = target,
                Peer = peerName ?? "?",
                Armed = Time.time,
                LastReport = 0f,
                Reports = 0
            };
            System.Threading.Interlocked.Increment(ref s_armed);
        }

        internal static void Poll()
        {
            if (!Plugin.DebugTeleportAreaProbe) return;
            if (s_watching.Count == 0) return;
            var scene = ZNetScene.instance;
            if (scene == null || ZDOMan.instance == null || ZoneSystem.instance == null) return;

            s_done.Clear();
            s_updates.Clear();
            foreach (var kv in s_watching)
            {
                var w = kv.Value;
                float age = Time.time - w.Armed;

                if (scene.IsAreaReady(w.Target))
                {
                    // Only interesting if it took long enough to be felt. A
                    // portal always holds the swirl 8s by design, so anything
                    // under that is not a dead window at all.
                    if (w.Reports > 0)
                        Plugin.Log.LogWarning(
                            $"[{Plugin.Stamp}] [Diag] areaReady CLEARED peer={w.Peer} " +
                            $"after={age:F1}s target=world({w.Target.x:F0},{w.Target.z:F0})");
                    else System.Threading.Interlocked.Increment(ref s_clearedFast);
                    s_done.Add(kv.Key);
                    continue;
                }

                if (age > GiveUpSeconds)
                {
                    Plugin.Log.LogError(
                        $"[{Plugin.Stamp}] [Diag] areaReady NEVER CLEARED peer={w.Peer} " +
                        $"after={age:F0}s target=world({w.Target.x:F0},{w.Target.z:F0}) " +
                        $"— giving up watching");
                    s_done.Add(kv.Key);
                    continue;
                }

                if (Time.time - w.LastReport < ReportEverySeconds) continue;
                w.LastReport = Time.time;
                w.Reports++;
                s_updates.Add(new KeyValuePair<long, Watch>(kv.Key, w));
                Report(w, age, scene);
            }
            foreach (var u in s_updates) s_watching[u.Key] = u.Value;
            foreach (var id in s_done) s_watching.Remove(id);
        }

        private static void Report(Watch w, float age, ZNetScene scene)
        {
            System.Threading.Interlocked.Increment(ref s_blockedReports);
            var zone = ZoneSystem.GetZone(w.Target);

            if (!ZoneSystem.instance.IsZoneLoaded(zone))
            {
                Plugin.Log.LogWarning(
                    $"[{Plugin.Stamp}] [Diag] areaBlocked peer={w.Peer} age={age:F0}s " +
                    $"zone={zone.x},{zone.y} reason=zoneNotLoaded");
                return;
            }

            // Same collection vanilla's IsAreaReady uses, so the candidate set
            // matches rather than approximates it.
            s_buffer.Clear();
            ZDOMan.instance.FindSectorObjects(zone, new SimulationDistance(1, 0), s_buffer);

            int blockers = 0;
            var sb = new System.Text.StringBuilder();
            foreach (var zdo in s_buffer)
            {
                int hash = zdo.GetPrefab();
                if (hash == 0) continue;                      // IsPrefabZDOValid: skipped
                var pf = scene.GetPrefab(hash);
                if (pf == null) continue;                      // IsPrefabZDOValid: skipped
                if (scene.FindInstance(zdo) != null) continue;  // has an instance, fine

                blockers++;
                if (blockers > MaxBlockersLogged) continue;
                long owner = zdo.GetOwner();
                sb.Append($" | {pf.name} zdo={zdo.m_uid} type={zdo.Type} ")
                  .Append($"persist={zdo.Persistent} distant={zdo.Distant} ")
                  .Append($"created={zdo.Created} owner={owner} ")
                  .Append($"ownerLive={ServerCoverage.IsLivePeer(owner)} ")
                  .Append($"ownerIsServer={owner == ZDOMan.GetSessionID()} ")
                  .Append($"zone={zdo.GetSector().x},{zdo.GetSector().y}");
            }

            Plugin.Log.LogWarning(
                $"[{Plugin.Stamp}] [Diag] areaBlocked peer={w.Peer} age={age:F0}s " +
                $"zone={zone.x},{zone.y} target=world({w.Target.x:F0},{w.Target.z:F0}) " +
                $"candidates={s_buffer.Count} blockers={blockers}{sb}");
        }

        internal static string Drain()
        {
            int a = System.Threading.Interlocked.Exchange(ref s_armed, 0);
            int f = System.Threading.Interlocked.Exchange(ref s_clearedFast, 0);
            int b = System.Threading.Interlocked.Exchange(ref s_blockedReports, 0);
            if (a == 0 && b == 0) return null;
            return $"areaProbe armed={a} clearedFast={f} blockedReports={b} watching={s_watching.Count}";
        }
    }

    /// <summary>
    /// v0.7.83 diagnostic (bug #7): poll whether the server has a GameObject
    /// for each connected player, instead of waiting for an RPC to reveal it.
    ///
    /// DeadWindow only fires when a routed RPC happens to arrive for a ZDO we
    /// never instantiated, so every span it has ever reported is RPC-SAMPLED
    /// and therefore a LOWER BOUND. The 2026-10-20 incident read as ten bursts
    /// totalling about two minutes across 37 minutes; a direct poll says
    /// whether the server was really bodiless for that player the whole time.
    ///
    /// peer.m_characterID is the player's own ZDOID, so this needs no guessing
    /// about which ZDO belongs to whom.
    /// </summary>
    internal static class PlayerInstanceWatch
    {
        private const float WarnAfterSeconds = 5f;

        private static readonly Dictionary<long, float> s_since = new Dictionary<long, float>();
        private static readonly List<long> s_gone = new List<long>();

        internal static int s_spans;
        internal static float s_worstSpan;

        internal static void Poll()
        {
            if (!Plugin.DebugPlayerInstanceWatch) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var scene = ZNetScene.instance;
            var peers = ZNet.instance.GetPeers();
            if (scene == null || peers == null || ZDOMan.instance == null) return;

            foreach (var peer in peers)
            {
                if (peer == null || peer.m_characterID.IsNone()) continue;
                var zdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                bool bodiless = zdo == null || scene.FindInstance(zdo) == null;

                if (bodiless)
                {
                    if (!s_since.ContainsKey(peer.m_uid)) s_since[peer.m_uid] = Time.time;
                    continue;
                }

                if (!s_since.TryGetValue(peer.m_uid, out float began)) continue;
                float span = Time.time - began;
                s_since.Remove(peer.m_uid);
                System.Threading.Interlocked.Increment(ref s_spans);
                if (span > s_worstSpan) s_worstSpan = span;
                if (span >= WarnAfterSeconds)
                    Plugin.Log.LogWarning(
                        $"[{Plugin.Stamp}] [Diag] noBody peer={peer.m_playerName} " +
                        $"span={span:F1}s zdo={peer.m_characterID} — server had no GameObject " +
                        $"for this player, every RPC to them was dropped");
            }

            // Forget peers that disconnected while bodiless, or the span would
            // be reported against whoever reuses the uid.
            s_gone.Clear();
            foreach (var kv in s_since)
            {
                bool live = false;
                foreach (var peer in peers) if (peer != null && peer.m_uid == kv.Key) { live = true; break; }
                if (!live) s_gone.Add(kv.Key);
            }
            foreach (var id in s_gone) s_since.Remove(id);
        }

        internal static string Drain()
        {
            int n = System.Threading.Interlocked.Exchange(ref s_spans, 0);
            float w = s_worstSpan; s_worstSpan = 0f;
            if (n == 0 && s_since.Count == 0) return null;
            return $"noBody spans={n} worst={w:F1}s openNow={s_since.Count}";
        }
    }

    /// <summary>
    /// v0.7.83 diagnostic (bug #7): watch the zone-loading registry itself.
    ///
    /// IsZoneReadyForType refuses any ZDO ranking below something registered as
    /// loading in its zone, and CreateObjectsSorted then `continue`s past it
    /// every frame without consuming budget — so a registry entry that never
    /// unsets blocks that zone for as long as it lives. We have only ever
    /// measured the EFFECT (zoneBlock denials); this watches the cause.
    ///
    /// Only LocationProxy and DungeonGenerator ever register. The proxy path
    /// registers while `ShouldDelayProxyLocationSpawning` is true, which is
    /// `!PokeCanSpawnLocation(...)`, which is `LocationPrefabLoadData.IsLoaded`.
    /// That flag is set only in OnPrefabLoaded, which returns early and
    /// SILENTLY when the async load result is not success — and LoadAsync is
    /// called only from the constructor, while the entry is reused forever once
    /// it is in m_locationPrefabs. One failed asset load therefore blocks that
    /// zone for the life of the process, with nothing retrying it. An entry
    /// here aging past AgeWarnSeconds is that condition.
    /// </summary>
    internal static class ZoneLoadingWatch
    {
        private const float AgeWarnSeconds = 15f;
        private const float RewarnSeconds = 60f;

        private struct Entry { public string Prefab; public float At; public float LastWarn; public Vector2s Zone; }
        private static readonly Dictionary<ZDOID, Entry> s_loading = new Dictionary<ZDOID, Entry>();

        internal static int s_set, s_unset, s_stale;

        internal static void NoteSet(ZDO zdo)
        {
            if (!Plugin.DebugZoneLoadingWatch || zdo == null) return;
            string name = "?";
            var scene = ZNetScene.instance;
            if (scene != null)
            {
                var pf = scene.GetPrefab(zdo.GetPrefab());
                if (pf != null) name = pf.name;
            }
            s_loading[zdo.m_uid] = new Entry
            {
                Prefab = name, At = Time.time, LastWarn = 0f, Zone = zdo.GetSector()
            };
            System.Threading.Interlocked.Increment(ref s_set);
        }

        internal static void NoteUnset(ZDO zdo)
        {
            if (!Plugin.DebugZoneLoadingWatch || zdo == null) return;
            if (s_loading.TryGetValue(zdo.m_uid, out var e))
            {
                float held = Time.time - e.At;
                if (held >= AgeWarnSeconds)
                    Plugin.Log.LogWarning(
                        $"[{Plugin.Stamp}] [Diag] zoneLoading RELEASED {e.Prefab} " +
                        $"zone={e.Zone.x},{e.Zone.y} held={held:F0}s");
                s_loading.Remove(zdo.m_uid);
            }
            System.Threading.Interlocked.Increment(ref s_unset);
        }

        internal static void Poll()
        {
            if (!Plugin.DebugZoneLoadingWatch) return;
            if (s_loading.Count == 0) return;
            foreach (var key in new List<ZDOID>(s_loading.Keys))
            {
                var e = s_loading[key];
                float age = Time.time - e.At;
                if (age < AgeWarnSeconds) continue;
                if (e.LastWarn > 0f && Time.time - e.LastWarn < RewarnSeconds) continue;
                e.LastWarn = Time.time;
                s_loading[key] = e;
                System.Threading.Interlocked.Increment(ref s_stale);
                Plugin.Log.LogWarning(
                    $"[{Plugin.Stamp}] [Diag] zoneLoading STUCK {e.Prefab} zdo={key} " +
                    $"zone={e.Zone.x},{e.Zone.y} age={age:F0}s — this zone refuses every " +
                    $"lower-ranked object while this entry stands");
            }
        }

        internal static string Drain()
        {
            int s = System.Threading.Interlocked.Exchange(ref s_set, 0);
            int u = System.Threading.Interlocked.Exchange(ref s_unset, 0);
            int st = System.Threading.Interlocked.Exchange(ref s_stale, 0);
            if (s == 0 && u == 0 && s_loading.Count == 0) return null;
            return $"zoneLoading set={s} unset={u} stale={st} openNow={s_loading.Count}";
        }
    }

    /// <summary>
    /// v0.7.83: register/unregister of the zone-loading entry. Postfix on set
    /// and Prefix on unset, so the entry is recorded after vanilla accepted it
    /// and read before vanilla drops it.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SetLoadingInZone))]
    internal static class ZoneSystem_SetLoadingInZone_Watch_Patch
    {
        static void Postfix(ZDO zdo)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZoneLoadingWatch.NoteSet(zdo);
        }
    }

    [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UnsetLoadingInZone))]
    internal static class ZoneSystem_UnsetLoadingInZone_Watch_Patch
    {
        static void Prefix(ZDO zdo)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZoneLoadingWatch.NoteUnset(zdo);
        }
    }


    /// <summary>
    /// v0.7.84 diagnostic: how long a dungeon interior rebuild blocks the tick.
    ///
    /// On 2026-10-29 (one player, 73 minutes) 61 interiors were rebuilt and 84
    /// of the session's 132 hitches landed in a 20s window where one did — 84%
    /// of those windows hitched against a 19% baseline, 7x. Our own
    /// createDestroy phase measured 6-35ms in those same windows, so the
    /// 460-574ms ticks are not ours. DungeonGenerator.Spawn is the suspect and
    /// has never been measured.
    ///
    /// Spawn() is fully synchronous. Per room it does a blocking
    /// m_prefab.Load(), three GetEnabledComponentsInChildren walks, a
    /// RandomSpawn/RandomObject randomise pass, SetActive(false) over every
    /// networked child, a full room Instantiate, then SetActive(true) again —
    /// and SnapToGround.SnappAll() over the lot at the end. 7 to 55 rooms of
    /// that in one frame, with no owner or ready gate on the Awake that starts
    /// it.
    ///
    /// That a rebuild happens on approach is vanilla; that the SERVER pays for
    /// it is ours. Vanilla only calls ZNet.SetReferencePosition from
    /// local-player code (Player, Tracker, Valkyrie, Game's spawn paths), so a
    /// stock dedicated server leaves m_referencePosition at Vector3.zero
    /// forever and never instantiates anything near a player. Our per-peer
    /// coverage does, for every peer at once.
    ///
    /// Three questions this has to answer before we touch anything:
    ///   ms          — is a rebuild really the half-second tick, or is the
    ///                 correlation pointing at something else in that window?
    ///   rebuild=#N  — is the SAME dungeon being cycled? N climbing is what
    ///                 justifies retention; all N=1 means these are first
    ///                 visits and retention buys nothing.
    ///   nearestPeer — is the server rebuilding dungeons nobody is near? The
    ///                 interior sits +5000m above its entrance
    ///                 (Location.cs: zoneCenter.y + 5000f) but GetZone and
    ///                 PointInsideActiveArea both discard Y, so the horizontal
    ///                 distance is the only one that decides whether it loads.
    /// </summary>
    [HarmonyPatch(typeof(DungeonGenerator), "Spawn")]
    internal static class DungeonSpawnTimer
    {
        private const double HitchMs = 100.0;   // matches PerfHitchMs

        private static readonly double s_msPerTick =
            1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Spawn() sets m_loadedRooms to null on its way out, so the room count
        // has to be read in the Prefix.
        private static readonly FieldInfo s_loadedRoomsField =
            AccessTools.Field(typeof(DungeonGenerator), "m_loadedRooms");
        private static readonly FieldInfo s_nviewField =
            AccessTools.Field(typeof(DungeonGenerator), "m_nview");

        // Rebuild count keyed by the dungeon's ZDO. The ZDO outlives the
        // GameObject — only the instance cycles — so its uid is a stable
        // identity across destroy/recreate, which is exactly what we want to
        // count. ZDOID is IEquatable with a real GetHashCode, so it keys fine.
        private static readonly Dictionary<ZDOID, int> s_rebuilds =
            new Dictionary<ZDOID, int>();

        private static int s_spawns, s_rooms, s_repeats;
        private static double s_totalMs, s_worstMs;

        internal struct State
        {
            public long T0;
            public int Rooms;
        }

        static void Prefix(DungeonGenerator __instance, out State __state)
        {
            __state = default(State);
            if (!Plugin.DebugDungeonSpawnTimer) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            // RoomPlacementData is a private nested struct, so it can't be
            // named here — but System.Array.Length is all we need.
            var rooms = s_loadedRoomsField?.GetValue(__instance) as System.Array;
            __state.Rooms = rooms != null ? rooms.Length : -1;
            __state.T0 = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        static void Postfix(DungeonGenerator __instance, State __state)
        {
            if (__state.T0 == 0) return;   // prefix bailed; nothing to report
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - __state.T0) * s_msPerTick;

            var pos = __instance.transform.position;

            int rebuild = 1;
            var nview = s_nviewField?.GetValue(__instance) as ZNetView;
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo != null)
            {
                // Unbounded growth would be a slow leak over a multi-day
                // uptime, and the count only has to survive long enough to
                // show a cycle. Dropping the table costs one session's worth
                // of history, not correctness.
                if (s_rebuilds.Count > 4096) s_rebuilds.Clear();
                s_rebuilds.TryGetValue(zdo.m_uid, out rebuild);
                rebuild++;
                s_rebuilds[zdo.m_uid] = rebuild;
            }

            // Horizontal distance only — see the class comment on Y.
            float nearest = -1f;
            bool inArea = false;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var d = peer.m_refPos - pos;
                d.y = 0f;
                float flat = d.magnitude;
                if (nearest < 0f || flat < nearest) nearest = flat;
                // The same test our TerrainComp peer-assignment uses, so the
                // two diagnostics agree on what "in a peer's area" means.
                if (ZNetScene.InActiveArea(pos, peer.m_refPos)) inArea = true;
            }

            DungeonDespawnTimer.NoteSpawned(__instance);
            s_spawns++;
            s_totalMs += ms;
            if (ms > s_worstMs) s_worstMs = ms;
            if (__state.Rooms > 0) s_rooms += __state.Rooms;
            if (rebuild > 1) s_repeats++;

            // One line per rebuild, ~55 a session. The per-event detail is the
            // point: an aggregate cannot say whether the expensive ones are
            // the repeats, the big ones or the far ones.
            string line =
                $"[{Plugin.Stamp}] [Diag] dungeonSpawn {ms:F0}ms rooms={__state.Rooms} " +
                $"rebuild=#{rebuild} pos=world({pos.x:F0},{pos.y:F0},{pos.z:F0}) " +
                $"type={(zdo != null ? zdo.Type.ToString() : "?")} " +
                $"nearestPeer={(nearest < 0f ? "none" : $"{nearest:F0}m")} " +
                $"inPeerArea={inArea}";
            if (ms >= HitchMs) Plugin.Log.LogWarning(line);
            else Plugin.Log.LogInfo(line);
        }

        internal static string Drain()
        {
            if (s_spawns == 0) return null;
            string s = $"dungeonSpawn spawns={s_spawns} rooms={s_rooms} " +
                       $"repeats={s_repeats} total={s_totalMs:F0}ms " +
                       $"mean={s_totalMs / s_spawns:F0}ms worst={s_worstMs:F0}ms";
            s_spawns = 0; s_rooms = 0; s_repeats = 0;
            s_totalMs = 0; s_worstMs = 0;
            return s;
        }
    }


    /// <summary>
    /// v0.7.86: the other half of DungeonSpawnTimer — log when a dungeon
    /// interior is torn down, so a boundary oscillation is visible as a
    /// spawn/despawn pair rather than having to be inferred from repeated
    /// spawns.
    ///
    /// RemoveObjects calls ZNetView.ResetZDO() BEFORE Object.Destroy, and
    /// Unity defers OnDestroy to end of frame, so m_nview.GetZDO() is already
    /// null here. The alive-map is therefore keyed by Unity instance id, not
    /// by ZDOID.
    /// </summary>
    [HarmonyPatch(typeof(DungeonGenerator), "OnDestroy")]
    internal static class DungeonDespawnTimer
    {
        private static readonly Dictionary<int, float> s_aliveSince = new Dictionary<int, float>();

        internal static void NoteSpawned(DungeonGenerator dg)
        {
            if (dg == null) return;
            if (s_aliveSince.Count > 4096) s_aliveSince.Clear();
            s_aliveSince[dg.GetInstanceID()] = Time.time;
        }

        static void Prefix(DungeonGenerator __instance)
        {
            if (!Plugin.DebugDungeonSpawnTimer) return;
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (__instance == null) return;

            int id = __instance.GetInstanceID();
            float since;
            bool known = s_aliveSince.TryGetValue(id, out since);
            s_aliveSince.Remove(id);
            if (!known) return;   // never finished a Spawn, nothing to pair with

            var pos = __instance.transform.position;
            float nearest = -1f;
            foreach (var peer in ZNet.instance.GetPeers())
            {
                if (peer == null) continue;
                var d = peer.m_refPos - pos;
                d.y = 0f;
                float flat = d.magnitude;
                if (nearest < 0f || flat < nearest) nearest = flat;
            }

            Plugin.Log.LogWarning(
                $"[{Plugin.Stamp}] [Diag] dungeonDespawn alive={Time.time - since:F0}s " +
                $"pos=world({pos.x:F0},{pos.y:F0},{pos.z:F0}) " +
                $"nearestPeer={(nearest < 0f ? "none" : $"{nearest:F0}m")} " +
                $"— left the coverage union; next approach pays a full rebuild");
        }
    }
}
