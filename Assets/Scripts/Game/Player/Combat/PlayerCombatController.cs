using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Events;
using Game.Match;
using Game.Player.Contracts;
using Game.Weapon.Contracts;
using Game.Weapon.Core;
using Game.Weapon.Manager;
using Network.Components;
using Network.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Combat {
    /// <summary>
    /// Handles health, damage, death, and respawn logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerCombatController : NetworkBehaviour {
        private bool HasCombatAuthority => NetworkAuthority.HasGlobalAuthority(this);

        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerCombatContext _playerContext;

        private PlayerTagController _tagController;
        private PlayerRagdoll _playerRagdoll;
        private DeathCameraController _deathCameraController;
        private WeaponManager _weaponManager;
        private CharacterController _characterController;
        private ClientNetworkTransform _clientNetworkTransform;
        private Transform _playerTransform;
        private GameObject _playerModelRoot;
        private Transform _worldWeaponSocket;
        private Animator _playerAnimator;
        private CinemachineCamera _fpCamera;
        private CinemachineImpulseSource _impulseSource;

        // Health constants
        private const float RegenDelay = 10f;
        private const float RegenRate = 10f;
        private const float MaxHealth = 100f;
        private const float AuthoritativeHealthShadowWindowSeconds = 0.25f;
        private const float OutOfBoundsKillYDefault = 600f;
        private const float OutOfBoundsRespawnYBuffer = 20f;
        private const int GunTagOobNonTaggedPenaltySeconds = 25;

        // Health state
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitDirection;
        private float _lastDamageTime;
        private string _lastBodyPartTag;
        private bool _isRegenerating;
        private bool _deathStatePending;
        private bool _hasAuthoritativeHealthShadow;
        private float _authoritativeHealthShadow;
        private bool _authoritativeDeadShadow;
        private float _authoritativeHealthShadowValidUntil;
        private Coroutine _respawnFadeCoroutine;
        private Coroutine _respawnTimeoutProbeCoroutine;

        private sealed class AssistInfo {
            internal ulong AttackerId;
            internal float LastDamageTime;
            internal float Damage;
        }

        private readonly Dictionary<ulong, List<AssistInfo>> _assistTrackers = new();
        private const float AssistTimeoutSeconds = 10f;
        private const float AssistMinDamage = 1f;

        private struct HealthStateAuthorityRequest {
            public float HealthValue { get; set; }
            public bool IsDead { get; set; }
            public bool IncrementDeaths { get; set; }
            public Vector3 HitPoint { get; set; }
            public Vector3 HitDirection { get; set; }
            public string BodyPartTag { get; set; }
        }

        // Network variables (from PlayerController)
        public NetworkVariable<float> netHealth;
        public NetworkVariable<bool> netIsDead;
        public NetworkVariable<int> deaths;
        
        // Progression
        private int _currentKillStreak;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                DevLog.LogError("[PlayerHealthController] IPlayerCombatContext not found!");
                enabled = false;
                return;
            }

            if(_playerTransform == null) _playerTransform = _playerContext.PlayerTransform;
            if(_tagController == null) _tagController = GetComponent<PlayerTagController>();
            if(_playerRagdoll == null) _playerRagdoll = GetComponent<PlayerRagdoll>();
            if(_deathCameraController == null) _deathCameraController = GetComponent<DeathCameraController>();
            if(_weaponManager == null) _weaponManager = _playerContext.WeaponManager;
            if(_characterController == null) _characterController = _playerContext.CharacterController;
            if(_clientNetworkTransform == null) _clientNetworkTransform = _playerContext.ClientNetworkTransform;
            if(_playerModelRoot == null) _playerModelRoot = _playerContext.PlayerModelRoot;
            if(_worldWeaponSocket == null) _worldWeaponSocket = _playerContext.WorldWeaponSocket;
            if(_playerAnimator == null) _playerAnimator = _playerContext.PlayerAnimator;
            if(_fpCamera == null) _fpCamera = _playerContext.FpCamera;
            if(_impulseSource == null) _impulseSource = _playerContext.ImpulseSource;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            RefreshStateBindings();
            SyncHealthShadowFromReplicated();
        }

        private void RefreshStateBindings() {
            if(_playerContext == null) {
                return;
            }

            netHealth = _playerContext.NetHealth;
            netIsDead = _playerContext.NetIsDead;
            deaths = _playerContext.Deaths;
        }

        /// <summary>Syncs authoritative health shadow from replicated network state.</summary>
        private void SyncHealthShadowFromReplicated() {
            if(netHealth == null || netIsDead == null) {
                return;
            }

            _authoritativeHealthShadow = Mathf.Clamp(netHealth.Value, 0f, MaxHealth);
            _authoritativeDeadShadow = netIsDead.Value;
            _hasAuthoritativeHealthShadow = true;
            _authoritativeHealthShadowValidUntil = Time.time;
        }

        private void CommitHealthShadow(float healthValue, bool isDead) {
            _authoritativeHealthShadow = Mathf.Clamp(healthValue, 0f, MaxHealth);
            _authoritativeDeadShadow = isDead;
            _hasAuthoritativeHealthShadow = true;
            _authoritativeHealthShadowValidUntil = Time.time + AuthoritativeHealthShadowWindowSeconds;
        }

        /// <summary>Resolves current authoritative health (from shadow or replicated).</summary>
        private float ResolveAuthoritativeHealth() {
            if(!_hasAuthoritativeHealthShadow || Time.time > _authoritativeHealthShadowValidUntil) {
                SyncHealthShadowFromReplicated();
            }

            return _hasAuthoritativeHealthShadow
                ? _authoritativeHealthShadow
                : Mathf.Clamp(netHealth != null ? netHealth.Value : MaxHealth, 0f, MaxHealth);
        }

        private bool ResolveAuthoritativeIsDead() {
            if(_hasAuthoritativeHealthShadow && _authoritativeDeadShadow) {
                return true;
            }

            return netIsDead is { Value: true };
        }

        /// <summary>
        /// Updates the player's health regeneration state based on time since last damage.
        /// </summary>
        public void UpdateHealthRegeneration() {
            RefreshStateBindings();
            if(!HasCombatAuthority) return;
            if(netIsDead == null || netHealth == null) return;

            if(netIsDead.Value || netHealth.Value >= MaxHealth) {
                _isRegenerating = false;
                return;
            }

            var timeSinceDamage = Time.time - _lastDamageTime;

            if(timeSinceDamage >= RegenDelay) {
                if(!_isRegenerating) {
                    _isRegenerating = true;
                }

                netHealth.Value = Mathf.Min(MaxHealth, netHealth.Value + RegenRate * Time.deltaTime);
            } else {
                _isRegenerating = false;
            }
        }

        /// <summary>
        /// Applies damage to the player on the server (authoritative).
        /// </summary>
        public bool ApplyDamageServer_Auth(in DamageApplicationRequest request) {
            var amount = request.Damage;
            var hitPoint = request.HitPoint;
            var hitDirection = request.HitDirection;
            var attackerId = request.AttackerClientId;
            var bodyPartTag = request.BodyPartTag;
            var isHeadshot = request.IsHeadshot;
            var weaponId = request.WeaponId;

            RefreshStateBindings();
            if(!HasCombatAuthority || netIsDead == null || _deathStatePending) return false;
            if(ResolveAuthoritativeIsDead()) return false;
            var activeMode = _playerContext != null && !string.IsNullOrEmpty(_playerContext.CurrentGameModeId)
                ? _playerContext.CurrentGameModeId
                : "Unknown";

            if(attackerId == ulong.MaxValue) {
                var isOobTagMode = _playerContext is { IsGunTagMode: true };
                if(isOobTagMode && _tagController != null && !_tagController.IsTagged.Value) {
                    _tagController.ApplyTimeTaggedDeltaAuthority(GunTagOobNonTaggedPenaltySeconds);
                }

                var healthBefore = ResolveAuthoritativeHealth();
                ApplyHealthStateAuthority(new HealthStateAuthorityRequest {
                    HealthValue = 0f,
                    IsDead = true,
                    IncrementDeaths = true,
                    HitPoint = hitPoint,
                    HitDirection = hitDirection,
                    BodyPartTag = bodyPartTag
                });
                CommitHealthShadow(0f, true);
                _deathStatePending = true;
                StartRespawnTimeoutProbe();
                FlowLog.Emit(FlowEventIds.PlayerLethal,
                    ("victim", OwnerClientId),
                    ("attacker", "Environment"),
                    ("healthBefore", healthBefore),
                    ("healthAfter", 0f),
                    ("bodyPart", bodyPartTag ?? "None"));
                FlowLog.Emit(FlowEventIds.PlayerDeathEntered,
                    ("player", OwnerClientId),
                    ("hasAuthority", HasCombatAuthority),
                    ("isOwner", IsOwner),
                    ("mode", activeMode),
                    ("position", _playerTransform != null ? _playerTransform.position : transform.position));
                
                TryForceHopballDrop("OutOfBoundsDeath");

                if(_playerContext is { PlayerName: not null }) {
                }
                BroadcastKillClientRpc("HOP", attackerId, OwnerClientId, null);
                
                ReserveSpawnPointForDeath();
                DieClientRpc(_lastBodyPartTag);
                
                EventBus.Publish(new PlayerDiedEvent(OwnerClientId, attackerId, bodyPartTag));
                return true;
            }

            // Check if we're in Tag mode
            var isTagMode = _playerContext is { IsGunTagMode: true };

            _lastHitPoint = hitPoint;
            _lastHitDirection = hitDirection;
            _lastDamageTime = Time.time;
            _isRegenerating = false;
            _lastBodyPartTag = bodyPartTag; // Store for ragdoll force application


            if(isTagMode) {
                var nonTaggedShootingTagged = false;
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    if(attackerClient.PlayerObject == null) return false;
                    var attackerTagController = attackerClient.PlayerObject.GetComponent<PlayerTagController>();

                    if(attackerTagController != null && !attackerTagController.IsTagged.Value && 
                       _tagController != null && _tagController.IsTagged.Value) {
                        nonTaggedShootingTagged = true;
                        if(attackerTagController.TimeTagged.Value > 0) {
                            attackerTagController.ApplyTimeTaggedDeltaAuthority(-1);
                        }

                        if(_playerContext != null) {
                            _playerContext.PlayHitEffects(hitPoint, amount);
                        }
                    }
                }

                // Tag mode: delegate to PlayerTagController (only if attacker is tagged)
                if(_tagController != null && !nonTaggedShootingTagged) {
                    _tagController.HandleTagTransfer(attackerId, hitPoint, amount);
                }
                // No kill in tag mode (except OOB)
            } else {
                // Normal damage mode
                var pre = ResolveAuthoritativeHealth();
                var newHp = Mathf.Max(0f, pre - amount);
                var actualDealt = pre - newHp;
                var isLethalHit = newHp <= 0f;

                ApplyHealthStateAuthority(new HealthStateAuthorityRequest {
                    HealthValue = newHp,
                    IsDead = isLethalHit,
                    IncrementDeaths = isLethalHit,
                    HitPoint = hitPoint,
                    HitDirection = hitDirection,
                    BodyPartTag = bodyPartTag
                });
                CommitHealthShadow(newHp, isLethalHit);

                if(_playerContext != null) {
                    _playerContext.PlayHitEffects(hitPoint, amount);
                }

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    if(attackerClient.PlayerObject == null) return false;
                    if(attackerClient.PlayerObject.TryGetComponent<PlayerCombatController>(out var attackerHealthController)) {
                        attackerHealthController.AddDamageDealtAuthority(actualDealt);
                    }
                }

                TrackAssistDamage(attackerId, actualDealt);

                var isPostMatchFlowStarted = _playerContext is { IsPostMatchFlowStarted: true };
                if(!isLethalHit || isPostMatchFlowStarted)
                    return false;
                _deathStatePending = true;
                StartRespawnTimeoutProbe();
                FlowLog.Emit(FlowEventIds.PlayerLethal,
                    ("victim", OwnerClientId),
                    ("attacker", attackerId),
                    ("healthBefore", pre),
                    ("healthAfter", newHp),
                    ("bodyPart", _lastBodyPartTag ?? "None"));
                FlowLog.Emit(FlowEventIds.PlayerDeathEntered,
                    ("player", OwnerClientId),
                    ("hasAuthority", HasCombatAuthority),
                    ("isOwner", IsOwner),
                    ("mode", activeMode),
                    ("position", _playerTransform != null ? _playerTransform.position : transform.position));

                TryForceHopballDrop("PlayerDeath");

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                    if(killerClient.PlayerObject == null) return false;
                    var killerName = "Player";
                    if(killerClient.PlayerObject.TryGetComponent<PlayerCombatController>(out var killerHealthController)) {
                        killerHealthController.AddKillAuthority();
                        if(killerHealthController._playerContext is { PlayerName: not null }) {
                            killerName = killerHealthController._playerContext.PlayerName.Value.ToString();
                        }
                    }
                    AwardAssists(attackerId);
                    BroadcastKillClientRpc(killerName, attackerId, OwnerClientId, weaponId);
                }

                // Reserve spawn point immediately when player dies (server-side)
                ReserveSpawnPointForDeath();

                DieClientRpc(_lastBodyPartTag);
                
                // Publish death event
                EventBus.Publish(new PlayerDiedEvent(OwnerClientId, attackerId, _lastBodyPartTag));
                return true;
            }

            return false; // No kill in tag mode (except OOB)
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastKillClientRpc(string killerName, ulong killerClientId,
            ulong victimClientId, string weaponId) { // Added weaponId
            var isLocalKiller = NetworkManager.Singleton.LocalClientId == killerClientId;
            
            if (isLocalKiller) {
                if (killerClientId != victimClientId) {
                    _currentKillStreak++;

                    var killerSpeed = 0f;
                    var isGrounded = true;

                    if(_playerContext != null) {
                        killerSpeed = _playerContext.FullVelocity.magnitude;
                        isGrounded = _playerContext.IsGrounded;
                    } else if(_characterController != null) {
                        killerSpeed = _characterController.velocity.magnitude;
                        isGrounded = _characterController.isGrounded;
                    }

                    EventBus.Publish(new PlayerKillProgressionEvent(killerClientId, killerSpeed, isGrounded, weaponId,
                        _currentKillStreak, 100));
                }
            }
            
            if(NetworkManager.Singleton.LocalClientId != victimClientId) return;
            _currentKillStreak = 0;
            var isOob = killerName == "HOP";
            EventBus.Publish(new PlayerDeathProgressionEvent(victimClientId, isOob));

        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(string bodyPartTag = null) {
            if(_playerRagdoll != null) {
                if(_lastHitPoint.HasValue && _lastHitDirection.HasValue)
                    _playerRagdoll.EnableRagdoll(_lastHitPoint, _lastHitDirection, bodyPartTag);
                else
                    _playerRagdoll.EnableRagdoll();
            }

            _playerContext?.SetRenderersEnabled(true);

            if(IsOwner) {
                FlowLog.Emit(FlowEventIds.PlayerControlState,
                    ("player", OwnerClientId),
                    ("enabled", false),
                    ("reason", "DeathEntered"));
                if(_weaponManager != null) {
                    _playerContext?.SetWeaponCameraEnabled(false);
                }

                EventBus.Publish(new HideHUDEvent());
                if(_deathCameraController != null) {
                    _deathCameraController.EnableDeathCamera();
                }

                var wasHoldingHopball = _playerContext is { IsHoldingHopball: true };
                _playerContext?.ApplyDeathShadowState(wasHoldingHopball);

                if(IsOwner && _fpCamera != null) {
                    _fpCamera.Lens.FieldOfView = _playerContext != null ? _playerContext.BaseFov : 80f;
                }
            }

            StartCoroutine(RespawnTimer());
        }

        /// <summary>
        /// Reserves a spawn point for the player when they die.
        /// Called on server side to prevent race conditions.
        /// </summary>
        private void ReserveSpawnPointForDeath() {
            if(!HasCombatAuthority) return;
            _playerContext?.ReserveRespawnPoint();
        }

        private IEnumerator RespawnTimer() {
            yield return new WaitForSeconds(3f);

            if(HasCombatAuthority) {
                DoRespawnServer();
            } else {
                RequestRespawnAuthority();
            }
        }

        private void RequestRespawnAuthority() {
            if(netIsDead is not { Value: true }) return;
            if(WeaponCombatAuthority.Instance == null || NetworkObject == null || !NetworkObject.IsSpawned) return;

            WeaponCombatAuthority.Instance.RequestRespawnServerRpc(new NetworkObjectReference(NetworkObject));
        }

        public void ProcessRespawnRequest() {
            if(!HasCombatAuthority) return;
            if(netIsDead is not { Value: true }) return;
            DoRespawnServer();
        }

        /// <summary>
        /// Orchestrates the respawn sequence on the server.
        /// </summary>
        private void DoRespawnServer() {
            PrepareRespawnClientRpc();

            var isTeamBased = _playerContext is { IsTeamBasedMode: true };
            if(_playerContext == null || !_playerContext.TryGetReservedRespawnPose(out var position, out var rotation)) {
                if(_playerContext != null) {
                    _playerContext.GetFallbackRespawnPose(out position, out rotation);
                } else {
                    ResolveEmergencyRespawnPose(out position, out rotation);
                }
            }

            if(IsYLevelOutOfBoundsKillEnabled()) {
                var outOfBoundsKillY = GetOutOfBoundsKillY();
                if(position.y <= outOfBoundsKillY) {
                    _playerContext?.GetFallbackRespawnPose(out position, out rotation);

                    if(position.y <= outOfBoundsKillY) {
                        position.y = outOfBoundsKillY + OutOfBoundsRespawnYBuffer;
                    }
                }
            }
            FlowLog.Emit(FlowEventIds.PlayerRespawnStarted,
                ("player", OwnerClientId),
                ("spawnPoint", position),
                ("team", isTeamBased && _playerContext != null ? _playerContext.CurrentTeam.ToString() : "None"),
                ("wasRagdolled", _playerRagdoll != null && _playerRagdoll.IsRagdoll));

            StartCoroutine(TeleportAfterPreparation(position, rotation));
        }

        private void ResolveEmergencyRespawnPose(out Vector3 position, out Quaternion rotation) {
            position = _playerTransform != null ? _playerTransform.position : Vector3.zero;
            rotation = _playerTransform != null ? _playerTransform.rotation : Quaternion.identity;

            var spawnManager = SpawnManager.Instance;
            if(spawnManager == null) {
                return;
            }

            var fallbackSpawn = spawnManager.GetNextSpawnForRespawn();
            if(fallbackSpawn == null) {
                return;
            }

            var spawnTransform = fallbackSpawn.transform;
            position = spawnTransform.position;
            rotation = spawnTransform.rotation;
        }

        [Rpc(SendTo.Everyone)]
        private void PrepareRespawnClientRpc() {
            if(!IsOwner) return;
            if(_respawnFadeCoroutine != null) {
                StopCoroutine(_respawnFadeCoroutine);
            }

            EventBus.Publish(new RequestRespawnFadeTransitionEvent());
        }
        private IEnumerator TeleportAfterPreparation(Vector3 position, Quaternion rotation) {
            const float fadeDuration = 0.5f;
            const float buffer = 0.15f;
            const float outOfBoundsGraceAfterRespawnSeconds = 2f;

            yield return new WaitForSeconds(fadeDuration + buffer);

            if(HasCombatAuthority && _playerContext != null) {
                _playerContext.ReleaseRespawnReservation();
            }

            ResetRagdollClientRpc(position, rotation);

            const float holdDuration = 0.5f;
            yield return new WaitForSeconds(holdDuration);

            _playerContext?.SetOutOfBoundsGraceWindow(outOfBoundsGraceAfterRespawnSeconds);
            _deathStatePending = false;
            CommitHealthShadow(MaxHealth, false);
            ResetHealthAndRegenerationState();
            StopRespawnTimeoutProbe();
            var isDeadNow = netIsDead is { Value: true };
            var isRagdolledNow = _playerRagdoll != null && _playerRagdoll.IsRagdoll;
            FlowLog.Emit(FlowEventIds.PlayerRespawnCompleted,
                ("player", OwnerClientId),
                ("position", position),
                ("controlEnabled", true),
                ("isDead", isDeadNow),
                ("isRagdolled", isRagdolledNow));
            var outOfBoundsKillY = GetOutOfBoundsKillY();
            var isInBoundsForYLevel = !IsYLevelOutOfBoundsKillEnabled() || position.y > outOfBoundsKillY;
            if(isDeadNow || isRagdolledNow || !isInBoundsForYLevel) {
                FlowLog.Emit(FlowEventIds.AnomalyRespawnInvariant,
                    ("player", OwnerClientId),
                    ("isRagdoll", isRagdolledNow),
                    ("isInBounds", isInBoundsForYLevel),
                    ("position", position));
            }

            SignalFadeInStartClientRpc();
            RestoreControlClientRpc();
        }

        [Rpc(SendTo.Owner)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void SignalFadeInStartClientRpc() {
            EventBus.Publish(new RequestRespawnFadeInSignalEvent());
        }

        [Rpc(SendTo.Owner)]
        private void RestoreControlClientRpc() {
            if(_characterController != null) _characterController.enabled = true;
            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", true),
                ("reason", "RespawnComplete"));

            _playerContext?.ResetLookPitchFromRespawn();
            _playerContext?.ClearLookInput();

            _playerContext?.ResetVelocity();

            if(_fpCamera != null) {
                _fpCamera.transform.localRotation = Quaternion.identity;
            }

            EventBus.Publish(new ShowHUDEvent());

            ShowRespawnVisualsClientRpc(_playerTransform.position);

            var animator = _playerAnimator;
            if(animator != null) {
                animator.Rebind();
                animator.Update(0f);
            }

            if(_weaponManager != null) {
                _weaponManager.ApplyTpWeaponStateOnRespawn();
            }

            if(_playerContext == null) return;
            var sampledMove = _playerContext.ResampleHeldMovementInputFromRespawn("RespawnControlRestore");
            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", true),
                ("reason", "RespawnControlRestoreSampled"),
                ("sampledMove", sampledMove));
        }

        [Rpc(SendTo.Everyone)]
        private void ShowRespawnVisualsClientRpc(Vector3 expectedSpawnPosition) {
            if(IsOwner) {
                if(_weaponManager != null) {
                    _playerContext?.SetWeaponCameraEnabled(true);
                }

                if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                    _playerModelRoot.SetActive(true);
                }

                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                _playerContext?.InvalidateRendererCache();
                _playerContext?.ApplyOwnerDefaultShadowState();

                _playerContext?.ResetWeaponState(resetAllAmmo: true, switchToWeapon0: true, updateHUD: true);
            } else {
                StartCoroutine(ShowVisualsAfterPositionSync(expectedSpawnPosition));
            }

            if(_weaponManager != null) {
                _weaponManager.ApplyTpWeaponStateOnRespawn();
            }
        }

        [Rpc(SendTo.Everyone)]
        private void ResetRagdollClientRpc(Vector3 position, Quaternion rotation) {
            if(_playerRagdoll != null) {
                _playerRagdoll.DisableRagdoll();
            }

            _playerContext?.InvalidateRendererCache();

            HideVisuals();

            ResetAnimatorState(_playerAnimator);

            if(!IsOwner) return;
            if(_deathCameraController != null) {
                _deathCameraController.DisableDeathCamera();
            }

            TeleportOwnerClientRpc(position, rotation);
        }

        [Rpc(SendTo.Owner)]
        private void TeleportOwnerClientRpc(Vector3 spawn, Quaternion rotation) {
            _ = TeleportAndNotifyAsync(spawn, rotation);
        }

        private async UniTaskVoid TeleportAndNotifyAsync(Vector3 spawn, Quaternion rotation) {
            if(_characterController != null) _characterController.enabled = false;

            if(_clientNetworkTransform != null) {
                _clientNetworkTransform.Teleport(spawn, rotation, Vector3.one);
            } else {
                _playerTransform.SetPositionAndRotation(spawn, rotation);
            }

            // Track respawn time to prevent landing sounds on respawn
            _playerContext?.ResetSpawnAnimationTime();

            await UniTask.WaitForFixedUpdate();
            await UniTask.WaitForFixedUpdate();

            var currentPos = _playerTransform.position;
            var distanceMoved = Vector3.Distance(currentPos, spawn);
            if(distanceMoved > 0.1f) {
                await UniTask.Delay(50);
            }
        }

        private void HideVisuals() {
            _playerContext?.SetRenderersEnabled(false);
        }

        private IEnumerator ShowVisualsAfterPositionSync(Vector3 expectedPosition) {
            const int maxWaitFrames = 10;
            var framesWaited = 0;

            while(framesWaited < maxWaitFrames) {
                var distance = Vector3.Distance(_playerTransform.position, expectedPosition);
                if(distance < 5f) {
                    break;
                }

                framesWaited++;
                yield return null;
            }

            ShowVisuals();
        }

        /// <summary>
        /// Shows the player's visuals and shadow.
        /// </summary>
        private void ShowVisuals() {
            _playerContext?.InvalidateRendererCache();

            if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                _playerModelRoot.SetActive(true);
            }

            _playerContext?.SetRenderersEnabled(true);
            _playerContext?.ApplyVisibleShadowState();
            _playerContext?.ForceRendererBoundsUpdate();
        }

        /// <summary>
        /// Resets the player's health and regeneration state.
        /// </summary>
        public void ResetHealthAndRegenerationState() {
            RefreshStateBindings();
            if(!HasCombatAuthority) return;

            if(netIsDead != null) {
                netIsDead.Value = false;
            }

            if(netHealth != null) {
                netHealth.Value = 100f;
            }

            _lastDamageTime = Time.time - RegenDelay;
            _isRegenerating = false;
            _deathStatePending = false;

            // Tag mode: reset tagged state on respawn
            if(_tagController != null) {
                _tagController.ResetTagState();
            }
        }

        private void ApplyHealthStateAuthority(in HealthStateAuthorityRequest request) {
            if(netHealth != null) {
                netHealth.Value = Mathf.Clamp(request.HealthValue, 0f, MaxHealth);
            }

            if(netIsDead != null) {
                netIsDead.Value = request.IsDead;
            }

            if(request.IncrementDeaths && deaths != null) {
                deaths.Value++;
            }

            _lastHitPoint = request.HitPoint;
            _lastHitDirection = request.HitDirection;
            _lastDamageTime = Time.time;
            _isRegenerating = false;
            _lastBodyPartTag = string.IsNullOrEmpty(request.BodyPartTag) ? null : request.BodyPartTag;
            _deathStatePending = request.IsDead;
        }

        private void AddDamageDealtAuthority(float delta) {
            if(delta <= 0f || _playerContext?.DamageDealt == null) return;
            _playerContext.DamageDealt.Value += delta;
        }

        private void AddKillAuthority() {
            if(_playerContext?.Kills == null) return;
            _playerContext.Kills.Value++;
        }

        private void AddAssistAuthority() {
            if(_playerContext?.Assists == null) return;
            _playerContext.Assists.Value++;
        }

        private static void ResetAnimatorState(Animator animator) {
            if(animator == null) return;
            animator.Rebind();
            animator.Update(0f);
        }

        /// <summary>
        /// Gets the currently equipped world weapon GameObject from the weapon socket.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() {
            if(_weaponManager == null) return null;
            var worldWeapon = _weaponManager.CurrentWorldWeaponInstance;
            if(worldWeapon != null && worldWeapon.activeSelf) {
                return worldWeapon;
            }

            if(_worldWeaponSocket == null) return null;
            foreach(Transform child in _worldWeaponSocket) {
                if(child.gameObject.activeSelf) {
                    return child.gameObject;
                }
            }

            return null;
        }

        private void TrackAssistDamage(ulong attackerId, float damage) {
            if(attackerId == ulong.MaxValue || damage <= 0f || attackerId == OwnerClientId) return;

            if(!_assistTrackers.TryGetValue(OwnerClientId, out var assists)) {
                assists = new List<AssistInfo>();
                _assistTrackers[OwnerClientId] = assists;
            }

            var entry = assists.Find(a => a.AttackerId == attackerId);
            if(entry == null) {
                entry = new AssistInfo {
                    AttackerId = attackerId,
                    Damage = 0f,
                    LastDamageTime = Time.time
                };
                assists.Add(entry);
            }

            entry.Damage += damage;
            entry.LastDamageTime = Time.time;
        }

        private void AwardAssists(ulong killerId) {
            if(!_assistTrackers.TryGetValue(OwnerClientId, out var assists) || assists.Count == 0) return;

            var now = Time.time;
            foreach(var assist in assists) {
                if(assist.AttackerId == killerId) continue;
                if(now - assist.LastDamageTime > AssistTimeoutSeconds) continue;
                if(assist.Damage < AssistMinDamage) continue;
                if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(assist.AttackerId, out var client)) continue;
                if(client.PlayerObject == null) continue;
                if(client.PlayerObject.TryGetComponent<PlayerCombatController>(out var assistHealthController)) {
                    assistHealthController.AddAssistAuthority();
                }
            }

            assists.Clear();
        }

        private void TryForceHopballDrop(string reason) {
            FlowLog.Emit(FlowEventIds.HopballForcedDrop,
                ("player", OwnerClientId),
                ("reason", reason));
            EventBus.Publish(new PlayerHopballDeathDropRequestedEvent(OwnerClientId, reason));
        }

        private void StartRespawnTimeoutProbe() {
            if(!HasCombatAuthority) return;
            if(_respawnTimeoutProbeCoroutine != null) {
                StopCoroutine(_respawnTimeoutProbeCoroutine);
            }
            _respawnTimeoutProbeCoroutine = StartCoroutine(RespawnTimeoutProbeCoroutine());
        }

        private void StopRespawnTimeoutProbe() {
            if(_respawnTimeoutProbeCoroutine == null) return;
            StopCoroutine(_respawnTimeoutProbeCoroutine);
            _respawnTimeoutProbeCoroutine = null;
        }

        private IEnumerator RespawnTimeoutProbeCoroutine() {
            const float timeoutSeconds = 10f;
            yield return new WaitForSeconds(timeoutSeconds);
            _respawnTimeoutProbeCoroutine = null;
            if(!HasCombatAuthority || netIsDead == null || netIsDead.Value == false) yield break;
            FlowLog.Emit(FlowEventIds.AnomalyDeathRespawnTimeout,
                ("player", OwnerClientId),
                ("elapsed", timeoutSeconds),
                ("phase", "DeadAwaitingRespawn"));
        }

        private float GetOutOfBoundsKillY() {
            return _playerContext != null ? _playerContext.GetOutOfBoundsKillY() : OutOfBoundsKillYDefault;
        }

        private bool IsYLevelOutOfBoundsKillEnabled() {
            return _playerContext == null || _playerContext.IsYLevelOutOfBoundsKillEnabled();
        }
    }
}

