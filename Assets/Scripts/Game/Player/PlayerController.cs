using System.Collections;
using Cysharp.Threading.Tasks;
using Game.Weapons;
using Network;
using Network.Rpc;
using Network.Singletons;
using NUnit.Framework;
using Unity.Cinemachine;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Player {
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerController : NetworkBehaviour {
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
        private static readonly int DamageTriggerHash = Animator.StringToHash("DamageTrigger");
        private static readonly int IsJumpingHash = Animator.StringToHash("IsJumping");
        private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");
        private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");

        // Health Regeneration
        private const float RegenDelay = 3f; // Seconds after taking damage before regen starts
        private const float RegenRate = 10f; // Health per second
        private const float MaxHealth = 100f;

        #endregion

        #region Serialized Fields

        [Header("Movement Parameters")] [SerializeField]
        private float acceleration = 15f;

        [SerializeField] private float airAcceleration = 50f;
        [SerializeField] private float maxAirSpeed = 5f;
        [SerializeField] private float friction = 8f;

        [Header("Look Parameters")] [SerializeField]
        public Vector2 lookSensitivity;

        [Header("Components")] [SerializeField]
        private CinemachineCamera fpCamera;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private PlayerInput playerInput;
        [SerializeField] private Animator characterAnimator;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private GrappleController grappleController;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private DeathCamera deathCamera;
        [SerializeField] private NetworkDamageRelay damageRelay;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private AudioClip hurtSound;
        [SerializeField] private CinemachineImpulseSource impulseSource;
        [SerializeField] private MeshRenderer worldWeapon;
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private Transform tr;
        [SerializeField] private float fallTriggerDistance = 3f; // start fall anim if drop > this
        [SerializeField] private float maxProbeDistance = 6f;

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
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitNormal;

        // Health regeneration tracking
        private float _lastDamageTime;
        private bool _isRegenerating;

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

        public NetworkVariable<int> kills = new();
        public NetworkVariable<int> deaths = new();
        public NetworkVariable<int> assists = new();
        public NetworkVariable<float> damageDealt = new();
        public NetworkVariable<float> averageVelocity = new();
        public NetworkVariable<int> pingMs = new();

        public NetworkVariable<int> playerMaterialIndex = new(0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        public NetworkVariable<FixedString64Bytes> playerName = new("Player",
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Owner);

        private float _timer;
        private float _totalVelocitySampled;
        private int _velocitySampleCount;
        private float _velSampleAccum;
        private int _velSampleCount;
        private float _velSampleTimer;
        private int _playerMask;
        private int _obstacleMask;
        private float _fallProbeTimer;
        private Weapon[] _weapons;

        [SerializeField] private UpperBodyPitch upperBodyPitch;

        #region Unity Lifecycle

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            _obstacleMask = worldLayer | enemyLayer;
            _weapons = GetComponents<Weapon>();
            EnsureRefs();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            playerMaterialIndex.OnValueChanged += OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
            netHealth.OnValueChanged += OnHealthChanged;

            ApplyPlayerMaterial(playerMaterialIndex.Value);

            if(characterController.enabled == false)
                characterController.enabled = true;

            // Set others to enemy layer
            if(!IsOwner)
                gameObject.layer = LayerMask.NameToLayer("Enemy");

            var gameMenu = GameMenuManager.Instance;
            if(gameMenu && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                var rootContainer = root?.Q<VisualElement>("root-container");
                if(rootContainer != null)
                    rootContainer.style.display = DisplayStyle.Flex;
            }

            HUDManager.Instance.ShowHUD();

            lookSensitivity = new Vector2(PlayerPrefs.GetFloat("SensitivityX", 0.1f),
                PlayerPrefs.GetFloat("SensitivityY", 0.1f));

            if(IsOwner) {
                playerName.Value = PlayerPrefs.GetString("PlayerName", "Unknown Player");
                var savedColorIndex = PlayerPrefs.GetInt("PlayerColorIndex", 0);
                playerMaterialIndex.Value = savedColorIndex;
                GrappleUIManager.Instance.RegisterLocalPlayer(this);
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();

            playerMaterialIndex.OnValueChanged -= OnMatChanged;
            netHealth.OnValueChanged -= OnHealthChanged;
        }

        private void OnMatChanged(int _, int newIdx) => ApplyPlayerMaterial(newIdx);

        private void OnHealthChanged(float oldV, float newV) {
            if(IsOwner) HUDManager.Instance.UpdateHealth(newV, 100f);
        }

        private void EnsureRefs() {
            // Dev-only guards: blow up early so production code can drop null checks
            Assert.IsNotNull(fpCamera, "[PlayerController] fpCamera missing");
            Assert.IsNotNull(characterController, "[PlayerController] CharacterController missing");
            Assert.IsNotNull(characterAnimator, "[PlayerController] Animator missing");
            Assert.IsNotNull(weaponManager, "[PlayerController] WeaponManager missing");
            Assert.IsNotNull(grappleController, "[PlayerController] GrappleController missing");
            Assert.IsNotNull(playerRagdoll, "[PlayerController] PlayerRagdoll missing");
            Assert.IsNotNull(deathCamera, "[PlayerController] DeathCamera missing");
            Assert.IsNotNull(damageRelay, "[PlayerController] NetworkDamageRelay missing");
            Assert.IsNotNull(sfxRelay, "[PlayerController] NetworkSfxRelay missing");
            Assert.IsNotNull(impulseSource, "[PlayerController] ImpulseSource missing");
            if(playerMaterials == null || playerMaterials.Length == 0)
                Debug.LogWarning("[PlayerController] playerMaterials not assigned");
        }

        private void Update() {
            if(IsServer) {
                if(tr.position.y <= 600f) {
                    netHealth.Value = 0f;
                    if(!netIsDead.Value) {
                        // _isDead = true;
                        BroadcastKillClientRpc("HOP", playerName.Value.ToString(), ulong.MaxValue);
                        DieServer();
                    }
                }

                HandleHealthRegeneration();

                _timer += Time.deltaTime;
                if(_timer >= 1f) {
                    _timer = 0f;
                    UpdatePing();
                }
            }

            if(!IsOwner || netIsDead.Value || characterController.enabled == false) return;

            HandleLanding();
            HandleMovement();
            HandleCrouch();
            UpdateAnimator();
            TrackVelocity();
        }

        private void LateUpdate() {
            if(!IsOwner || netIsDead.Value) return;

            HandleLook();

            upperBodyPitch.SetLocalPitchFromCamera(CurrentPitch);
        }

        #endregion

        private void UpdatePing() {
            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            if(!transport) return;

            var rtt = transport.GetCurrentRtt(OwnerClientId);

            pingMs.Value = (int)rtt;
        }

        private void HandleHealthRegeneration() {
            // Don't regen if dead or already at max health
            if(netIsDead.Value || netHealth.Value >= MaxHealth) {
                _isRegenerating = false;
                return;
            }

            var timeSinceDamage = Time.time - _lastDamageTime;

            // Check if enough time has passed to start regenerating
            if(timeSinceDamage >= RegenDelay) {
                if(!_isRegenerating) {
                    _isRegenerating = true;
                    // Optional: Play regen start sound/VFX here
                    // StartRegenClientRpc();
                }

                // Regenerate health
                netHealth.Value = Mathf.Min(MaxHealth, netHealth.Value + RegenRate * Time.deltaTime);
            } else {
                _isRegenerating = false;
            }
        }

        private void TrackVelocity() {
            var speed = CurrentFullVelocity.sqrMagnitude;
            if(speed >= WalkSpeed * WalkSpeed) {
                _velSampleAccum += Mathf.Sqrt(speed);
                _velSampleCount++;
            }

            _velSampleTimer += Time.deltaTime;
            if(_velSampleTimer >= 0.1f && _velSampleCount > 0) { // 10 Hz
                var avg = _velSampleAccum / _velSampleCount;
                SubmitVelocitySampleServerRpc(avg);
                _velSampleTimer = 0f;
                _velSampleAccum = 0f;
                _velSampleCount = 0;
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        void SubmitVelocitySampleServerRpc(float speed) {
            _totalVelocitySampled += speed;
            _velocitySampleCount++;
            averageVelocity.Value = _totalVelocitySampled / _velocitySampleCount;
        }

        private void ApplyPlayerMaterial(int index) {
            var mesh = GetComponentInChildren<SkinnedMeshRenderer>();
            if(!mesh) return;

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

            if(IsOwner) {
                var key = Mathf.Approximately(height, 15f) ? "jumpPad" : "jump";

                if(key == "jumpPad") {
                    sfxRelay.RequestWorldSfx(SfxKey.JumpPad, attachToSelf: true, true);
                }

                sfxRelay.RequestWorldSfx(SfxKey.Jump, attachToSelf: true, true);
            }

            _verticalVelocity = Mathf.Sqrt(height * -2f * Physics.gravity.y * GravityScale);

            PlayJumpAnimationServerRpc();

            _isJumping = true;
        }

        [Rpc(SendTo.Everyone)]
        private void PlayJumpAnimationServerRpc() {
            characterAnimator.SetTrigger(JumpTriggerHash);
            characterAnimator.SetBool(IsJumpingHash, true);
        }

        public void PlayWalkSound() {
            if(!IsGrounded) return;

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Walk, attachToSelf: true, true);
            }
        }

        public void PlayRunSound() {
            if(!IsGrounded) return;

            if(IsOwner) {
                sfxRelay.RequestWorldSfx(SfxKey.Run, attachToSelf: true, true);
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
            if(Physics.Raycast(tr.position, Vector3.down, out var hit, characterController.height * 0.6f)) {
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
            var motion = (tr.forward * moveInput.y + tr.right * moveInput.x).normalized;
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
            // Only when no input
            if(moveInput.sqrMagnitude >= 0.01f) return;

            var speed = _horizontalVelocity.magnitude;

            // If we’re already basically stopped, do nothing (avoid div-by-zero)
            if(speed < 0.001f) return;

            var drop = speed * friction * Time.deltaTime;
            var newSpeed = Mathf.Max(speed - drop, 0f);

            // Scale the vector down proportionally
            _horizontalVelocity *= newSpeed / speed;
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
            var rayHit = Physics.Raycast(fpCamera.transform.position, Vector3.up, out var hit, 0.75f, _obstacleMask);
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
            if(Physics.Raycast(fpCamera.transform.position, Vector3.up, StandCheckHeight, _obstacleMask) &&
               !crouchInput) return;

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
            tr.Rotate(Vector3.up * yawDelta);
        }

        #endregion

        #region Gameplay Methods

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitNormal, ulong attackerId) {
            if(!IsServer || netIsDead.Value) return false;

            _lastHitPoint = hitPoint;
            _lastHitNormal = hitNormal;
            _lastDamageTime = Time.time;
            _isRegenerating = false;

            var pre = netHealth.Value;
            var newHp = Mathf.Max(0f, pre - amount);
            var actualDealt = pre - newHp;

            netHealth.Value = newHp;
            // characterAnimator.SetTrigger(DamageTriggerHash);
            
            PlayHitEffectsClientRpc();

            // Credit damage to attacker (server write)
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                if(attacker != null) {
                    attacker.damageDealt.Value += actualDealt;
                }
            }

            // Lethal?
            if(newHp <= 0f && !netIsDead.Value) {
                netIsDead.Value = true;

                // Award kill to attacker; death to victim
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                    var killer = killerClient.PlayerObject?.GetComponent<PlayerController>();
                    if(killer) {
                        killer.kills.Value++;
                        BroadcastKillClientRpc(killer.playerName.Value.ToString(), playerName.Value.ToString(),
                            attackerId);
                    }
                }

                deaths.Value++;

                // Show ragdoll/etc. on everyone
                DieClientRpc(_lastHitPoint ?? tr.position, _lastHitNormal ?? Vector3.up);
                return true;
            }

            return false;
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastKillClientRpc(string killerName, string victimName, ulong killerClientId) {
            // Every client shows the kill in their feed
            var isLocalKiller = NetworkManager.Singleton.LocalClientId == killerClientId;
            GameMenuManager.Instance.AddKillToFeed(killerName, victimName, isLocalKiller);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayHitEffectsClientRpc() {
            // This runs only on the client who owns this player (the victim)
            if(IsOwner) {
                SoundFXManager.Instance.PlayUISound(hurtSound);
                impulseSource.GenerateImpulse();
            }
            
            characterAnimator.SetTrigger(DamageTriggerHash);
        }

        private void DieServer() {
            if(!IsServer) return;

            netIsDead.Value = true;
            deaths.Value++;

            // Tell everyone to show death visuals; the owner will also switch cameras.
            DieClientRpc(_lastHitPoint ?? tr.position, _lastHitNormal ?? Vector3.up);
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(Vector3 hitPoint, Vector3 hitNormal) {
            // Show ragdoll for everyone; only the owner swaps to death camera/HUD.
            if(playerRagdoll)
                playerRagdoll.EnableRagdoll(hitPoint, -hitNormal);

            if(IsOwner) {
                if(weaponManager) {
                    fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(false);
                }

                HUDManager.Instance.HideHUD();
                deathCamera.EnableDeathCamera();
                worldWeapon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            StartCoroutine(RespawnTimer());
        }

        private IEnumerator RespawnTimer() {
            yield return new WaitForSeconds(3f);

            RequestRespawnServerRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestRespawnServerRpc() {
            if(!netIsDead.Value) return; // avoid respawning a living player

            DoRespawnServer();
        }

        private void DoRespawnServer() {
            // 1) reset networked state
            netIsDead.Value = false;
            netHealth.Value = 100f;

            // Reset damage timer on respawn
            _lastDamageTime = Time.time - RegenDelay; // Start with regen available
            _isRegenerating = false;
            
            // Reset animator state
            characterAnimator.Rebind();
            characterAnimator.Update(0f);

            // 2) choose spawn
            var position = SpawnManager.Instance.GetNextSpawnPosition();
            var rotation = SpawnManager.Instance.GetNextSpawnRotation();

            // 3) tell OWNER (the authoritative side) to teleport
            TeleportOwnerClientRpc(position, rotation);
            // TeleportOwnerClientRpc(position, rotation);

            // 4) clear death visuals for everyone; owner will also restore UI/camera
            // RespawnVisualsClientRpc();
        }

        [Rpc(SendTo.Owner)]
        private void TeleportOwnerClientRpc(Vector3 spawn, Quaternion rotation) {
            _ = TeleportAndNotifyAsync(spawn, rotation);
        }

        private async UniTaskVoid TeleportAndNotifyAsync(Vector3 spawn, Quaternion rotation) {
            // Hide visuals during teleport
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r.name != "GrappleLine") {
                    r.enabled = false;
                }
            }

            // Disable character controller
            if(characterController) characterController.enabled = false;

            // Teleport
            var cnt = GetComponent<ClientNetworkTransform>();
            if(cnt) {
                cnt.Teleport(spawn, rotation, Vector3.one);
            } else {
                tr.SetPositionAndRotation(spawn, rotation);
            }

            // Wait for network sync
            await UniTask.WaitForFixedUpdate();

            // Re-enable character controller
            if(characterController) characterController.enabled = true;

            // Tell server we're ready to show visuals
            if(IsOwner) {
                RespawnVisualsClientRpc();
            }
        }


        [Rpc(SendTo.Everyone)]
        private void RespawnVisualsClientRpc() {
            // Clear ragdoll etc. for all
            playerRagdoll.DisableRagdoll();

            // 2) make sure all renderers are visible again (in case any were toggled)
            // TODO: improve detection of GrappleLine to avoid hardcoding name
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach(var r in renderers) {
                if(r.name != "GrappleLine") {
                    r.enabled = true;
                }
            }

            var skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach(var smr in skinnedRenderers) {
                if(IsOwner) {
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                } else {
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
            }

            if(IsOwner)
                worldWeapon.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            // 3) restore expected layer (owner vs others)
            gameObject.layer = IsOwner
                ? LayerMask.NameToLayer("Player")
                : LayerMask.NameToLayer("Enemy");

            // Owner-only UI/camera resets
            if(IsOwner) {
                deathCamera.DisableDeathCamera();
                foreach(var w in _weapons) {
                    w.ResetWeapon();
                    w.weaponPrefab.transform.localPosition = w.spawnPosition;
                    w.weaponPrefab.transform.localEulerAngles = w.spawnRotation;
                }

                playerInput.SwitchWeapon(0);

                // Re-enable current weapon viewmodel
                if(weaponManager) {
                    fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(true);
                }

                var currentWeapon = weaponManager.CurrentWeapon;
                currentWeapon.CurrentDamageMultiplier = 1f;
                HUDManager.Instance.UpdateHealth(netHealth.Value, 100f);
                HUDManager.Instance.UpdateAmmo(currentWeapon.currentAmmo, currentWeapon.magSize);
                HUDManager.Instance.ShowHUD();

                CurrentPitch = 0f;
                _horizontalVelocity = Vector3.zero;
                _verticalVelocity = 0f;
                lookInput = Vector2.zero;
                fpCamera.transform.localRotation = Quaternion.identity;
            }
        }

        #endregion

        #region Private Methods - Animation

        private void HandleLanding() {
            // Landing from a jump
            if(_isJumping && IsGrounded && _verticalVelocity <= 0f) {
                _isJumping = false;
                _isFalling = false;

                if(IsOwner) {
                    PlayLandingAnimationServerRpc();
                    sfxRelay.RequestWorldSfx(SfxKey.Land, attachToSelf: true);
                }
            }

            // Landing from a fall
            if(!_isFalling || !IsGrounded) return;
            _isFalling = false;

            if(IsOwner) {
                PlayLandingAnimationServerRpc();
                sfxRelay.RequestWorldSfx(SfxKey.Land, attachToSelf: true);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void PlayLandingAnimationServerRpc() {
            // Everyone plays the animation simultaneously
            characterAnimator.SetTrigger(LandTriggerHash);
            characterAnimator.SetBool(IsJumpingHash, false);
            characterAnimator.SetBool(IsFallingHash, false);
            characterAnimator.SetBool(IsGroundedHash, IsGrounded);
            _isFalling = false;
            _isJumping = false;
        }

        void UpdateFallingState() {
            // CharacterController's "grounded" is authoritative for "actually touching"
            if(IsGrounded) {
                _isFalling = false;
                return;
            }

            // Feet origin: a little above the bottom of the capsule
            var feet = characterController.bounds.center
                       + Vector3.down * (characterController.height * 0.5f - characterController.radius + 0.02f);

            if(Physics.Raycast(feet, Vector3.down, out var hit, maxProbeDistance, _obstacleMask,
                   QueryTriggerInteraction.Ignore)) {
                // If the *immediate* drop under feet is big enough, treat as falling (walked off a ledge)
                _isFalling = hit.distance > fallTriggerDistance;
            } else {
                // Nothing below within probe => definitely falling
                _isFalling = true;
            }
        }

        private void UpdateAnimator() {
            var localVelocity = tr.InverseTransformDirection(_horizontalVelocity);
            var isSprinting = _horizontalVelocity.sqrMagnitude > (WalkSpeed + 1f) * (WalkSpeed + 1f);

            UpdateFallingState();

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