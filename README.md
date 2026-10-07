# ServerZoneOwnership

A BepInEx plugin for the **Valheim dedicated server** that moves world simulation
off the players' machines and onto the server — then manages the network cost that
decision creates, so more players can share a world without mobs stuttering.

**Server-only. Clients install nothing.**

---

## 1. Why move ownership to the server

In vanilla Valheim there is no authoritative simulation. Every object in the world
is a **ZDO** (a networked bag of state), and each ZDO has one **owner** — the
machine responsible for simulating it and telling everyone else what it did. For
anything near a player, that owner is normally **that player's own game client**.

So when you fight a greydwarf, your PC is deciding where it moves and when it
swings, then reporting that to everyone else. That is fast and smooth for *you* —
your client simulates it locally at full frame rate, with no network round trip.

The problem is the other seven people. A player on a congested connection, a
laptop CPU, or distant Wi-Fi owns the mobs near them, and everyone else
experiences those mobs through that player's uplink. One weak link degrades the
session for the whole group, and nothing in vanilla lets you opt out of it.

This plugin makes **the server own every ZDO in every zone covering any connected
player**. The server simulates all of it — AI, pathfinding, physics — and every
client becomes a pure observer of the world around it. Simulation quality then
depends on the server (usually the best-connected, most consistent machine in the
group) rather than on whoever happened to walk in first.

### What the server takes ownership of

Everything persistent inside the covered zones, which in practice means:

| | |
|---|---|
| **Creatures** | all AI, pathfinding, aggro and attacks |
| **Terrain modifications** | digging, levelling, the heightmap compiler |
| **Structures** | walls, floors, roofs, their stability and damage |
| **Vehicles** | carts and boats, including who is attached or at the helm |
| **Containers and props** | chests, fires, torches, item drops |
| **Spawning** | ambient population, events and raids |

Coverage is the **union of every peer's active area**, deduplicated, so players
standing together collapse into shared work rather than multiplying it.

---

## 2. The problem that creates, and the two throttles that solve it

Taking ownership is the easy half. The hard half is that **vanilla's networking was
designed around the assumption we just broke.**

Because the nearest client normally owns a mob and simulates it locally, vanilla's
server-to-client stream only has to carry things you are *not* simulating
yourself. It is a low-priority trickle, and vanilla tunes it accordingly. Once the
server owns everything, that same trickle has to carry **every mob's motion to
every player**, and it is far too slow for the job.

Two separate limits bite, and each needed its own fix.

### Throttle 1 — the send scheduler divided bandwidth by player count

Vanilla's `ZDOMan.SendZDOToPeers2` services **one peer per frame**, with a 0.05s
pause between full rounds. A round therefore costs `0.05s + peers × frameTime`,
and each peer gets exactly one send per round — so **the rate each player receives
divides by the number of players**. Measured on this server: ~15 sends/sec with one
player, ~3/sec with eight. It is a scheduling limit, not a bandwidth one: eight
players on an idle server get the same 3/sec as eight on a hammered one.

At 3 updates/sec, mobs visibly step and teleport while your own movement stays
smooth — exactly the symptom that prompted this work.

The 0.05s pause tells you vanilla considers **20Hz per peer** acceptable, since
that is roughly what a lone player gets. So the fix applies that same cap **per
peer instead of per round**: `DebugPeerSendHz = 20`. Nobody is now worse off for
having company.

### Throttle 2 — distance-tiered per-object update rate

Fixing the scheduler exposed the next limit. Vanilla refuses to send to a peer
whose socket queue exceeds 8192 bytes, and caps each packet at `10240 − queue`
bytes. Past a certain volume, sends are refused outright and packets truncate
mid-list.

Measured: refusals average **2–7% with one player but ~22% with two**, sustained
across several days. A second player does not compete for the first player's
bandwidth — it enlarges the union of loaded zones, so more mobs tick and more ZDOs
change. The cost scales with the world being simulated, not with the number of
sockets.

And vanilla has **no concept of a per-object update rate.** Distance appears only
in its sort *order*, which is never consulted unless a packet overflows. So a
creature 150m away was refreshed exactly as often as one swinging at your face.

The fix remembers, per player and per object, when that player was last told about
it, and skips anything not yet due for its distance:

| Distance to that player | Max updates/sec |
|---|---|
| ≤ 30 m | uncapped (≈20/s) |
| 30 – 100 m | 10/s |
| > 100 m | 3/s |

Each player is evaluated **independently** — the same mob can be due for someone
fighting it and throttled for someone across the valley.

Two things always bypass the cap, because delaying them would be a correctness bug
rather than a saving: **an object the player has never received** (so nothing ever
fails to appear, and newly approached scenery arrives promptly), and **an object
whose owner changed**.

It is deliberately **not type-aware**. A projectile that matters is near, so
distance already keeps it at full rate; anything genuinely static never enters the
candidate list at all, because vanilla only offers objects whose state actually
changed.

### What the throttling measurably buys

Over a ~2.6 hour session: **33% of all send candidates dropped** — 0% inside 30m,
28% in the mid tier, 46% beyond 100m.

The honest reading is that this **re-aims a fixed budget rather than enlarging
it.** Packet size is unchanged, so the number of updates reaching a player is
still bounded by `packet size × send rate`. What changes is *which* objects win
those slots: distant creatures no longer compete with the one in front of you.
Over the same session the byte budget still truncated more than the throttle
removed, so the packet — not the cap — remains the binding constraint.

---

## 3. Diagnostics

The plugin is heavily instrumented, because most of the above was wrong on the
first guess and only measurement settled it.

| Log line | Reports |
|---|---|
| `[Launch]` | launch args (secrets redacted), simulation distance, tier caps |
| `[Perf]` | tick health, per-player send queue, delivered vs attempted sends, refusals |
| `[Tier]` | per-distance-tier candidates, exemptions by cause, throttled, packed, cut |
| `[Nav]` | navmesh tile and link-pool pressure |
| `[Pass]` | time spent in each of our own passes, as a share of wall clock |
| `[Census]` | optional: every prefab actually sent, with byte cost and distance |
| `[Digest]` | daily rollup of terrain, cart, container and reclaim events |

Tuning knobs sit together at the top of `src/Plugin.cs`, each documented with its
vanilla default. `DebugSendTiers = false` reverts all distance throttling to
vanilla behaviour in one line.

---

## Building

Requires .NET SDK (tested with 8.0.4).

1. Copy these DLLs from a Valheim Dedicated Server install
   (`valheim_server_Data\Managed\`) into a folder next to the project:
   - `assembly_valheim.dll`
   - `assembly_utils.dll`
   - `UnityEngine.dll`
   - `UnityEngine.CoreModule.dll`
   - `UnityEngine.PhysicsModule.dll`

2. Download the BepInEx 5.4.21 x64 release from
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.21)
   and extract `BepInEx/core/BepInEx.dll` + `BepInEx/core/0Harmony.dll` into a
   `refs/BepInEx/core/` folder next to the project.

3. Adjust the `<HintPath>` entries in `ServerZoneOwnership.csproj` if your layout
   differs.

4. `dotnet build -c Release`

Output: `bin/Release/ServerZoneOwnership.dll`.

## Installing

1. Install [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   into your dedicated server folder (`valheim_server.exe` should sit alongside
   `winhttp.dll` afterwards).
2. Start the server once so `BepInEx/plugins/` is created.
3. Drop `ServerZoneOwnership.dll` into `BepInEx/plugins/`.
4. Restart the server.
5. Confirm in `BepInEx/LogOutput.log`:
   ```
   [Info   :ServerZoneOwnership] 43 patch classes applied, none failed.
   [Message:ServerZoneOwnership] ===[SESSION START]=== <time> · vX.Y.Z · git <sha>
   ```
   If a patch class fails it is named explicitly and **only that feature** is
   inactive — patches are applied individually rather than through `PatchAll`,
   because one failing patch there aborts every later one and leaves the server
   silently half-patched.

## Known limitations

- **CPU and RAM scale with player count.** The server simulates AI and physics for
  the zones around every connected player — roughly one client's worth of
  non-render load each. Measured at 11–21% total CPU with 8 players on a Ryzen 5
  5600X, with the hottest thread the real ceiling, since Valheim's simulation is
  largely single-threaded.
- **The packet budget is still the binding constraint.** Distance throttling
  re-aims the available slots; it does not enlarge them.
- **The throttle filters less when a peer's queue is backed up**, since it works
  by delaying things sent recently and little has been sent. Ordering is
  unaffected: vanilla still sorts near-first, with long-waiting distant objects
  occasionally promoted, which is the intended behaviour either way.
- **Distance-only has rough edges.** Another player at 120m, or a fast arrow at
  60m, is updated at that tier's rate and may look steppy.
- **Group testing is scarce.** Much of the measurement above is from one or two
  players; the 8-player figures come from single sessions.
