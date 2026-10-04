# 📋 PLUGIN DEVELOPMENT SPECIFICATION

## 1. Project Overview & Objective
* **Plugin Name:** `LivestockAirdrops`
* **Target Platform:** Oxide / uMod (Rust Server Plugin)
* **Language:** C# (single `.cs` file, compiled by Oxide at load time)
* **Objective:** Create a plugin that allows players to throw custom-skinned **Supply Signals (Signal Grenades)** to call in a cargo plane. The cargo plane drops a live, fully functional livestock animal, carried down by a bunch of party balloons, (Cow, Bull, Sheep, Ram, or Lamb) based on the specific skin ID of the thrown signal.

---

## 2. Core Mechanics & Technical Flow
1. **Signal Detection:** `OnExplosiveThrown` / `OnExplosiveDropped` fire when a player throws (or rolls) a `supply.signal`. The item's `skin` is read at this point and, if it matches a configured animal, the signal entity is "armed" (remembered along with the thrower).
2. **Plane Takeover:** When the armed signal pops, vanilla spawns a `CargoPlane` and fires `OnCargoPlaneSignaled(CargoPlane, SupplySignal)`. The plugin claims that plane and rewrites its flight path:
   * Altitude = terrain height at the drop point + `Drop Altitude (Meters)`.
   * Flight duration recalculated from `Plane Flight Speed`.
   * The path is symmetric around the drop point, so the plane passes directly overhead.
   * The vanilla smoke stays as the visual marker.
   * Plugins cannot subclass `CargoPlane` or override `DropCrate()`, so the vanilla plane is reused and only its drop is intercepted.
3. **The Drop Sequence:** When a claimed plane drops its crate, `OnSupplyDropDropped(SupplyDrop, CargoPlane)` fires. The plugin kills the crate and calls `SpawnAirdropAnimal()` at the crate's position for each animal in `Spawn Count` (spread in a small ring, slightly staggered in height).
4. **The Descent:** The animal is the moving entity and the balloons are parented to it:
   * The animal is spawned, its AI is suspended (brain / navigator / `NavMeshAgent` disabled) and its rigidbody made kinematic.
   * A `DescentController` MonoBehaviour lowers the animal at `6 m/s × Descent Speed Modifier`.
   * `Balloon Count` `PartyBalloon` entities are parented to the animal's back (one centred, the rest in a ring), with random palette colours and the animal's `Balloon Text`. The prefab is auto-detected (the `StringPool` prefab carrying a `PartyBalloon`), currently `birthday_balloons_2025/star_balloon.deployed.prefab`.
   * Players can shoot balloons off: each one popped speeds the descent up, to 4× with none left (`Popped Balloons Speed Up Descent`).
   * Why not a parachute: the player `parachute.prefab` is a vehicle whose canopy only renders client-side with a real rider (even with `InUse`/`HasDriver` flags forced), and the supply-drop parachute is part of the crate model (`SupplyDrop.ParachuteRoot`), not a separate entity.
5. **The Landing Sequence:**
   * Ground height each step = downward raycast against Terrain / World / Construction / Deployed (so animals can land on buildings), falling back to the heightmap.
   * On contact: release the balloons (they drift up for 8 s, then are killed), snap the animal onto the NavMesh (`NavMesh.SamplePosition`, 4 m radius, max 2 m vertical change), restore rigidbody state, re-enable AI, and `Warp` any `NavMeshAgent`.

---

## 3. Configuration File Structure (`/oxide/config/LivestockAirdrops.json`)
Notification messages support `{name}` and `{count}` placeholders.

```json
{
  "General Settings": {
    "Drop Altitude (Meters)": 150.0,
    "Plane Flight Speed": 40.0,
    "Descent Speed Modifier": 1.0,
    "Prevent Damage To Animal On Landing": true,
    "Use Party Balloons": true,
    "Balloon Prefab (blank = auto-detect)": "",
    "Balloon Count": 5,
    "Balloon Height Above Animal": 1.3,
    "Popped Balloons Speed Up Descent": true,
    "Thrower Can Lead Animal Immediately": true
  },
  "Airdrop Configurations": [
    {
      "Animal Name": "Prize Cow",
      "Signal Skin ID": 3813316121,
      "Prefab Path": "assets/rust.ai/agents/cow/cow.prefab",
      "Spawn Count": 1,
      "Notification Message": "A cargo plane is delivering a live Cow to your location!"
    },
    {
      "Animal Name": "Enraged Bull",
      "Signal Skin ID": 3813325374,
      "Prefab Path": "assets/rust.ai/agents/bull/bull.prefab",
      "Spawn Count": 1,
      "Notification Message": "WARNING: An aggressive Bull is being dropped at your location!"
    },
    {
      "Animal Name": "Domestic Sheep",
      "Signal Skin ID": 3813326419,
      "Prefab Path": "assets/rust.ai/agents/sheep/sheep.prefab",
      "Spawn Count": 2,
      "Notification Message": "A delivery of {count} Sheep is descending from the skies!"
    },
    {
      "Animal Name": "Proud Ram",
      "Signal Skin ID": 3813327235,
      "Prefab Path": "assets/rust.ai/agents/sheep/sheep.prefab",
      "Sex (Random, Male, Female)": "Male",
      "Spawn Count": 1,
      "Notification Message": "A Ram is floating in. Mind the horns!"
    },
    {
      "Animal Name": "Baby Lamb",
      "Signal Skin ID": 3813328103,
      "Prefab Path": "assets/rust.ai/agents/sheep/lamb.prefab",
      "Spawn Count": 1,
      "Notification Message": "A tiny Lamb is floating down from the sky. Aww!"
    }
  ]
}
```

> ✅ Prefab paths verified on the live server (2026-10-04). There is **no ram prefab**: `sheep.prefab` covers both sexes, so the Proud Ram is a sheep with `"Sex": "Male"`. Cow (female) and bull (male) are separate prefabs, so `Sex` has no effect on them. The plugin still checks every path on load; `livestockairdrops.prefabs <words...>` searches the server's prefab list.

---

## 4. Hooks & Methods

| Hook / Method | Purpose |
|---|---|
| `OnExplosiveThrown(BasePlayer, BaseEntity, ThrownWeapon)` | Read item skin, arm matching supply signals. |
| `OnExplosiveDropped(BasePlayer, BaseEntity, ThrownWeapon)` | Same, for signals rolled/dropped with right-click. |
| `OnCargoPlaneSignaled(CargoPlane, SupplySignal)` | Claim the plane, rewrite altitude/speed, notify the thrower. |
| `OnSupplyDropDropped(BaseEntity, CargoPlane)` | Kill the vanilla crate from a claimed plane, spawn animals. |
| `OnEntityTakeDamage(BaseCombatEntity, HitInfo)` | Block non-player damage (fall/collision) while descending and for 5 s after landing. |
| `SpawnAirdropAnimal(Vector3 position, AnimalConfig config, ulong ownerId)` | Spawn animal (sex, trust), suspend AI, attach `DescentController` + balloons. |

Vanilla entities are removed with `entity.Kill()`.

---

## 5. Administrative Commands & Permissions
* **Permission:** `livestockairdrops.admin`
* **Command (chat `/giveanimal` and server console / RCON `giveanimal`):**
  * `giveanimal <player name or SteamID> <animal name from config>`
  * Animal name may contain spaces and matches case-insensitively (exact, then partial).
  * Creates a `supply.signal` with the configured skin, renames it to the animal name, and gives it to the target (online or sleeping).
  * Running it with no arguments lists the configured animals.

---

## 6. Edge Cases & Safety Constraints
* **Entity Sinking:** Landing uses a raycast + NavMesh sample. Without NavMesh nearby (e.g. a roof), the animal stays on the raycast surface.
* **Aggro State on Drop:** AI components stay disabled for the whole descent and are re-enabled only after the balloons are released.
* **Server Performance / Cleanup:**
  * Claimed planes are force-killed if they haven't dropped `secondsToTake + 60 s` after being called.
  * A descent longer than 180 s force-lands the animal.
  * Animals that drift out of map bounds are killed along with their balloons.
  * Balloons are spawned with saving disabled; released balloons are killed on `Unload()`.
  * On `Unload()` all claimed planes are killed (so they don't drop vanilla crates) and every descending animal is landed immediately.
* **Signals that never pop:** destroyed armed signals are purged from tracking on the next throw.

---

## 7. Gen2 Livestock Implementation Notes (from the server's game code)
* Livestock are `Rust.Ai.Gen2.LivestockAnimal : BaseNPC2`, driven by `FSMComponent` + `RustNavMeshAgent` on the Gen2 (Recast) navmesh, **not** Unity's NavMesh.
* **Descent:** `RustNavMeshAgent.Pause(source)` (same as the game's mount system) + `FSMComponent.SetFsmActive(false)`. The FSM switch is re-applied every physics step because `NpcSleepingComponent` turns the FSM back on when players come near.
* **Landing:** `BaseNPC2.TrySampleNavmesh(pos, 10 m)`, then `Unpause` (which warps the agent onto the navmesh) and `SetFsmActive(true)`. Animals that land off the navmesh log a warning.
* **Sex:** `IsBeingPurchased = true` + `IsMale` set before `Spawn()`, as `LivestockVendor` does; otherwise `InitShared` re-rolls the sex at random.
* **Ownership:** `SetFamiliarity(thrower, Livestock.trustToLead, FamiliarityReason.Purchased)` lets the thrower lead the animal straight away, like a vendor purchase.
* Non-Gen2 prefabs fall back to disabling brain/navigator behaviours and the Unity NavMesh.

---

## 8. Signal Icons (Workshop "CustomItem" skins)
* `supply.signal` has no store skins (`HasSkins: false`) and the Rust skin editor can't target it, but icon-only Workshop items with `"ItemType": "CustomItem"` (just `manifest.txt` + a 512×512 `icon.png`) replace its inventory/hotbar icon.
* Icons are generated by `icons/make_icons.py` (base: the game's supply signal icon; animal art: Twemoji, CC-BY 4.0) into `icons/workshop/<animal>/`, and uploaded with SteamCMD via `icons/upload_icon.bat` (one `.vdf` per animal; SteamCMD writes the new ID back into it).
* Workshop IDs: Prize Cow 3813316121, Enraged Bull 3813325374, Domestic Sheep 3813326419, Proud Ram 3813327235, Baby Lamb 3813328103, Baby Calf 3813328745.
* A client downloads a new skin the first time it sees the ID, so a fresh icon can take a moment to appear.

---

## 9. Testing Checklist (must be verified on a live server)
- [x] Livestock prefab paths exist (no load warnings), v1.1.0 loaded 2026-10-04.
- [x] Balloons render on the animal during descent (v1.3.0, 2026-10-04). Tune `Balloon Count` / `Balloon Height Above Animal` to taste.
- [ ] Suspended AI truly stays passive mid-air (especially bull/ram).
- [ ] Animals walk/graze normally after landing (navigator resumes).
- [ ] Landing on a foundation/roof works and doesn't clip.
