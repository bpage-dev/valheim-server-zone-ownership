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
        public const string PluginVersion = "0.7.42";

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
        // --- createDestroy stride (v0.7.10) -------------------------------
        // 1 = vanilla behaviour (every tick). >1 skips the object create/destroy
        // pass on all but every Nth tick, rehearsing how round-robin would feel:
        // with N peers, round-robin refreshes each peer every Nth tick. Set to 8
        // with ONE player online to feel an 8-player round-robin. Watch for late
        // object pop-in while travelling fast (boat, cart). Put back to 1 after.
        //
        // BPTODO refactor roundrobin the create/destroy ticks among players
        // rather than artificially all on every x'th frame (eg 8), flattening spike
        internal const int DebugCreateDestroyStride = 8;

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
        internal const float DebugSendCensusSeconds = 5f;

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
        internal const float DebugSendTierSeconds = 5f;

        // Promote projectiles above Default in the send order (v0.7.42). Pure
        // ordering — projectiles still pass the same distance stride as
        // everything else. false = vanilla ordering, one bool.
        internal const bool DebugProjectilePriority = true;

        // How often the [Nav] navmesh gauge prints. Ours, no vanilla default.
        // Raise it if the log gets noisy; the numbers are instantaneous
        // readings plus per-window counters, so the window length only changes
        // the resolution, never the meaning.
        private const float NavStatsIntervalSeconds = 20f;

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
        private float _navStatsTimer;
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
            Application.logMessageReceived += NavMeshPressure.OnUnityLog;
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
            Application.logMessageReceived -= NavMeshPressure.OnUnityLog;
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
            _navStatsTimer += Time.deltaTime;
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

            if (_navStatsTimer >= NavStatsIntervalSeconds)
            {
                _navStatsTimer = 0f;
                NavMeshPressure.LogStats();
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
            if (passes != null || reclaims != null || sync != null || terrain != null || proj != null)
                Log.LogInfo($"[{Stamp}] [Pass] {passes} | {reclaims} | {sync}" +
                            (terrain != null ? $" | {terrain}" : "") +
                            (proj != null ? $" | {proj}" : ""));
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
            if (eventInv > 0)
            {
                Log.LogInfo(
                    $"[{Plugin.Stamp}] [Diag] SpawnSystem invocations in last {PeerAwareSummaryIntervalSeconds:F0}s: " +
                    $"biome={biomeInv} event={eventInv}.");
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

        private static int s_frame;

        static bool Prefix(ZNetScene __instance)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;

            // v0.7.10: stride is a REHEARSAL for round-robin, not round-robin.
            // This pass is our most expensive (12.4% of wall clock, single calls
            // up to 214ms), and the planned fix is to walk one peer's area per
            // frame instead of every peer's. Before building that, we want to
            // know how the resulting latency FEELS — with 8 peers, a given
            // player's surroundings would refresh every 8th frame.
            //
            // Setting the stride to 8 with one player online reproduces exactly
            // that delay (~0.27s at 30 ticks/sec) while keeping the test simple:
            // skip the whole pass on off-frames. It saves the same work for one
            // player that round-robin would save with eight, so it also shows
            // the [Pass] cost drop, but the point is the visible effect: how
            // late do objects and mobs appear as you travel.
            s_frame++;
            if (Plugin.DebugCreateDestroyStride > 1
                && s_frame % Plugin.DebugCreateDestroyStride != 0)
            {
                return false;   // skip this tick entirely, as round-robin would
            }

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
                var pos = zdo.GetPosition();
                Plugin.Log.LogInfo(
                    $"[{Plugin.Stamp}] [Diag] Ship ownership → peer {desired} (was {prev}) " +
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
                    ReclaimStats.CountScanned();
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
    /// v0.6.2 diagnostic: sample a ship's server-side state every ~2s — owner,
    /// players aboard, speed, rudder, water level, whether the buoyancy block
    /// would run, and velocity.
    ///
    /// CAVEAT: this is a Postfix, and a Postfix does not run when the original
    /// method throws. While Ship.UpdateSailSize was crashing (fixed in v0.6.6),
    /// every crashing tick was invisible here — which is exactly why this
    /// diagnostic reported "water found, physics ran" while the hull was
    /// free-falling. Check the log for exceptions from CustomFixedUpdate before
    /// trusting a clean-looking sample.
    ///
    /// While a client owns the ship (v0.6.4+), these samples describe the
    /// server's network-driven copy, not the physics actually running.
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

            // v0.7.2: the boat investigation is closed (v0.6.4 carve-out +
            // v0.6.6 sail fix), so we no longer need a line per tick. Two
            // numbers still guard against regressions: the server owning a
            // crewed boat (it should hand off within ~2s), and hull depth —
            // the lunge showed as 3.9-5.4m below water versus 1.37m healthy.
            if (waterFound)
                DailyDigest.RecordShipSample(isOwner && playerCount > 0, -heightAboveWater);
        }
    }

    /// <summary>
    /// Cart (Vagon) diagnostics — v0.5.23, reworked in v0.6.6.
    ///
    /// Symptom: a cart disconnects easily while being pulled. Two theories:
    ///
    ///  A. (original) The server owns the cart and runs its joint against the
    ///     puller's network-positioned stand-in, so lag trips the detach-distance
    ///     check or the joint's break force.
    ///
    ///  B. (from reading Vagon in build 25390671) The joint actually lives on
    ///     the puller's client: Vagon.Interact asks the owner for the cart, and
    ///     RPC_RequestOwn hands ZDO ownership to the requester, whose client then
    ///     runs AttachTo. Our ReleaseZDOS then reclaims the cart within ~2s —
    ///     carts don't set s_inUse, so nothing tells it the cart is busy. The
    ///     client is no longer the owner, so its own Vagon.Update runs
    ///     `else if (IsAttached()) Detach();` and drops the cart itself.
    ///
    /// Theory B signature, in order:
    ///   [Diag] Cart RPC_RequestOwn … outcome=GRANT → peer P
    ///   [Diag] Cart reclaimed by server from peer P … attachJoint=True   (≤2s later)
    ///   [Diag] Vagon.Detach … serverOwns=True …   (server clearing the stale flag)
    /// Theory A signature: Vagon.AttachTo on the server, then Vagon.Detach with
    /// serverOwns=True and reason=DISTANCE_EXCEEDED or joint_broke.
    ///
    /// Result (2026-09-18, v0.6.6 in-game test): theory B confirmed — GRANT,
    /// then reclaim with attachJoint=True every ~2s, then stale_attach_flag
    /// detaches. Fixed in v0.6.7 by skipping hitched carts with a live owner
    /// in ZDOMan_ReleaseZDOS_Patch.
    ///
    /// Only logs detaches that matter. When a peer owns an attached cart,
    /// vanilla's non-owner branch calls Detach() on the server's copy EVERY
    /// FRAME (it just clears local state; only the owner can clear the ZDO
    /// flag) — logging those would flood the log with non-events.
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
            bool serverOwns = nview != null && nview.IsValid() && nview.IsOwner();
            if (!serverOwns && joint == null && attachedObj == null) return;

            var zdo = nview?.GetZDO();
            long owner = zdo?.GetOwner() ?? 0;
            bool attachFlag = zdo != null && zdo.GetBool(ZDOVars.s_attachJointHash);
            var pos = __instance.transform.position;

            string reason;
            float dist = -1f;
            if (attachedObj == null)
            {
                reason = joint == null && attachFlag
                    ? "stale_attach_flag (a previous owner attached it; the joint was never on the server)"
                    : "no_attached_object";
            }
            else
            {
                dist = Vector3.Distance(
                    attachedObj.transform.position + __instance.m_attachOffset,
                    __instance.m_attachPoint.position);
                reason = dist >= __instance.m_detachDistance
                    ? $"DISTANCE_EXCEEDED ({dist:F2}m >= {__instance.m_detachDistance:F2}m)"
                    : joint == null ? "joint_broke" : "requested_or_owner_changed";
            }

            // v0.7.2: counted by reason. The reason histogram is what told
            // theory B from theory A, and it still would.
            DailyDigest.RecordCartDetach(reason);
        }
    }

    /// <summary>
    /// v0.6.6 diagnostic: the server's answer when a player grabs a cart it
    /// owns. See Vagon_Detach_Diag_Patch for how this fits theory B.
    /// </summary>
    [HarmonyPatch(typeof(Vagon), nameof(Vagon.RPC_RequestOwn))]
    internal static class Vagon_RPC_RequestOwn_Diag_Patch
    {
        private static readonly FieldInfo s_nviewField = AccessTools.Field(typeof(Vagon), "m_nview");

        static void Prefix(Vagon __instance, long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var nview = (ZNetView)s_nviewField.GetValue(__instance);
            if (nview == null || !nview.IsValid()) return;
            bool serverOwns = nview.IsOwner();
            bool inUse = serverOwns && __instance.InUse();
            // GRANT = handed to the grabber (vanilla), DENY = someone else has
            // it, IGNORED = the server wasn't the owner so vanilla stays quiet.
            string outcome = !serverOwns ? "IGNORED" : inUse ? "DENY" : "GRANT";
            DailyDigest.RecordCartRequestOwn(outcome);
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
            // Server-side attaches should be rare now: vanilla puts the joint on
            // the puller's client. A high count next to the detach reasons would
            // mean theory A is back.
            DailyDigest.RecordCartAttach();
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
                $"[{Plugin.Stamp}] [Diag] ItemStand RPC_DropItem received: sender={sender} " +
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
            bool isWet = nameHash == s_wetHash;
            string prefab = character.gameObject.name.Replace("(Clone)", "");
            float waterLevel = (float)s_waterLevelField.GetValue(character);
            float tarLevel = (float)s_tarLevelField.GetValue(character);
            float liquidLevel = isWet ? waterLevel : tarLevel;
            float depthBelow = liquidLevel - pos.y;
            // The original question was "why is this mob wet when it shouldn't
            // be?" — only the mob being ABOVE the liquid answers that, so the
            // digest counts those separately from ordinary in-water wetness.
            bool suspicious = depthBelow < -0.5f;
            DailyDigest.RecordStatusEffect(isWet, prefab, liquidLevel, suspicious);
        }
    }

    /// <summary>
    /// v0.7.0 — navmesh tile pressure gauge (group-play lag).
    ///
    /// Mob pathfinding works off 32m navmesh tiles, built on demand by any
    /// moving creature (a path request pokes a 3x3 tile block) and kept per
    /// agent size, so one patch of ground can hold several tiles. Neighbouring
    /// tiles are stitched with NavMesh links at ~1m spacing — ~64 links per
    /// tile — drawn from ONE engine-wide pool of 65535. Exhaust it and
    /// NavMesh.AddLink silently returns an invalid handle: mobs can no longer
    /// cross tile boundaries, so they stall and rubber-band. That is what
    /// group combat felt like on 2026-09-22 (8 peers, 6337 allocation errors
    /// in 2.5 minutes).
    ///
    /// Our design concentrates this: the server owns every mob for every peer
    /// (deliberately — a laggy client must not own simulation), so all of the
    /// pathfinding for all peers lands on one machine, where vanilla would
    /// have spread it across clients.
    ///
    /// Emits one line every 20s:
    ///   [Nav] tiles=N (stale=N) stitches=N/65535 (N%) top: Humanoid×N … |
    ///         built=N freed=N linkFails=N peers=N
    /// Warning level once the pool is 80% used or any allocation failed.
    /// stale = tiles past the timeout still waiting to be swept; a non-trivial
    /// stale count means the sweep can't keep up and the knobs need revisiting.
    /// </summary>
    internal static class NavMeshPressure
    {
        internal const int EngineLinkBudget = 65535;

        internal static readonly FieldInfo TilesField =
            AccessTools.Field(typeof(Pathfinding), "m_tiles");
        internal static readonly MethodInfo TimeoutTilesMethod =
            AccessTools.Method(typeof(Pathfinding), "TimeoutTiles");

        private static readonly Type s_tileType =
            typeof(Pathfinding).GetNestedType("NavMeshTile", BindingFlags.NonPublic);
        private static readonly FieldInfo s_pokeTimeField =
            s_tileType != null ? AccessTools.Field(s_tileType, "m_pokeTime") : null;
        private static readonly FieldInfo s_links1Field =
            s_tileType != null ? AccessTools.Field(s_tileType, "m_links1") : null;
        private static readonly FieldInfo s_links2Field =
            s_tileType != null ? AccessTools.Field(s_tileType, "m_links2") : null;

        internal static int s_linkAllocFailures;
        internal static int s_tilesBuilt;
        internal static int s_tilesFreed;
        private static int s_lastTileCount;

        /// <summary>
        /// The link-pool-exhausted message comes from native engine code, so
        /// there is no method to patch. Observe the Unity log stream, count,
        /// and report per window instead of letting it spam the log.
        /// </summary>
        internal static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error || string.IsNullOrEmpty(condition)) return;
            if (condition.IndexOf("NavMeshLink", StringComparison.Ordinal) >= 0)
                System.Threading.Interlocked.Increment(ref s_linkAllocFailures);
        }

        internal static void LogStats()
        {
            var pathfinding = Pathfinding.instance;
            if (pathfinding == null || TilesField == null || s_pokeTimeField == null) return;
            if (!(TilesField.GetValue(pathfinding) is System.Collections.IDictionary tiles)) return;

            int tileCount = 0, stale = 0, stitches = 0, unbuilt = 0;
            float now = Time.time;
            var perAgentType = new Dictionary<int, int>();
            foreach (System.Collections.DictionaryEntry entry in tiles)
            {
                tileCount++;
                int agentType = ((Vector3Int)entry.Key).z;
                perAgentType.TryGetValue(agentType, out int seen);
                perAgentType[agentType] = seen + 1;

                var tile = entry.Value;
                // Measure staleness against the timeout actually in force, not
                // our knob — the knob may be disabled (<= 0 = leave vanilla's).
                if (now - (float)s_pokeTimeField.GetValue(tile) > pathfinding.m_tileTimeout) stale++;
                int links = CountLinks(s_links1Field, tile) + CountLinks(s_links2Field, tile);
                stitches += links;
                // Zero links = queued but never built. Vanilla builds ONE tile
                // per maintenance pass (measured ~4.5/sec), so when creation
                // outruns that the backlog grows and mobs in those tiles have no
                // navmesh at all. This is the number that says whether the whole
                // tile set is even worth keeping.
                if (links == 0) unbuilt++;
            }

            int built = System.Threading.Interlocked.Exchange(ref s_tilesBuilt, 0);
            int freed = System.Threading.Interlocked.Exchange(ref s_tilesFreed, 0);
            int fails = System.Threading.Interlocked.Exchange(ref s_linkAllocFailures, 0);
            int peers = ZNet.instance != null ? ZNet.instance.GetPeers().Count : 0;
            if (tileCount == 0 && fails == 0 && peers == 0) return;

            var top = string.Join(" ", perAgentType
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => $"{(Pathfinding.AgentType)kv.Key}×{kv.Value}"));

            // Creation rate for the window: everything freed, plus net growth.
            // Compare against `built` to see how oversubscribed the pipeline is.
            int created = Mathf.Max(0, freed + (tileCount - s_lastTileCount));
            s_lastTileCount = tileCount;

            string line =
                $"[{Plugin.Stamp}] [Nav] tiles={tileCount} (stale={stale}, " +
                $"unbuilt={unbuilt} {(tileCount > 0 ? unbuilt * 100 / tileCount : 0)}%) " +
                $"created={created} " +
                $"stitches={stitches}/{EngineLinkBudget} ({stitches * 100f / EngineLinkBudget:F0}%) " +
                $"top: {top} | built={built} freed={freed} linkFails={fails} peers={peers}" +
                // Mark load-test readings so they can never be mistaken for a
                // real-world baseline when we compare sessions later.
                (SyntheticPeers.Count > 0 ? $" +synth={SyntheticPeers.Count} (LOAD TEST)" : "");

            if (fails > 0 || stitches > EngineLinkBudget * 0.8f) Plugin.Log.LogWarning(line);
            else Plugin.Log.LogInfo(line);
        }

        private static int CountLinks(FieldInfo field, object tile)
        {
            if (field == null) return 0;
            return (field.GetValue(tile) as System.Collections.ICollection)?.Count ?? 0;
        }
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
    /// Counts tile builds for the [Nav] gauge. Builds are the expensive half of
    /// the pipeline (one per pass, async), so a high built count next to a high
    /// freed count means tiles are thrashing — i.e. NavTileTimeoutSeconds is
    /// too aggressive and should go back up.
    /// </summary>
    [HarmonyPatch(typeof(Pathfinding), "BuildTile")]
    internal static class Pathfinding_BuildTile_Diag_Patch
    {
        static void Postfix()
        {
            System.Threading.Interlocked.Increment(ref NavMeshPressure.s_tilesBuilt);
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
                       $"scanned={s_scanned} forcedZdoSends~{forcedSends}";
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

    /// <summary>
    /// Which peer the server is mid-send to. ZDOMan.ServerSortSendZDOS and
    /// ZDO.Serialize both run inside one SendZDOs call but neither can see the
    /// peer — ZDOPeer is a private nested type, so it cannot be bound as a
    /// patch parameter. The send-rate replacement already holds the ZNetPeer,
    /// so it parks the identity here for the duration of the call.
    ///
    /// Deliberately separate from SendCensus.Capturing: that flag is gated on
    /// DebugSendCensusSeconds, which is documented to be switched off after a
    /// capture. Riding the throttle on it would silently turn the throttle into
    /// a no-op (every ZDO would look never-sent, so everything would be exempt)
    /// with nothing in the log saying so.
    /// </summary>
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

    [HarmonyPatch(typeof(Pathfinding), "TimeoutTiles")]
    internal static class Pathfinding_TimeoutTiles_Timing_Patch
    {
        [HarmonyPriority(Priority.First)]
        static void Prefix(out long __state) { __state = PassTimer.Now; }
        [HarmonyPriority(Priority.Last)]
        static void Postfix(long __state) { PassTimer.Record(PassTimer.NavSweep, __state); }
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
}
