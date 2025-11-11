using System.Collections;
using Game.Weapons;
using Network;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UIElements;

namespace Game.Player {
    public class PlayerController : NetworkBehaviour {
        #region Constants

        private const float WalkSpeed = 5f;
        private const float SprintSpeed = 10f;
        private const float JumpHeight = 2f;
        private const float CrouchSpeed = 2.5f;
        private const float StandHeight = 1.7f;
        private const float CrouchHeight = 1.1f;
        private const float StandCollider = 1.9f;
        private const float CrouchCollider = 1.3f;
        private const float StandCheckHeight = StandCollider - CrouchCollider;
        private const float PitchLimit = 90f;
        private const float GravityScale = 3f;

        #endregion

        #region Animation Parameter Hashes

        private static readonly int MoveXHash = Animator.StringToHash("moveX");
        private static readonly int MoveYHash = Animator.StringToHash("moveY");
        private static readonly int LookXHash = Animator.StringToHash("lookX");
        private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");
        private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
        private static readonly int JumpTriggerHash = Animator.StringToHash("JumpTrigger");
        private static readonly int LandTriggerHash = Animator.StringToHash("LandTrigger");
        private static readonly int IsJumpingHash = Animator.StringToHash("IsJumping");
        private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");

        #endregion

        #region Serialized Fields

        [Header("Movement Parameters")] [SerializeField]
        private float acceleration = 15f;

        [SerializeField] private float airAcceleration = 50f;
        [SerializeField] private float maxAirSpeed = 5f;
        [SerializeField] private float friction = 8f;

        [Header("Look Parameters")] [SerializeField]
        public Vector2 lookSensitivity = new(0.1f, 0.1f);

        [Header("Components")] [SerializeField]
        private CinemachineCamera fpCamera;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private Animator characterAnimator;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private DeathCamera deathCamera;
        [SerializeField] private NetworkDamageRelay damageRelay;
        [FormerlySerializedAs("soundRelay")] [SerializeField] private NetworkSfxRelay sfxRelay;

        #endregion

        #region Public Input Fields

        public Vector2 moveInput;
        public Vector2 lookInput;
        public bool sprintInput;
        public bool crouchInput;

        #endregion

        #region Private Fields

        private float _currentPitch;
        private float _maxSpeed = WalkSpeed;
        private float _verticalVelocity;
        private Vector3 _horizontalVelocity;
        private bool _isJumping;
        private bool _isFalling;
        private bool _wasFalling;
        private float _crouchTransition;
        private CinemachineImpulseSource _impulseSource;
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitNormal;
        private LayerMask _playerBodyLayer;
        private LayerMask _maskedLayer;

        #endregion

        #region Private Properties

        public Vector3 CurrentFullVelocity => new(_horizontalVelocity.x, _verticalVelocity, _horizontalVelocity.z);
        private bool IsGrounded => characterController.isGrounded;

        private float CurrentPitch {
            get => _currentPitch;

            set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
        }

        #endregion

        public NetworkVariable<float> netHealth = new(100f);
        public NetworkVariable<bool> netIsDead = new();
        [SerializeField] private Material[] playerMaterials;
        public NetworkVariable<int> playerMaterialIndex = new();

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _impulseSource = FindFirstObjectByType<CinemachineImpulseSource>();
            _playerBodyLayer = LayerMask.GetMask("Player");
            _maskedLayer = LayerMask.GetMask("Masked");

            netHealth.OnValueChanged += (oldV, newV) => {
                if(IsOwner) {
                    HUDManager.Instance.UpdateHealth(newV, 100f);
                    _impulseSource?.GenerateImpulse();
                }

                if(IsServer && newV <= 0f && !netIsDead.Value) {
                    DieServer();
                }
            };

            if(!IsServer) {
                ApplyPlayerMaterial(playerMaterialIndex.Value);
            }

            if(characterController.enabled == false) {
                characterController.enabled = true;
            }

            if(!IsOwner) {
                gameObject.layer = LayerMask.NameToLayer("Enemy");
            }

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.Flex;
            }

            HUDManager.Instance.ShowHUD();
        }

        private void Update() {
            if(IsServer) {
                if(transform.position.y <= 600f) {
                    netHealth.Value = 0f;
                    DieServer();
                }
            }

            if(!IsOwner || netIsDead.Value) return;

            HandleLanding();
            HandleMovement();
            HandleCrouch();
            UpdateAnimator();
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            HandleLook();
        }

        #endregion

        private void ApplyPlayerMaterial(int index) {
            var mesh = GetComponentInChildren<SkinnedMeshRenderer>();
            if(mesh == null) return;

            var materials = mesh.materials;
            materials[0] = playerMaterials[index % playerMaterials.Length];
            mesh.materials = materials;
        }

        #region Public Methods

        public void TryJump(float height = JumpHeight) {
            if(!IsGrounded) {
                return;
            }

            height = CheckForJumpPad() ? 15f : height;

            if(sfxRelay != null && IsOwner) {
                var key = Mathf.Approximately(height, 15f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    sfxRelay?.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                } else {
                    sfxRelay?.RequestWorldSfx(SfxKey.Jump, attachToSelf: true);
                }
            }

            _verticalVelocity = Mathf.Sqrt(height * -2f * Physics.gravity.y * GravityScale);
            characterAnimator.SetTrigger(JumpTriggerHash);
            characterAnimator.SetBool(IsJumpingHash, true);
            _isJumping = true;
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(sfxRelay != null && IsOwner) {
                sfxRelay?.RequestWorldSfx(SfxKey.Walk, attachToSelf: true);
            }
        }

        public void PlayRunSound() {
            if(!IsGrounded) return;

            if(sfxRelay != null && IsOwner) {
                sfxRelay?.RequestWorldSfx(SfxKey.Run, attachToSelf: true);
            }
        }

        #endregion

        #region Movement Methods

        private void OnControllerColliderHit(ControllerColliderHit hit) {
            if(hit.gameObject.CompareTag("JumpPad")) {
                TryJump(15f);
            }

            grappleController.CancelGrapple();
        }

        private bool CheckForJumpPad() {
            // Raycast downward to check for jump pad
            if(Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit,
                   characterController.height * 0.6f)) {
                if(hit.collider.CompareTag("JumpPad")) {
                    return true;
                }
            }

            return false;
        }

        private void HandleMovement() {
            UpdateMaxSpeed();
            CalculateHorizontalVelocity();
            CheckCeilingHit();
            ApplyGravity();
            MoveCharacter();
        }

        private void UpdateMaxSpeed() {
            if(crouchInput) {
                _maxSpeed = CrouchSpeed;
            } else if(sprintInput) {
                _maxSpeed = SprintSpeed;
            } else {
                _maxSpeed = WalkSpeed;
            }
        }

        private void CalculateHorizontalVelocity() {
            var motion = (transform.forward * moveInput.y + transform.right * moveInput.x).normalized;
            motion.y = 0f;

            if(IsGrounded) {
                ApplyFriction();
                ApplyDirectionChange(motion);

                var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * _maxSpeed : Vector3.zero;
                _horizontalVelocity =
                    Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);
            } else {
                AirStrafe(motion);
            }
        }

        private void ApplyFriction() {
            if(!(moveInput.sqrMagnitude < 0.01f)) return;

            var horizontalSpeed = _horizontalVelocity.magnitude;

            if(!(horizontalSpeed > 0.1f)) return;

            var drop = horizontalSpeed * friction * Time.deltaTime;
            _horizontalVelocity *= Mathf.Max(horizontalSpeed - drop, 0f) / horizontalSpeed;
        }

        private void ApplyDirectionChange(Vector3 motion) {
            if(!(_horizontalVelocity.magnitude > 0.1f) || !(motion.magnitude > 0.1f)) return;

            var angle = Vector3.Angle(_horizontalVelocity, motion);

            if(!(angle > 90f)) return;

            var normalizedAngle = Mathf.InverseLerp(90f, 180f, angle);
            var reduction = Mathf.Lerp(0.85f, 0.2f, normalizedAngle * normalizedAngle);
            _horizontalVelocity *= reduction;
        }

        private void AirStrafe(Vector3 wishDir) {
            if(moveInput.sqrMagnitude < 0.01f) return;

            var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
            var addSpeed = maxAirSpeed - currentSpeed;

            if(addSpeed <= 0) return;

            var accelSpeed = airAcceleration * Time.deltaTime;
            accelSpeed = Mathf.Min(accelSpeed, addSpeed);

            _horizontalVelocity += wishDir * accelSpeed;
        }

        private void CheckCeilingHit() {
            Debug.DrawRay(fpCamera.transform.position, 0.75f * Vector3.up, Color.yellow);

            var mask = _playerBodyLayer | _maskedLayer;
            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out var hit, 0.75f, ~mask);
            if(rayHit && _verticalVelocity > 0f) {
                grappleController.CancelGrapple();
                _verticalVelocity = 0f;
            }
        }

        private void ApplyGravity() {
            if(IsGrounded && _verticalVelocity <= 0.01f) {
                _verticalVelocity = -3f;
            } else {
                _verticalVelocity += Physics.gravity.y * GravityScale * Time.deltaTime;
            }
        }

        private void MoveCharacter() {
            var fullVelocity = new Vector3(_horizontalVelocity.x, _verticalVelocity, _horizontalVelocity.z);
            characterController.Move(fullVelocity * Time.deltaTime);
        }

        private void HandleCrouch() {
            if(Physics.Raycast(fpCamera.transform.position, Vector3.up, StandCheckHeight) && !crouchInput) return;

            characterAnimator.SetBool(IsCrouchingHash, crouchInput);

            var targetTransition = crouchInput ? 1f : 0f;
            _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);

            var targetCameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);
            var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);

            fpCamera.transform.localPosition = new Vector3(0f, targetCameraHeight, 0f);

            // Adjust both height and center together
            var centerY = targetColliderHeight / 2f;
            characterController.height = targetColliderHeight;
            characterController.center = new Vector3(0f, centerY, 0f);
        }

        #endregion

        #region Look Methods

        private void HandleLook() {
            var lookDelta = new Vector2(lookInput.x * lookSensitivity.x, lookInput.y * lookSensitivity.y);

            UpdatePitch(lookDelta.y);
            UpdateYaw(lookDelta.x);
            UpdateTurnAnimation(lookDelta.x);
        }

        private void UpdatePitch(float pitchDelta) {
            CurrentPitch -= pitchDelta;
            fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, 0f);
        }

        private void UpdateYaw(float yawDelta) {
            transform.Rotate(Vector3.up * yawDelta);
        }

        #endregion

        #region Gameplay Methods

        public void ApplyDamageServer(float amount, Vector3 hitPoint, Vector3 hitNormal) {
            if(!IsServer) return;
            if(netIsDead.Value) return;

            _lastHitPoint = hitPoint;
            _lastHitNormal = hitNormal;

            netHealth.Value = Mathf.Max(0f, netHealth.Value - amount);

            if(netHealth.Value <= 0f && !netIsDead.Value) {
                DieServer();
            }
        }

        private void DieServer() {
            if(!IsServer) return;

            netIsDead.Value = true;

            // Tell everyone to show death visuals; the owner will also switch cameras.
            DieClientRpc(_lastHitPoint ?? transform.position, _lastHitNormal ?? Vector3.up);
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(Vector3 hitPoint, Vector3 hitNormal) {
            // Show ragdoll for everyone; only the owner swaps to death camera/HUD.
            if(playerRagdoll != null)
                playerRagdoll.EnableRagdoll(hitPoint, -hitNormal);

            if(IsOwner) {
                if(weaponManager != null) {
                    fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(false);
                }

                HUDManager.Instance.HideHUD();
                deathCamera.EnableDeathCamera();
            }

            StartCoroutine(RespawnTimer());
        }

        private IEnumerator RespawnTimer() {
            yield return new WaitForSeconds(3f);

            RequestRespawnServerRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestRespawnServerRpc() {
            if(!IsServer) return;
            if(!netIsDead.Value) return; // avoid respawning a living player

            DoRespawnServer();
        }

        private void DoRespawnServer() {
            // 1) reset networked state
            netIsDead.Value = false;
            netHealth.Value = 100f;

            // 2) choose spawn
            var position = SpawnManager.Instance.GetNextSpawnPosition();
            var rotation = SpawnManager.Instance.GetNextSpawnRotation();

            // 3) tell OWNER (the authoritative side) to teleport
            TeleportOwnerClientRpc(position, rotation);

            // 4) clear death visuals for everyone; owner will also restore UI/camera
            RespawnVisualsClientRpc();
        }

        [Rpc(SendTo.Owner)]
        private void TeleportOwnerClientRpc(Vector3 spawn, Quaternion rotation) {
            // OWNER runs this; it has authority if you’re using ClientNetworkTransform
            if(characterController) characterController.enabled = false;

            var cnt = GetComponent<ClientNetworkTransform>();
            if(cnt != null) {
                // Prefer Teleport; falls back to direct set if missing
                cnt.Teleport(spawn, rotation, Vector3.one);
            } else {
                // If not using CNT, the owner can still move it and authority will sync
                transform.SetPositionAndRotation(spawn, rotation);
            }

            if(characterController) characterController.enabled = true;
        }

        [Rpc(SendTo.Everyone)]
        private void RespawnVisualsClientRpc() {
            // Clear ragdoll etc. for all
            playerRagdoll.DisableRagdoll();

            // 2) make sure all renderers are visible again (in case any were toggled)
            // TODO: improve detection of GrappleLine to avoid hardcoding name
            foreach(var r in GetComponentsInChildren<Renderer>(true)) {
                if(r.name != "GrappleLine") {
                    r.enabled = true;
                }
            }

            foreach(var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true)) {
                if(IsOwner) {
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                } else {
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
            }

            // 3) restore expected layer (owner vs others)
            gameObject.layer = IsOwner
                ? LayerMask.NameToLayer("Player")
                : LayerMask.NameToLayer("Enemy");

            // Owner-only UI/camera resets
            if(GetComponent<NetworkObject>().IsOwner) {
                deathCamera.DisableDeathCamera();
                var weapons = GetComponentsInChildren<Weapons.Weapon>();
                foreach(var w in weapons) {
                    w.ResetWeapon();
                    GetComponent<PlayerInput>().SwitchWeapon(0);
                }

                // Re-enable current weapon viewmodel
                if(weaponManager != null) {
                    fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(true);
                }

                CurrentPitch = 0f;
                fpCamera.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);
                lookInput = Vector2.zero;
                moveInput = Vector2.zero;

                HUDManager.Instance.UpdateHealth(netHealth.Value, 100f);
                HUDManager.Instance.UpdateAmmo(weaponManager.CurrentWeapon.currentAmmo,
                    weaponManager.CurrentWeapon.magSize);
                HUDManager.Instance.ShowHUD();
            }
        }

        #endregion

        #region Private Methods - Animation

        private void HandleLanding() {
            // Landing from a jump
            if(_isJumping && IsGrounded && _verticalVelocity <= 0f) {
                _isJumping = false;
                characterAnimator.SetBool(IsJumpingHash, false);
                characterAnimator.SetTrigger(LandTriggerHash);

                if(sfxRelay != null && IsOwner) {
                    sfxRelay?.RequestWorldSfx(SfxKey.Land, attachToSelf: true);
                }
            }

            // Landing from a fall
            if(!_isFalling || !IsGrounded) return;
            characterAnimator.SetTrigger(LandTriggerHash);

            if(sfxRelay != null && IsOwner) {
                sfxRelay?.RequestWorldSfx(SfxKey.Land, attachToSelf: true);
            }
        }

        private void UpdateAnimator() {
            var localVelocity = transform.InverseTransformDirection(_horizontalVelocity);
            var isSprinting = _horizontalVelocity.magnitude > (WalkSpeed + 1f);

            Physics.Raycast(transform.position, Vector3.down, out var hit, 1f);
            Debug.DrawRay(transform.position, Vector3.down * 1f, Color.red);
            _isFalling = !IsGrounded && hit.distance > 3f;

            characterAnimator.SetFloat(MoveXHash, localVelocity.x / _maxSpeed, 0.1f, Time.deltaTime);
            characterAnimator.SetFloat(MoveYHash, localVelocity.z / _maxSpeed, 0.1f, Time.deltaTime);
            characterAnimator.SetBool(IsSprintingHash, isSprinting);
            characterAnimator.SetBool(IsFallingHash, _isFalling);
        }

        private void UpdateTurnAnimation(float yawDelta) {
            var turnSpeed = Mathf.Abs(yawDelta) > 0.001f ? Mathf.Clamp(yawDelta * 10f, -1f, 1f) : 0f;

            characterAnimator.SetFloat(LookXHash, turnSpeed, 0.1f, Time.deltaTime);
        }

        #endregion

        #region Grapple Support Methods

        public void SetVelocity(Vector3 horizontalVelocity) {
            _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
        }

        public void AddVerticalVelocity(float verticalBoost) {
            _verticalVelocity += verticalBoost;
        }

        #endregion
    }
}