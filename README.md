# HOP

HOP is a fast-paced multiplayer movement shooter built in Unity.  
It focuses on high-skill traversal, readable combat, and fully online play with public matchmaking.

---

## Overview

HOP is an in-progress game project:

- First-person movement system with advanced traversal mechanics.
- Multiple game modes and full match flow (from lobby to post-match).
- Online multiplayer with public matchmaking and relay-backed connectivity.
- A codebase structured for iteration: clear separation between gameplay, networking, UI, and tooling.

---

## Gameplay Features

- **Advanced traversal**
  - Grapple for pulling the player toward a point and chaining movement.
  - Wall-running on eligible surfaces with velocity redirection.
  - Ledge mantling for climbing vertical obstacles.
  - Air-strafing, slides, bunny-hopping, and momentum preservation tuned for “feel.”

- **Core modes**
  - **Hopball** – ball-possession objective mode designed around traversal.
  - **Deathmatch / Team Deathmatch** – standard elimination modes.
  - **King of the Hill** – moving capture point that forces repositioning.
  - **Gun Tag** – one player is “it” and must tag others via combat.

- **Combat**
  - Weapon system supporting multiple weapon types and fire modes.
  - Hit feedback via damage indicators, hitmarkers, and killfeed.
  - First-person viewmodel presentation with separate logic for sway, bob, and animations.

- **Momentum mechanics**
  - **Momentum damage multiplier**
    - Players build a damage multiplier by maintaining speed and staying in motion.
    - Standing still (or slowing down) causes the multiplier to decay, encouraging constant repositioning.
  - **Speed trails (multiplier feedback)**
    - Players emit speed trails that scale with their current movement multiplier.
    - Trails provide immediate visual feedback during high-speed combat.

- **Social & progression**
  - Lobbying and public matchmaking so players can find and join matches.
  - Basic progression and post-match summary (e.g., XP, challenges) to support repeat play.

---

## Key Systems

### Movement & Traversal

The movement controller is organized into focused components for:

- Ground and air movement, friction, and acceleration.
- Grapple behavior, including momentum preservation and jump-pad interaction.
- Wall-running, mantling, and jump-pad launches with apex tracking and compensation.

The goal is to keep the “feel” of the game configurable and debuggable rather than hard-coded.

### Networking & Matchmaking

- Uses Unity’s multiplayer stack to support:
  - Lobby creation and discovery.
  - Relay-based connectivity so players can join without direct IP access.
  - Distributed authority + host migration, allowing matches to continue if the session owner disconnects.
  - Backfill, so open slots can be filled and lobbies don’t gradually dwindle as players leave.
  - Match lifecycle: lobby → loading → match → post-match → back to lobby.

- Player state, scores, objectives, and projectiles are synchronized over the network with an emphasis on:
  - Ownership and authority boundaries that are easy to reason about.
  - Clear separation between network messages and gameplay logic.

### Game Modes & Rules

Game rules are implemented as discrete mode controllers that handle:

- Scoring and win conditions per mode (Hopball, KOTH, DM/TDM, Gun Tag).
- Round flow, including sudden death or overtime where applicable.
- Integration with the HUD, scoreboard, and announcer/UI feedback.

### UI & UX

- Menu flow for:
  - Main menu, settings, and matchmaking.
  - In-game pause/options.
- HUD elements for:
  - Health, ammo, objectives, timers, and team scores.
  - Chat and voice indicators where supported.
- Designed to keep information readable during high-speed movement.

---

## Architecture Overview

At a high level, the project is structured into:

- **Gameplay** – player controllers, weapons, health/damage, game modes, progression.
- **Networking** – session management, matchmaking, replication, and diagnostics.
- **UI** – menus, HUD, overlays, and supporting UI frameworks.
- **Editor & tooling** – custom inspectors and utilities to support content creation and debugging.

Responsibility is split so that:

- Movement, combat, and visuals are separate concerns.
- Network-related code is grouped and testable in isolation.
- UI components can evolve without rewriting core gameplay.

For more detail, see `Assets/Scripts/README.md` in the repository.

---

## Technology

- **Engine**: Unity (URP pipeline)
- **Multiplayer**: Unity multiplayer stack (lobbies, relay, netcode-style synchronization)
- **Audio / VFX**: Unity’s built-in audio system, post-processing, and VFX Graph
- **Async / utilities**: Task-based utilities where appropriate for async workflows
