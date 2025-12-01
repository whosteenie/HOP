using Game.Audio;
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

        public void SetGameplayCameraActive(bool active) {
            if(fpCamera != null) {
                fpCamera.gameObject.SetActive(active);
            }

            if(deathCamera != null) {
                deathCamera.gameObject.SetActive(active);
            }
        }

        public void ResetVelocity() {
            if(movementController != null) {
                movementController.ResetVelocity();
            }
        }

        public void TryJump(float height = 2f) {
            if(movementController != null) {
                movementController.TryJump(height);
            }
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(movementController == null) return;

            if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Walk, attachToSelf: true, true);
            }
        }

        public void PlayRunSound() {
            if(!IsGrounded) return;

            if(movementController == null) return;

            if(movementController.CachedHorizontalSpeedSqr < 0.5f * 0.5f) {
                return;
            }

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Run, attachToSelf: true, true);
            }
        }

        public void PickupHopball() {
            if(playerHopballController != null) {
                playerHopballController.TryPickupHopball();
            } else {
                Debug.LogWarning("HopballController == null, cannot pick up hopball.");
            }
        }

        public bool IsHoldingHopball => playerHopballController != null && playerHopballController.IsHoldingHopball;

        public void DropHopball() {
            if(playerHopballController != null) {
                playerHopballController.DropHopball();
            }
        }

        #endregion

        #region Core Components

        public Transform PlayerTransform {
            get {
                if(playerTransform != null) return playerTransform;
                return transform;
            }
        }
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
        public NetworkSfxRelay SfxRelay => sfxRelay;
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

        public Vector3 GetHorizontalVelocity() {
            if(movementController != null) {
                return movementController.HorizontalVelocity;
            }
            return Vector3.zero;
        }

        public float GetVerticalVelocity() {
            if(movementController != null) {
                return movementController.VerticalVelocity;
            }
            return 0f;
        }

        public Vector3 GetFullVelocity {
            get {
                if(movementController != null) {
                    return movementController.FullVelocity;
                }
                return Vector3.zero;
            }
        }

        public float GetMaxSpeed() {
            if(movementController != null) {
                return movementController.MaxSpeed;
            }
            return 5f;
        }

        public float GetCachedHorizontalSpeedSqr() {
            if(movementController != null) {
                return movementController.CachedHorizontalSpeedSqr;
            }
            return 0f;
        }

        public float AverageVelocity => statsController != null ? statsController.averageVelocity.Value : 0f;

        public void SetVelocity(Vector3 horizontalVelocity) {
            if(movementController != null) {
                movementController.SetVelocity(horizontalVelocity);
            }
        }

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