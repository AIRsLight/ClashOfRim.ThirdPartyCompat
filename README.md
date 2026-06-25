# ClashOfRim Third-Party Compatibility

This optional package contains compatibility hooks for RimWorld mods that need
extra handling when ClashOfRim projects, transfers, or settles multiplayer state.
It is distributed separately from the main mod so compatibility patches can be
enabled only when the corresponding third-party mods are present.

The compatibility package has two parts:

- a RimWorld mod that registers client-side compatibility hooks;
- optional server plugins that extend save indexing, baseline collection, and
  raid settlement logic.

## Load Order

Use this package after the main ClashOfRim mod and after the supported mods it
needs to patch:

1. Harmony
2. ClashOfRim
3. Supported third-party mods according to their own load-order rules
4. ClashOfRim Third-Party Compatibility

The package detects supported mods at runtime. If a supported mod is not loaded,
its compatibility hooks are skipped.

## Supported Client Compatibility

### Adaptive Storage Framework

Package id: `adaptive.storage.framework`

Adds remote-map projection handling for packed storage containers. The
compatibility layer preserves the relationship between storage containers and
their packed contents when another player's map is loaded as a remote map, and
marks packed remote contents so local pawns do not treat them as ordinary loose
items.

### Vehicle Framework

Package id: `SmashPhil.VehicleFramework`

Adds support for vehicle-aware remote maps and raids. Current hooks cover
vehicle caravan entry into remote maps, aerial vehicle landing guards, vehicle
defense-point behavior, and vehicle assault behavior during raids. The
compatibility package also contributes server-side handling for vehicle save
indexes, vehicle hit point baselines, vehicle cargo, and raid settlement damage.

### Melee Animation

Package id: `co.uk.epicguru.meleeanimation`

Removes transient melee-animation runtime state from projected remote maps and
snapshot saves. This avoids loading remote maps with stale animation state that
belongs to another client's runtime session.

### Vanilla Expanded Framework

Package id: `oskarpotocki.vanillafactionsexpanded.core`

Adds guards for framework-owned world authority/runtime state and restores MVCF
pawn verb managers after pawn transfer or pawn preview restoration. This keeps
transferred pawns closer to their original combat behavior when Vanilla Expanded
Framework is active.

### Humanoid Alien Races

Package id: `erdelf.HumanoidAlienRaces`

Registers metadata support for statue-like objects that preserve alien-race
pawn appearance state. This is used when those objects are listed, traded,
gifted, or restored through ClashOfRim thing references.

### Facial Animation

Package id: `Nals.FacialAnimation`

Registers metadata support for statue-like objects that preserve facial
animation state. This shares the statue transfer path used by Humanoid Alien
Races compatibility.

## Server Plugins

Server plugins are built into `Build\ServerPlugins` and copied into the server
package `Plugins/` directory by `Tools\BuildWindowsServer.ps1`.

### Adaptive Storage Server Plugin

Plugin id: `AIRsLight.ClashOfRim.AdaptiveStorage`

Adds a save-index extension for Adaptive Storage packed contents so the server
can reason about container contents without flattening them into ordinary map
items.

### Vehicle Framework Server Plugin

Plugin id: `AIRsLight.ClashOfRim.VehicleFramework`

Adds server-side vehicle indexing, vehicle hit point baseline requirements, and
raid settlement snapshot editing for vehicles and vehicle cargo.

## Scope

This compatibility package only handles behaviors that affect ClashOfRim
multiplayer state, such as remote-map projection, pawn/thing transfer, save
indexing, baseline validation, and raid settlement. Normal single-player
behavior of the supported third-party mods is intentionally left to those mods.
