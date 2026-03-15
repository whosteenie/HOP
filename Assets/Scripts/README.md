# Scripts Organization

## Structure Overview

```
Scripts/
├── Game/                      # Gameplay, UI, menus, match logic, social, settings
│   ├── Hopball/               # Hopball gamemode (controller, visual, spawn, indicator)
│   ├── Match/                 # Match flow, KOTH, post-match, map definitions, podium
│   ├── Menu/                  # Main menu and in-game menus
│   │   └── Options/           # Options subsystem (tab handlers, helpers)
│   ├── Player/                # Player controllers, movement, combat, visuals
│   │   ├── Core/              # PlayerController, PlayerTeamManager, network state, spawn presentation
│   │   ├── Movement/          # Movement, dash, wall run, mantle, grapple, speed trail
│   │   ├── Look/              # Look, UpperBodyPitch, PlayerInput
│   │   ├── Combat/            # Health, tag, stats, ragdoll, death camera
│   │   └── Visual/            # Renderer, shadow, materials, animation, mannequin
│   ├── Progression/           # XP, challenges, progression store
│   ├── Settings/              # GameSettings, SettingsData, VideoSettingsRuntimeApplier
│   ├── Social/                # Chat, voice, Discord, Vivox, streamer mode, profanity filter
│   ├── Spawning/              # Spawn points, spawn manager
│   ├── UI/                    # HUD, scoreboard, chat UI, modal host
│   │   ├── Core/              # UIElementBase, UINavigator, UIBindingHelper, UIModalHost, DropdownOpenStateBinder
│   │   ├── HUD/               # HUDManager, DamageVignette, GrappleUI, SniperOverlay, KillFeed
│   │   ├── Screens/           # Scoreboard, Chat, VoiceOverlay, InGameContextMenu
│   │   └── Misc/              # MenuBlurVolumeController, ChallengeUiRenderer, PostMatchXpDisplay, LoadingBallAnimation
│   └── Weapon/                # Weapon logic, Kinemation bindings
│       ├── Core/              # Weapon, WeaponData, WeaponCombat, WeaponMount, WeaponReload, WeaponAmmoAuthority
│       ├── Kinemation/        # KinFpWeaponDriver, KinDriverAudio, KinGrappleClavicle, binding catalog, etc.
│       ├── Manager/           # WeaponManager, WeaponAuthority, WeaponSwitch, WeaponLoadout, FpPresentation, FpLighting
│       ├── Presentation/      # WeaponSway, WeaponBob, WeaponCameraController
│       └── World/             # WorldWeaponBinding, WeaponWorldWeaponRegistry, WeaponShadowManager
├── Network/                   # Multiplayer, Steam, session management
│   ├── AntiCheat/             # AntiCheatLogger, RpcRateLimiter, AntiCheatConfig
│   ├── Components/            # ClientNetworkTransform, ClientNetworkAnimator, OwnerNetworkAnimator
│   ├── Core/                  # CustomNetworkManager, NetworkAuthority, LocalIdentity, ConnectionPayload, PrivateMatchTeamAssignments, UgsAuthService
│   ├── Rpc/                   # NetworkDamageRelay, NetworkFxRelay
│   ├── Session/               # SessionManager, SessionParty, SessionMatchmaker, SessionVoice, interfaces (namespace Network.Session, Network.Session.Interface)
│   ├── Singletons/            # KeybindManager, InitSceneManager, SceneTransitionManager, DisconnectTransitionController, PlayerMaterialPacketManager
│   └── Steam/                 # SteamManager, FacepunchTransport
├── Events/                    # Central event bus
│   ├── Editor/                # EventBusDebugWindow, EventBusLogSettingsEditor
│   └── (event types, EventBus, EventBusLogSettings, MatchEvents, SessionEvents, GameplayEvents, UIEvents, etc.)
├── Diagnostics/               # Network diagnostics
│   └── (DebugHelpers, FlowLog, FlowEventIds, DebugEventLogger, MeshTriangleWarningDiagnostics)
├── Editor/                    # Editor-only tools (not in builds)
│   ├── Build/                 # Steam/Vivox build processors, SteamAppIdEditorHelper (namespace Editor.Build)
│   ├── Tools/                 # ChallengeAssetGenerator, EditorAssetTools (namespace Editor.Tools)
│   └── (WeaponDataEditor, WeaponConfigurator, PlayerMannequinConfigEditor, GrappleAnimViewmodelOffsetApplier – namespace Editor or Editor.Game depending on file)
├── OSI/                       # Off-screen indicators (third-party; to be replaced later). Namespace OSI.
```

## Namespace Conventions

- **Game**: `Game.Menu`, `Game.Menu.Options`, `Game.Player.Core`, `Game.Player.Movement`, `Game.Weapons.Core`, `Game.Weapons.Kinemation`, `Game.UI.Core`, `Game.UI.HUD`, `Game.UI.Screens`, `Game.UI.Misc`, `Game.Match`, `Game.Hopball`, `Game.Social`, `Game.Settings`, `Game.Progression`, `Game.Spawning`.
- **Network**: `Network.Core`, `Network.Session`, `Network.Session.Interface`, `Network.Events`, `Network.Events.Editor`, `Network.Diagnostics`, `Network.Rpc`, `Network.Steam`, `Network.AntiCheat`, `Network.Components`, `Network.Singletons`.
- **Editor**: `Editor`, `Editor.Build`, `Editor.Tools` (top-level Editor folder; optional target is `Game.Editor.*` under `Game/Editor/` – see `docs/SCRIPTS_FOLDER_REORGANIZATION.md`).
- **Events**: Lives under `Scripts/Events/` with namespace `Network.Events`; used by Game, Network, and OSI.
- **OSI**: Top-level namespace `OSI`; third-party code, leave unchanged.