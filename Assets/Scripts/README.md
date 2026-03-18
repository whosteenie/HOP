# Scripts Organization

## Purpose

This folder is organized around a few top-level domains:

- `Game`: gameplay, player, match flow, menus, UI, social, settings, progression
- `Network`: session/bootstrap/transport/auth/connection orchestration
- `Events`: shared event types and the central event bus
- `Diagnostics`: development-time logging and diagnostic helpers

The current architecture intentionally favors:

- event-driven boundaries between systems
- contracts at subsystem seams instead of concrete cross-domain controller references
- `Player -> Weapon`, not `Weapon -> Player`
- `Match -> objective modules` only where necessary, with score/state often flowing through events

## Top-Level Structure

```text
Scripts/
├── Game/
│   ├── Adapters/            # Bridges between game runtime and external/network/session systems
│   ├── Audio/               # Audio runtime and editor helpers
│   ├── Hopball/             # Hopball objective runtime, authority, presentation, indicator
│   ├── Match/               # Match flow, timers, KOTH, post-match, maps, score resolution
│   ├── Menu/                # Main menu, lobby flow, private match setup, options
│   ├── Player/              # Player runtime split by responsibility
│   ├── Progression/         # XP, challenges, progression state/store
│   ├── Settings/            # Game settings, runtime appliers, settings data
│   ├── Social/              # Chat, voice, Discord/Vivox, profanity, streamer mode
│   ├── UI/                  # HUD, scoreboard, modal host, overlays, misc runtime UI
│   └── Weapon/              # Weapon runtime, manager layer, presentation, Kinemation integration
├── Network/
│   ├── AntiCheat/
│   ├── Components/
│   ├── Contracts/
│   ├── Core/
│   ├── Session/
│   └── Steam/
├── Events/                  # EventBus + Gameplay/UI/Match/Session event definitions
├── Diagnostics/             # FlowLog, dev logging, event diagnostics, mesh/debug helpers
└── Imported/                # Third-party runtime/editor packages
```

## Game Folder

### `Game/Player`

`Player` is now the main composition root for player-owned behavior.

```text
Game/Player/
├── Combat/      # Health, death, tag, stats, ragdoll, damage application
├── Contracts/   # Player-facing contracts used by sibling player subsystems
├── Core/        # PlayerController and root-owned coordination/helpers
├── Input/       # Input handling, HUD-facing prompts, scoped input behavior
├── Movement/    # Movement runtime and movement abilities
└── Visual/      # Rendering, materials, animation, shadow, weapon-camera/shadow visuals
```

Notes:

- `PlayerController` is the player composition root, but should not become a dumping ground.
- When player subcontrollers need sibling state, prefer existing contracts in `Game.Player.Contracts`.
- Weapon-adjacent components that are actually player-owned now belong with `Player`, not `Weapon`.
- `Player.Visual` owns player presentation concerns, including player-owned weapon camera/shadow helpers.

### `Game/Weapon`

`Weapon` is a weapon-owned subsystem. It should not depend on concrete `Player.*` types.

```text
Game/Weapon/
├── Core/         # Weapon runtime, combat, mount, reload, fx relay, owner interfaces
├── Kinemation/   # Kinemation-specific integration and event relays
├── Manager/      # WeaponManager, switching, loadout, authority, FP presentation coordination
└── Presentation/ # Weapon bob/sway and other weapon-local presentation
```

Important boundary:

- `Weapon` depends on weapon-owned interfaces such as owner/runtime context contracts.
- `Player` implements those interfaces.
- The dependency direction should stay `Player -> Weapon`, not `Weapon -> Player`.

### `Game/Match`

`Match` owns shared match flow and mode-agnostic orchestration:

- timer/state transitions
- map/round/post-match flow
- objective score resolution
- match-owned player state proxies/authority

Objective-specific systems like Hopball and KOTH may plug into match flow, but UI should not read them directly.

### `Game/Hopball`

`Hopball` is an objective module, not general match flow.

It owns:

- hopball spawning/runtime
- holder/authority flow
- hopball-specific presentation and indicators

It should avoid reaching broadly into `Match` where events or pushed state are sufficient.

### `Game/UI`

UI should consume:

- events
- player-local data
- match-side read models/resolvers

UI should avoid reading objective managers directly when a match-side resolver or event path exists.

## Network Folder

`Network` is separated into its own assembly and should remain focused on connection/session/runtime networking concerns rather than gameplay ownership.

```text
Network/
├── AntiCheat/   # Rate limiting, anti-cheat helpers, logging
├── Components/  # NetworkBehaviour helpers/components
├── Contracts/   # Session/network-facing contracts
├── Core/        # Network manager, authority, identity, payloads
├── Session/     # Lobby, matchmaking, scene flow, gameplay readiness, voice/session flow
└── Steam/       # Steam transport/integration
```

## Events Folder

`Events` is its own assembly and acts as a neutral communication layer between systems.

Use it for:

- gameplay events
- UI refresh/request events
- match lifecycle and score queries
- post-match/player command events
- session/network coordination events

Prefer adding an event when it helps prevent direct cross-domain references.

## Diagnostics Folder

`Diagnostics` contains:

- `FlowLog`
- dev logging helpers such as `DevLog`
- debug event logging
- diagnostics-only utilities

Policy:

- use `DevLog`/assembly-local dev log wrappers for editor/development-only diagnostics
- use `FlowLog` for intentional runtime diagnostic events

## Assemblies

Current assembly definitions:

- `Game.asmdef`
- `Network.asmdef`
- `Events.asmdef`
- `Diagnostics.asmdef`

Guidance:

- do not add asmdefs just to mirror folders
- add them when they enforce a real boundary or provide meaningful compile isolation
- `Events` and `Diagnostics` are intentionally shared low-level dependencies

## Namespace Conventions

Use namespaces that match the owning domain rather than the nearest folder accident.

Examples:

- `Game.Player.Core`
- `Game.Player.Input`
- `Game.Player.Movement`
- `Game.Player.Visual`
- `Game.Player.Combat`
- `Game.Player.Contracts`
- `Game.Weapon.Core`
- `Game.Weapon.Manager`
- `Game.Weapon.Presentation`
- `Game.Weapon.Kinemation`
- `Game.Match`
- `Game.Hopball`
- `Game.UI.Core`, `Game.UI.HUD`, `Game.UI.Screens`, `Game.UI.Misc`
- `Network.Core`, `Network.Session`, `Network.Contracts`
- `Events`
- `Diagnostics`

When a file's current folder and namespace disagree, treat the namespace/ownership as the source of truth and move the file physically later when convenient.

## Practical Rules

- Prefer contracts or events over direct references across domain boundaries.
- Keep match/objective/UI relationships one-way where possible.
- Keep player as the owner of player-local presentation and input state.
- Keep weapon logic weapon-owned; do not let it reach into concrete player controllers.
- If a class is only "in Core" because it had nowhere else to go, it is a good candidate for re-homing.
