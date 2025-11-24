# PlayerController Refactoring Plan

## Current Responsibilities Analysis

### PlayerController.cs (1779 lines) - TOO LARGE
1. **Movement** (~200 lines)
   - HandleMovement, CalculateHorizontalVelocity, ApplyGravity, MoveCharacter
   - AirStrafe, ApplyFriction, CheckCeilingHit
   - UpdateMaxSpeed, HandleCrouch, UpdateCharacterControllerCrouch

2. **Look/Camera** (~50 lines)
   - HandleLook, UpdatePitch, UpdateYaw, UpdateSpeedFov
   - CurrentPitch property

3. **Health/Damage/Death** (~300 lines)
   - ApplyDamageServer_Auth, DieServer, DieClientRpc
   - HandleHealthRegeneration, ResetHealthAndRegenerationState
   - Respawn logic (DoRespawnServer, TeleportAfterPreparation, etc.)

4. **Animation** (~100 lines)
   - UpdateAnimator, UpdateFallingState, UpdateTurnAnimation
   - PlayJumpAnimationServerRpc, PlayLandingAnimationServerRpc

5. **Material/Visuals** (~200 lines)
   - ApplyPlayerMaterial, UpdateTaggedGlow
   - SetRenderersEnabled, SetSkinnedMeshRenderersShadowMode
   - ForceRendererBoundsUpdate, VerifyAndFixVisibility
   - Renderer cache management

6. **Network State** (~100 lines)
   - NetworkVariable management
   - OnNetworkSpawn/Despawn
   - Network variable change handlers

7. **Tag Mode Logic** (~100 lines)
   - Tag transfer logic in ApplyDamageServer_Auth
   - Tag state change handlers
   - Tag glow effects

8. **Podium Logic** (~150 lines)
   - ForceRespawnForPodiumServer, TeleportToPodiumFromServer
   - SnapBonesToRoot, TeleportAndSnapToPodium

9. **Weapon State** (~50 lines)
   - ResetWeaponState

10. **Velocity Tracking** (~50 lines)
    - TrackVelocity, SubmitVelocitySampleServerRpc

11. **Misc Utilities** (~100 lines)
    - SetGameplayCameraActive, SetWorldModelVisibleRpc
    - ResetVelocity, TryJump, PlayWalkSound, PlayRunSound

## Proposed Refactoring

### New Classes to Create

#### 1. PlayerMovementController.cs (NEW)
**Responsibility**: All movement-related logic
- HandleMovement, CalculateHorizontalVelocity, ApplyGravity
- AirStrafe, ApplyFriction, CheckCeilingHit
- UpdateMaxSpeed, HandleCrouch, UpdateCharacterControllerCrouch
- TryJump, ResetVelocity, SetVelocity, AddVerticalVelocity
- Movement constants (WalkSpeed, SprintSpeed, etc.)

**Dependencies**: CharacterController, Transform, LayerMask

#### 2. PlayerLookController.cs (NEW)
**Responsibility**: Camera/look logic
- HandleLook, UpdatePitch, UpdateYaw
- UpdateSpeedFov
- CurrentPitch property
- FOV constants and settings

**Dependencies**: CinemachineCamera, Transform

#### 3. PlayerHealthController.cs (NEW)
**Responsibility**: Health, damage, death, respawn
- ApplyDamageServer_Auth (move tag logic elsewhere)
- DieServer, DieClientRpc
- HandleHealthRegeneration, ResetHealthAndRegenerationState
- All respawn logic (DoRespawnServer, TeleportAfterPreparation, etc.)
- Health constants (MaxHealth, RegenDelay, RegenRate)

**Dependencies**: PlayerController (for state), NetworkVariables

#### 4. PlayerAnimationController.cs (NEW)
**Responsibility**: Animation state management
- UpdateAnimator, UpdateFallingState, UpdateTurnAnimation
- PlayJumpAnimationServerRpc, PlayLandingAnimationServerRpc
- Animation parameter hashes
- Falling/jumping state tracking

**Dependencies**: Animator, PlayerController (for state)

#### 5. PlayerVisualController.cs (NEW)
**Responsibility**: Material, renderer, and visual state management
- ApplyPlayerMaterial, UpdateTaggedGlow
- SetRenderersEnabled, SetSkinnedMeshRenderersShadowMode
- SetWorldWeaponRenderersShadowMode, SetWorldWeaponPrefabsShadowMode
- ForceRendererBoundsUpdate, VerifyAndFixVisibility
- Renderer cache management (RefreshRendererCacheIfNeeded, InvalidateRendererCache)
- SetWorldModelVisibleRpc
- DelayedBoundsUpdate coroutine

**Dependencies**: SkinnedMeshRenderer, Material[], MaterialPropertyBlock

#### 6. PlayerTagController.cs (NEW)
**Responsibility**: Tag mode specific logic
- Tag transfer logic (extract from ApplyDamageServer_Auth)
- OnTaggedStateChanged handler
- UpdateTaggedGlow (move from PlayerController)
- Tag mode network variables (tags, tagged, timeTagged, isTagged)
- Tag sound effects

**Dependencies**: PlayerVisualController (for glow), NetworkVariables

#### 7. PlayerPodiumController.cs (NEW)
**Responsibility**: Podium-specific logic
- ForceRespawnForPodiumServer, ForceRespawnForPodiumClientRpc
- TeleportToPodiumFromServer, TeleportToPodiumOwnerClientRpc
- TeleportAndSnapToPodium, SnapBonesToRoot
- SnapPodiumVisualsClientRpc
- Podium fields (rootBone, _podiumAnimator, _podiumSkinned, etc.)

**Dependencies**: Animator, Transform, PlayerController

#### 8. PlayerStatsController.cs (NEW)
**Responsibility**: Stats tracking and network variables
- Velocity tracking (TrackVelocity, SubmitVelocitySampleServerRpc)
- Ping tracking (UpdatePing)
- Stats network variables (kills, deaths, assists, damageDealt, averageVelocity, pingMs)
- Timer management for periodic updates

**Dependencies**: NetworkVariables, NetworkManager

### Classes to Enhance

#### 9. PlayerOutline.cs (ENHANCE)
**Current**: Basic outline rendering
**Enhance**: 
- Merge PlayerController's UpdateTaggedGlow into this
- Handle both team-based outlines AND tag mode glow
- Centralize all outline/glow logic here

#### 10. PlayerShadow.cs (ENHANCE)
**Current**: Sets shadow modes on spawn
**Enhance**:
- Move SetSkinnedMeshRenderersShadowMode logic here
- Move SetWorldWeaponRenderersShadowMode logic here
- Centralize all shadow casting mode management

### Execution Order Issues

#### Current Problems:
1. **GrappleController**: Uses `Start()` for SetupGrappleLine - should be `Awake()` or `OnNetworkSpawn()`
2. **SpeedTrail**: Uses `OnNetworkSpawn()` but also needs controller reference - should cache in `Awake()`
3. **UpperBodyPitch**: Uses `Awake()` - good, but should verify it runs before PlayerController
4. **PlayerShadow**: Uses `OnNetworkSpawn()` - should run after renderers are set up
5. **PlayerRagdoll**: Uses `OnNetworkSpawn()` - good
6. **PlayerController**: Massive `OnNetworkSpawn()` - should be split

#### Recommended Execution Order:
```
1. Awake() - Component caching, initialization
   - UpperBodyPitch.Awake()
   - PlayerController (component refs only)
   - GrappleController (setup line)
   
2. Start() - Cross-component references
   - SpeedTrail (find controller)
   
3. OnNetworkSpawn() - Network initialization
   - PlayerRagdoll.OnNetworkSpawn()
   - PlayerShadow.OnNetworkSpawn()
   - PlayerController.OnNetworkSpawn() (delegated to sub-controllers)
   - GrappleController.OnNetworkSpawn()
   - SpeedTrail.OnNetworkSpawn()
```

### Partial Class Structure

After refactoring, PlayerController should become:

```csharp
// PlayerController.cs (Main - Network state, coordination)
public partial class PlayerController : NetworkBehaviour {
    // Network variables
    // Component references
    // Public API
    // Coordination methods
}

// PlayerController.Movement.cs
public partial class PlayerController {
    // Movement logic
}

// PlayerController.Look.cs
public partial class PlayerController {
    // Look/camera logic
}

// PlayerController.Health.cs
public partial class PlayerController {
    // Health/damage/death/respawn
}

// PlayerController.Animation.cs
public partial class PlayerController {
    // Animation state
}

// PlayerController.Visual.cs
public partial class PlayerController {
    // Visual/material management
}

// PlayerController.Tag.cs
public partial class PlayerController {
    // Tag mode logic
}

// PlayerController.Podium.cs
public partial class PlayerController {
    // Podium logic
}

// PlayerController.Stats.cs
public partial class PlayerController {
    // Stats tracking
}
```

### Migration Strategy

#### Phase 1: Extract Independent Systems
1. Create PlayerStatsController - move velocity/ping tracking
2. Create PlayerTagController - move tag mode logic
3. Create PlayerPodiumController - move podium logic
4. Enhance PlayerOutline - merge tag glow logic

#### Phase 2: Extract Core Systems
5. Create PlayerVisualController - move renderer/material logic
6. Create PlayerAnimationController - move animation logic
7. Enhance PlayerShadow - move shadow mode logic

#### Phase 3: Extract Movement Systems
8. Create PlayerMovementController - move movement logic
9. Create PlayerLookController - move look/camera logic
10. Create PlayerHealthController - move health/death/respawn

#### Phase 4: Convert to Partial Classes
11. Split PlayerController into partial classes
12. Update all references
13. Fix execution order issues

### Benefits

1. **Single Responsibility**: Each class has one clear purpose
2. **Easier Debugging**: Issues isolated to specific systems
3. **Better Testability**: Can test systems independently
4. **Reduced Conflicts**: Less chance of merge conflicts
5. **Clearer Architecture**: Easier to understand and maintain
6. **Better Performance**: Can optimize individual systems

### Dependencies Map

```
PlayerController (Core)
├── PlayerMovementController
├── PlayerLookController
├── PlayerHealthController
├── PlayerAnimationController
├── PlayerVisualController
│   ├── PlayerOutline (enhanced)
│   └── PlayerShadow (enhanced)
├── PlayerTagController
│   └── PlayerVisualController
├── PlayerPodiumController
├── PlayerStatsController
├── WeaponManager
├── GrappleController
├── MantleController
└── UpperBodyPitch
```

