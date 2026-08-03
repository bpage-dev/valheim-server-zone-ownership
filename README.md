# ServerZoneOwnership

A BepInEx plugin for the **Valheim dedicated server** that makes the server
take over zone ownership from clients. In vanilla, whichever player enters
a zone first owns it — meaning their machine simulates the zone's mobs and
physics for the whole group. Players on slow or laptop connections then drag
the entire session's simulation quality down. This mod moves that work to the
server, so the server's connection quality (usually the best in the group)
determines simulation quality for everyone.

The mod is **server-only**. Clients need no changes.

## What it does

Five Harmony patches, all no-ops on client builds:

- **`ZNet.GetReferencePosition`** — dedicated server's reference position defaults
  to `Vector3.zero` forever. Return the centroid of connected peer positions as
  a fallback for systems that consult this directly.
- **`ZDOMan.IsInPeerActiveArea`** — treat the server as "in active area" for any
  sector where at least one peer's active area falls, so peers can't steal back
  server-owned ZDOs when they diverge.
- **`SpawnSystem.UpdateSpawning`** — the periodic biome repopulator bails out
  early when `Player.m_localPlayer == null` (a client-side assumption). Skip
  that guard on the server so mobs actually spawn.
- **`ZoneSystem.Update`** — replace vanilla "load one zone around centroid" with
  per-peer, sector-deduped loading. Overlapping peers collapse into shared work.
- **`ZNetScene.CreateDestroyObjects`** — walk every unique sector covered by any
  peer once (via `ZDOMan.FindObjects`) and merge results before the vanilla
  create/remove passes.
- **`ZDOMan.ReleaseZDOS`** — iterate the unique-sector coverage set every 2s
  and claim any non-server-owned ZDOs for the server.

## Building

Requires .NET SDK (tested with 8.0.4).

1. Copy the following DLLs from a Valheim Dedicated Server install
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

3. Adjust the `<HintPath>` entries in `ServerZoneOwnership.csproj` if your
   layout differs.

4. `dotnet build -c Release`

Output: `bin/Release/ServerZoneOwnership.dll`.

## Installing

1. Install [BepInExPack_Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/)
   into your dedicated server folder (`valheim_server.exe` sits alongside
   `winhttp.dll` after install).
2. Start the server once so `BepInEx/plugins/` is created.
3. Drop `ServerZoneOwnership.dll` into `BepInEx/plugins/`.
4. Restart the server.
5. Verify in `BepInEx/LogOutput.log`:
   ```
   [Info: ServerZoneOwnership] ServerZoneOwnership vX.Y.Z patches applied.
   ```

Every 20 seconds the log also prints coverage stats:
```
[Coverage] N sectors owned by server across K peer(s). Sector bbox: ...
```

## Known limitations

- **CPU/RAM cost.** The server now simulates AI and physics for zones near
  every connected player. Roughly one client's worth of non-render load per
  active player. Modest for small groups; scales linearly with peer count.
- **Some client-only MonoBehaviours may misbehave on headless.** Anything that
  reaches for renderers, audio sources, or `Player.m_localPlayer` without
  guards can throw. If you see exceptions from a system we haven't patched,
  open an issue.
- **Meshnet / mods that already manage ownership** will conflict.
