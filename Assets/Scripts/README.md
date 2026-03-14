# Scripts Organization

## Structure Overview

```
Scripts/
├── Game/                    # Gameplay, UI, menus, match logic
│   ├── AI/                  # Bot agents, demo recording, edge zones
│   ├── Audio/               # Menu music, sound
│   ├── Hopball/             # Hopball gamemode
│   ├── HopDebug/            # Debug utilities
│   ├── Match/               # Match flow, KOTH, post-match, map definitions
│   ├── Menu/                # Main menu and in-game menus
│   │   └── Options/         # Options subsystem (tab handlers, helpers)
│   ├── Player/              # Player controllers, movement, grapple
│   │   ├── Core/            # PlayerController, PlayerTeamManager
│   │   ├── Movement/        # Movement, dash, wall run, mantle, speed trail
│   │   ├── Grapple/         # GrappleController, SwingGrapple
│   │   ├── Look/            # Look, UpperBodyPitch, PlayerInput
│   │   ├── Combat/          # Health, tag, stats, ragdoll, death camera
│   │   ├── Visual/          # Renderer, shadow, materials, animation
│   │   ├── Hopball/         # PlayerHopballController
│   │   └── Podium/          # PlayerPodiumController
│   ├── Progression/         # XP, challenges, progression store
│   ├── Security/            # Secure file I/O
│   ├── Settings/            # GameSettings, SettingsData, VideoSettingsApplier
│   ├── Social/              # Chat, voice, streamer mode, profanity filter
│   ├── Spawning/            # Spawn points, spawn manager
│   ├── UI/                  # HUD, scoreboard, chat UI, UIElementBase
│   │   ├── Core/            # UIElementBase, UINavigator, UIBindingHelper, UIModalHost, DropdownOpenStateBinder
│   │   ├── HUD/             # HUDManager, DamageVignette, GrappleUI, SniperOverlay, KillFeed
│   │   ├── Screens/         # Scoreboard, Chat, VoiceOverlay, InGameContextMenu
│   │   └── Misc/            # ChallengeUiRenderer, PostMatchXpDisplay, LoadingBallAnimation
│   ├── Visuals/             # Zone particles, effects
│   └── Weapons/             # Weapon logic, Kinemation bindings
│       ├── Core/            # Weapon, WeaponData, WeaponAnimationEvents, WeaponAmmoAuthority
│       ├── Kinemation/      # FpWeaponDriver, ReloadEventRelay, SoundEventRelay, BindingCatalog
│       ├── Manager/         # WeaponManager
│       ├── Presentation/    # Sway, Bob, CameraController, FpLighting, FpViewmodelPresentation
│       └── World/           # WorldWeaponBinding, WeaponWorldWeaponRegistry, WeaponShadowManager
├── Network/                 # Multiplayer, Steam, session management
│   ├── AntiCheat/
│   ├── Components/          # ClientNetworkTransform, etc.
│   ├── Core/                # LocalIdentity, ConnectionPayload, CustomNetworkManager, PrivateMatchTeamAssignments
│   ├── Diagnostics/
│   ├── Events/              # EventBus, SessionEvents, MatchEvents
│   │   └── Editor/          # Event bus editor tools
│   ├── Rpc/                 # NetworkDamageRelay, NetworkFxRelay
│   ├── Services/
│   ├── Session/             # SessionManager (partials)
│   ├── Singletons/          # KeybindManager, InitSceneManager, SceneTransitionManager, DisconnectTransitionController
│   ├── Steam/
│   └── UGS/
├── Editor/                  # Editor-only tools (not in builds)
│   ├── Game/                # Custom inspectors (WeaponData, PlayerMannequin)
│   ├── Build/               # Steam/Vivox build processors
│   └── Tools/               # Menu tools (Challenge generator, asset tools)
├── Discord/                 # Discord integration
├── OSI/                     # Off-screen indicators
└── Rendering/               # Menu blur, post-processing
```

## Namespace Conventions

- **Options**: `Game/Menu/Options/` → `Game.Menu.Options`
- **Editor scripts**: `Game.Editor`, `Game.Editor.Build`, `Game.Editor.Tools`, `Network.Events.Editor`
- **Player, Weapons, UI, Network**: Subfolders are for organization only; types stay in `Game.Player`, `Game.Weapons`, `Game.UI`, `Network`
- **Third-party integrations** (Discord, OSI, Rendering) use top-level namespaces
