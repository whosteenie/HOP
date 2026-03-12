using Audio.Networking;
using Game.Player.Hopball;
using Game.Player.Look;
using Game.Weapons;
using Network.Components;
using Network.Rpc;
using OSI;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Public API and utility methods for PlayerController.
    /// Separated into partial class for better organization.
    /// </summary>
    public partial class PlayerController {

        #region Public API

        /// <summary>
        /// Hides all FP visuals (weapons, hopball) without destroying. Used to defer visible teardown
        /// during unexpected disconnect—hide first so when NGO despawns the player, the teardown is invisible.
        /// Call from SessionManager before starting the gradual fade.
        /// </summary>
        public void HideFpVisualsForDisconnectTransition() {
            if(!IsOwner) return;
            if(weaponManager != null) weaponManager.HideFpVisualsForDisconnectTransition();
            var hopball = GetComponent<PlayerHopballController>();
            if(hopball != null) hopball.HideFpVisualsForDisconnectTransition();
        }

        /// <summary>
        /// Enables/disables gameplay camera components without deactivating camera GameObjects.
        /// This keeps FP hierarchy (including KIN viewmodel/sound relays) alive during post-match.
        /// </summary>
        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.enabled = active;
            }

            if(deathCamera != null) {
                deathCamera.enabled = active;
            }

            if(weaponCameraController != null) {
                weaponCameraController.SetWeaponCameraEnabled(active);
            } else if(weaponCamera != null) {
                weaponCamera.enabled = active;
            }
        }

        /// <summary>
        /// Locks or unlocks local gameplay control during transitions (for example podium).
        /// </summary>
        public void SetPostMatchControlLock(bool locked, bool lockLook = true, bool resetVelocity = true) {
            if(!IsOwner) return;

            if(locked) {
                moveInput = Vector2.zero;
                lookInput = Vector2.zero;
                sprintInput = false;
                crouchInput = false;
                if(resetVelocity && movementController != null) {
                    movementController.ResetVelocity();
                }
            }

            LockLook = locked && lockLook;
        }

        /// <summary>
        /// Resets the player's current velocity.
        /// </summary>
        public void ResetVelocity() {
            if(movementController != null) {
                movementController.ResetVelocity();
            }
        }

        /// <summary>
        /// Attempts to make the player jump with a specific height.
        /// </summary>
        public void TryJump(float height = 2f) {
            if(movementController != null) {
                movementController.TryJump(height);
            }
        }

        /// <summary>
        /// Plays the player's walk sound if grounded and moving.
        /// </summary>
        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(movementController == null) return;

            if(characterController != null) {
                var actual = characterController.velocity;
                actual.y = 0f;
                if(actual.sqrMagnitude < 0.3f * 0.3f) {
                    return;
                }
            } else if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(!IsOwner) return;
            if(audioRelay == null) return;
            audioRelay.RequestPlayAttached("foley.tile.walk", new NetworkObjectReference(NetworkObject), allowOverlap: true);
        }

        /// <summary>
        /// Plays the player's run sound if grounded and moving fast enough.
        /// </summary>
        public void PlayRunSound() {
            var isWallRunning = wallRunController != null && wallRunController.IsWallRunning;
            if(!IsGrounded && !isWallRunning) return;

            if(movementController == null) return;

            if(characterController != null && IsGrounded) {
                var actual = characterController.velocity;
                actual.y = 0f;
                if(actual.sqrMagnitude < 0.5f * 0.5f) {
                    return;
                }
            } else if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(!IsOwner) return;
            if(audioRelay == null) return;
            audioRelay.RequestPlayAttached("foley.tile.run", new NetworkObjectReference(NetworkObject), allowOverlap: true);
        }

        /// <summary>
        /// Attempts to pick up a nearby Hopball.
        /// </summary>
        public void PickupHopball() {
            if(playerHopballController != null) {
                playerHopballController.TryPickupHopball();
            } else {
                Debug.LogWarning("HopballController == null, cannot pick up hopball.");
            }
        }

        public bool IsHoldingHopball => playerHopballController != null && playerHopballController.IsHoldingHopball;

        /// <summary>
        /// Drops the currently held Hopball.
        /// </summary>
        public void DropHopball() {
            if(playerHopballController != null) {
                playerHopballController.DropHopball();
            }
        }

        #endregion

        #region Core Components

        public Transform PlayerTransform => playerTransform != null ? playerTransform : transform;
        public CharacterController CharacterController => characterController;
        public PlayerInput PlayerInput => playerInput;
        public UnityEngine.InputSystem.PlayerInput UnityPlayerInput => unityPlayerInput;
        public AudioListener AudioListener => audioListener;
        public Target PlayerTarget => playerTarget;
        public LayerMask WorldLayer => worldLayer;
        public LayerMask PlayerLayer => playerLayer;
        public LayerMask EnemyLayer => enemyLayer;
        public LayerMask WeaponLayer => weaponLayer;
        public LayerMask HopballLayer => hopballLayer;

        #endregion

        #region Cameras

        public CinemachineCamera FpCamera => fpCamera;
        public Transform FpCameraTransform => fpCamera != null ? fpCamera.transform : null;
        public Camera WeaponCamera => weaponCamera;
        public CinemachineCamera DeathCamera => deathCamera;
        public WeaponCameraController WeaponCameraController => weaponCameraController;

        #endregion

        #region Player Model

        public GameObject PlayerModelRoot => playerModelRoot;
        public SkinnedMeshRenderer PlayerMesh => playerMesh;
        public Material[] PlayerMaterials => playerMaterials;
        public PlayerVisualController VisualController => visualController;
        public PlayerAnimationController AnimationController => animationController;
        public PlayerShadow PlayerShadow => playerShadow;
        public PlayerRenderer PlayerRenderer => playerRenderer;
        public UpperBodyPitch UpperBodyPitch => upperBodyPitch;
        public PlayerRagdoll PlayerRagdoll => playerRagdoll;
        public SpeedTrail SpeedTrail => speedTrail;
        public Transform DeathCameraTarget => deathCameraTarget;

        #endregion

        #region Gameplay Controllers

        public PlayerMovementController MovementController => movementController;
        public PlayerLookController LookController => lookController;
        public PlayerStatsController StatsController => statsController;
        public PlayerHealthController HealthController => healthController;
        public PlayerTagController TagController => tagController;
        public PlayerPodiumController PodiumController => podiumController;
        public PlayerHopballController PlayerHopballController => playerHopballController;
        public PlayerTeamManager TeamManager => playerTeamManager;
        public MantleController MantleController => mantleController;

        public DeathCameraController DeathCameraController => deathCameraController;

        #endregion

        #region Weapons

        public WeaponManager WeaponManager => weaponManager;

        public GrappleController GrappleController => grappleController;

        // public SwingGrapple SwingGrapple => swingGrapple;
        public NetworkDamageRelay DamageRelay => damageRelay;
        public NetworkFxRelay FxRelay => fxRelay;
        public NetworkAudioRelay AudioRelay => audioRelay;
        public CinemachineImpulseSource ImpulseSource => impulseSource;
        // public MeshRenderer WorldWeaponRenderer => worldWeapon;
        public GameObject[] WorldWeaponPrefabs => worldWeaponPrefabs;
        public Weapon WeaponComponent => weaponComponent;
        public Animator PlayerAnimator => playerAnimator;
        public Transform WorldWeaponSocket => worldWeaponSocket;

        #endregion

        #region Network Components

        public ClientNetworkTransform ClientNetworkTransform => clientNetworkTransform;
        public NetworkVariable<float> NetHealth => netHealth;
        public NetworkVariable<bool> NetIsDead => netIsDead;
        public NetworkVariable<bool> NetIsCrouching => netIsCrouching;
        public NetworkVariable<bool> NetIsSliding => netIsSliding;
        public NetworkVariable<bool> NetIsJumping => netIsJumping;
        public NetworkVariable<bool> NetIsFalling => netIsFalling;
        public NetworkVariable<bool> NetIsWallRunning => netIsWallRunning;
        public NetworkVariable<bool> NetIsRightWallRun => netIsRightWallRun;
        public NetworkVariable<float> NetWallRunDirection => netWallRunDirection;
        public NetworkVariable<int> Kills => kills;
        public NetworkVariable<int> Deaths => deaths;
        public NetworkVariable<int> Assists => assists;
        public NetworkVariable<float> DamageDealt => damageDealt;
        public NetworkVariable<int> PlayerMaterialIndex => playerMaterialIndex;
        public NetworkVariable<FixedString64Bytes> PlayerName => playerName;
        public int PingMs => statsController != null ? statsController.pingMs.Value : 0;

        #endregion

        #region Player State

        public Vector3 Position => PlayerTransform.position;
        public Quaternion Rotation => PlayerTransform.rotation;
        public bool IsDead => netIsDead is { Value: true };
        public bool IsCrouching => netIsCrouching is { Value: true };
        public bool IsGrounded => movementController != null && movementController.IsGrounded;

        #endregion

        #region Velocity Helpers

        /// <summary>
        /// Gets the current horizontal velocity vector.
        /// </summary>
        public Vector3 GetHorizontalVelocity() {
            return movementController != null ? movementController.HorizontalVelocity : Vector3.zero;
        }

        /// <summary>
        /// Gets the current vertical velocity value.
        /// </summary>
        public float GetVerticalVelocity() {
            return movementController != null ? movementController.VerticalVelocity : 0f;
        }

        /// <summary>
        /// Gets the full velocity vector including horizontal and vertical components.
        /// </summary>
        public Vector3 GetFullVelocity => movementController != null ? movementController.FullVelocity : Vector3.zero;

        /// <summary>
        /// Gets the maximum movement speed currently allowed.
        /// </summary>
        public float GetMaxSpeed() {
            return movementController != null ? movementController.MaxSpeed : 5f;
        }

        /// <summary>
        /// Gets the cached horizontal speed squared value.
        /// </summary>
        public float GetCachedHorizontalSpeedSqr() {
            return movementController != null ? movementController.CachedHorizontalSpeedSqr : 0f;
        }

        public float AverageVelocity => statsController != null ? statsController.averageVelocity.Value : 0f;
        public float ObservedServerMovementSpeed => _lastObservedServerMovementSpeed;

        public void SetVelocity(Vector3 horizontalVelocity) {
            if(movementController != null) {
                movementController.SetVelocity(horizontalVelocity);
            }
        }

        /// <summary>
        /// Adds a vertical velocity boost to the player.
        /// </summary>
        public void AddVerticalVelocity(float verticalBoost) {
            if(movementController != null) {
                movementController.AddVerticalVelocity(verticalBoost);
            }
        }

        #endregion

        #region Gun Tag Stats

        public int Tags => tagController != null ? tagController.tags.Value : 0;
        public int Tagged => tagController != null ? tagController.tagged.Value : 0;
        public int TimeTagged => tagController != null ? tagController.timeTagged.Value : 0;
        public bool IsTagged => tagController != null && tagController.isTagged.Value;

        #endregion

        #region Podium Methods

        public void ForceRespawnForPodiumServer() {
            if(podiumController != null) {
                podiumController.ForceRespawnForPodiumServer();
            }
        }

        public void TeleportToPodiumFromServer(Vector3 position, Quaternion rotation) {
            if(podiumController != null) {
                podiumController.TeleportToPodiumFromServer(position, rotation);
            }
        }

        #endregion
    }
}
