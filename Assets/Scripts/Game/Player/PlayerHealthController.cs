using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Weapons;
using Game.Hopball;
using Game.Match;
using Game.Spawning;
using Game.UI;
using Network.Components;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles health, damage, death, and respawn logic for the player.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerHealthController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private PlayerTagController _tagController;
        private PlayerVisualController _visualController;
        private PlayerAnimationController _animationController;
        private PlayerShadow _playerShadow;
        private PlayerRagdoll _playerRagdoll;
        private DeathCameraController _deathCameraController;
        private WeaponManager _weaponManager;
        private CharacterController _characterController;
        private ClientNetworkTransform _clientNetworkTransform;
        private Transform _playerTransform;
        private PlayerLookController _lookController;
        private PlayerMovementController _movementController;
        private PlayerTeamManager _teamManager;
        private GameObject _playerModelRoot;
        private Transform _worldWeaponSocket;
        private Animator _playerAnimator;
        private WeaponCameraController _weaponCameraController;
        private CinemachineCamera _fpCamera;
        private CinemachineImpulseSource _impulseSource;
        [SerializeField] private AudioClip hurtSound;

        // Health constants
        private const float RegenDelay = 10f;
        private const float RegenRate = 10f;
        private const float MaxHealth = 100f;
        private const float OutOfBoundsKillYDefault = 600f;
        private const float OutOfBoundsRespawnYBuffer = 20f;

        // Health state
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitDirection;
        private float _lastDamageTime;
        private string _lastBodyPartTag;
        private bool _isRegenerating;
        private Coroutine _respawnFadeCoroutine;
        private Coroutine _respawnTimeoutProbeCoroutine;

        // Spawn reservation
        private SpawnPoint _reservedSpawnPoint;

        private class AssistInfo {
            public ulong AttackerId;
            public float LastDamageTime;
            public float Damage;
        }

        private readonly Dictionary<ulong, List<AssistInfo>> _assistTrackers = new();
        private const float AssistTimeoutSeconds = 10f;
        private const float AssistMinDamage = 1f;

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
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerHealthController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_playerTransform == null) _playerTransform = playerController.PlayerTransform;
            if(_tagController == null) _tagController = playerController.TagController;
            if(_visualController == null) _visualController = playerController.VisualController;
            if(_animationController == null) _animationController = playerController.AnimationController;
            if(_playerShadow == null) _playerShadow = playerController.PlayerShadow;
            if(_playerRagdoll == null) _playerRagdoll = playerController.PlayerRagdoll;
            if(_deathCameraController == null) _deathCameraController = playerController.DeathCameraController;
            if(_weaponManager == null) _weaponManager = playerController.WeaponManager;
            if(_characterController == null) _characterController = playerController.CharacterController;
            if(_clientNetworkTransform == null) _clientNetworkTransform = playerController.ClientNetworkTransform;
            if(_lookController == null) _lookController = playerController.LookController;
            if(_movementController == null) _movementController = playerController.MovementController;
            if(_teamManager == null) _teamManager = playerController.TeamManager;
            if(_playerModelRoot == null) _playerModelRoot = playerController.PlayerModelRoot;
            if(_worldWeaponSocket == null) _worldWeaponSocket = playerController.WorldWeaponSocket;
            if(_playerAnimator == null) _playerAnimator = playerController.PlayerAnimator;
            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_impulseSource == null) _impulseSource = playerController.ImpulseSource;
            if(_weaponCameraController == null) _weaponCameraController = playerController.WeaponCameraController;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            // Get network variables from PlayerController (network-dependent)
            if(playerController == null) return;
            netHealth = playerController.NetHealth;
            netIsDead = playerController.NetIsDead;
            deaths = playerController.Deaths;
        }

        /// <summary>
        /// Updates the player's health regeneration state based on time since last damage.
        /// </summary>
        public void UpdateHealthRegeneration() {
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
        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false, string weaponId = null) {
            if(!IsServer || netIsDead == null || netIsDead.Value) return false;
            var activeMode = MatchSettingsManager.Instance != null
                ? MatchSettingsManager.Instance.selectedGameModeId
                : "Unknown";

            if(attackerId == ulong.MaxValue) {
                var healthBefore = netHealth.Value;
                netHealth.Value = 0f;
                netIsDead.Value = true;
                StartRespawnTimeoutProbe();
                FlowLog.Emit(FlowEventIds.PlayerLethal,
                    ("victim", OwnerClientId),
                    ("attacker", "Environment"),
                    ("healthBefore", healthBefore),
                    ("healthAfter", netHealth.Value),
                    ("bodyPart", bodyPartTag ?? "None"));
                FlowLog.Emit(FlowEventIds.PlayerDeathEntered,
                    ("player", OwnerClientId),
                    ("isServer", IsServer),
                    ("isOwner", IsOwner),
                    ("mode", activeMode),
                    ("position", _playerTransform != null ? _playerTransform.position : transform.position));
                
                _lastHitPoint = hitPoint;
                _lastHitDirection = hitDirection;
                _lastDamageTime = Time.time;
                _isRegenerating = false;
                _lastBodyPartTag = bodyPartTag;

                TryForceHopballDrop("OutOfBoundsDeath");

                var victimName = "Player";
                if(playerController != null && playerController.PlayerName != null) {
                    victimName = playerController.PlayerName.Value.ToString();
                }
                BroadcastKillClientRpc("HOP", victimName, attackerId, OwnerClientId, null);
                
                deaths.Value++;
                ReserveSpawnPointForDeath();
                DieClientRpc(_lastBodyPartTag);
                
                EventBus.Publish(new PlayerDiedEvent(OwnerClientId, attackerId, bodyPartTag));
                return true;
            }

            // Check if we're in Tag mode
            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            _lastHitPoint = hitPoint;
            _lastHitDirection = hitDirection;
            _lastDamageTime = Time.time;
            _isRegenerating = false;
            _lastBodyPartTag = bodyPartTag; // Store for ragdoll force application


            if(isTagMode) {
                var nonTaggedShootingTagged = false;
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    if(attackerClient.PlayerObject == null) return false;
                    var attacker = attackerClient.PlayerObject.GetComponent<PlayerController>();
                    if(attacker == null) return false;
                    var attackerTagController = attacker.GetComponent<PlayerTagController>();

                    if(attackerTagController != null && !attackerTagController.isTagged.Value && 
                       _tagController != null && _tagController.isTagged.Value) {
                        nonTaggedShootingTagged = true;
                        if(attackerTagController.timeTagged.Value > 0) {
                            attackerTagController.timeTagged.Value--;
                        }

                        if(playerController != null) {
                            playerController.PlayHitEffectsClientRpc(hitPoint, amount);
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
                var pre = netHealth.Value;
                var newHp = Mathf.Max(0f, pre - amount);
                var actualDealt = pre - newHp;

                netHealth.Value = newHp;

                // Publish damage event
                EventBus.Publish(new PlayerDamagedEvent(OwnerClientId, actualDealt, hitPoint));

                if(playerController != null) {
                    playerController.PlayHitEffectsClientRpc(hitPoint, amount);
                }

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    if(attackerClient.PlayerObject == null) return false;
                    var attacker = attackerClient.PlayerObject.GetComponent<PlayerController>();
                    if(attacker != null && attacker.damageDealt != null) {
                        attacker.damageDealt.Value += actualDealt;
                    }
                }

                TrackAssistDamage(attackerId, actualDealt);

                var isPostMatchFlowStarted = false;
                if(PostMatchManager.Instance != null) {
                    isPostMatchFlowStarted = PostMatchManager.Instance.PostMatchFlowStarted;
                }
                if(!(newHp <= 0f) || netIsDead.Value || isPostMatchFlowStarted)
                    return false;
                netIsDead.Value = true;
                StartRespawnTimeoutProbe();
                FlowLog.Emit(FlowEventIds.PlayerLethal,
                    ("victim", OwnerClientId),
                    ("attacker", attackerId),
                    ("healthBefore", pre),
                    ("healthAfter", newHp),
                    ("bodyPart", _lastBodyPartTag ?? "None"));
                FlowLog.Emit(FlowEventIds.PlayerDeathEntered,
                    ("player", OwnerClientId),
                    ("isServer", IsServer),
                    ("isOwner", IsOwner),
                    ("mode", activeMode),
                    ("position", _playerTransform != null ? _playerTransform.position : transform.position));

                TryForceHopballDrop("PlayerDeath");

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                    if(killerClient.PlayerObject == null) return false;
                    var killer = killerClient.PlayerObject.GetComponent<PlayerController>();
                    if(killer != null) {
                        killer.Kills.Value++;
                        AwardAssists(attackerId);
                        var killerName = killer.playerName != null ? killer.playerName.Value.ToString() : "Player";
                        var victimName = "Player";
                        if(playerController != null && playerController.PlayerName != null) {
                            victimName = playerController.PlayerName.Value.ToString();
                        }
                        BroadcastKillClientRpc(killerName, victimName, attackerId, OwnerClientId, weaponId);
                    }
                }

                deaths.Value++;

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
        private void BroadcastKillClientRpc(string killerName, string victimName, ulong killerClientId,
            ulong victimClientId, string weaponId) { // Added weaponId
            var isLocalKiller = NetworkManager.Singleton.LocalClientId == killerClientId;
            
            // Progression: Award XP for kills & Update Killstreak
            if (isLocalKiller) {
                if (killerClientId != victimClientId) {
                    // It's a kill
                    _currentKillStreak++;
                    if (Progression.ProgressionManager.Instance != null) {
                        Progression.ProgressionManager.Instance.AddXp(100);
                        
                        // Collect Kill Context
                        var killerSpeed = 0f;
                        var isGrounded = true;
                        
                        if(_movementController != null) {
                            killerSpeed = _movementController.FullVelocity.magnitude;
                            isGrounded = _movementController.IsGrounded;
                        } else if(_characterController != null) {
                             killerSpeed = _characterController.velocity.magnitude;
                             isGrounded = _characterController.isGrounded;
                        }
                        
                        Progression.ProgressionManager.Instance.RecordKill(killerSpeed, isGrounded, weaponId);
                        Progression.ProgressionManager.Instance.UpdateKillStreak(_currentKillStreak);
                    }
                }
            }
            
            // Progression: Reset streak on death
            if (NetworkManager.Singleton.LocalClientId == victimClientId) {
                 _currentKillStreak = 0;
                 // Record Death (Normal or OOB)
                 if (Progression.ProgressionManager.Instance != null) {
                     // Check if OOB (killer is "HOP")
                     var isOob = killerName == "HOP";
                     Progression.ProgressionManager.Instance.RecordDeath(isOob);
                 }
            }

            if(KillFeedManager.Instance != null) {
                EventBus.Publish(new AddKillFeedEntryEvent(killerName, victimName, isLocalKiller, killerClientId,
                    victimClientId, wasKill: true));
            }
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(string bodyPartTag = null) {
            if(_playerRagdoll != null) {
                if(_lastHitPoint.HasValue && _lastHitDirection.HasValue)
                    _playerRagdoll.EnableRagdoll(_lastHitPoint, _lastHitDirection, bodyPartTag);
                else
                    _playerRagdoll.EnableRagdoll();
            }

            if(_visualController != null) {
                _visualController.SetRenderersEnabled(true);
            }

            if(IsOwner) {
                FlowLog.Emit(FlowEventIds.PlayerControlState,
                    ("player", OwnerClientId),
                    ("enabled", false),
                    ("reason", "DeathEntered"));
                if(playerController != null && playerController.PlayerInput != null) {
                    playerController.PlayerInput.ForceDisableSniperOverlay(false);
                }
                if(_weaponManager != null && _weaponCameraController != null) {
                    _weaponCameraController.SetWeaponCameraEnabled(false);
                }

                if(HUDManager.Instance != null) {
                    EventBus.Publish(new HideHUDEvent());
                }
                if(_deathCameraController != null) {
                    _deathCameraController.EnableDeathCamera();
                }

                var wasHoldingHopball = false;
                if(playerController != null) {
                    var hopballController = playerController.PlayerHopballController;
                    if(hopballController != null) {
                        wasHoldingHopball = hopballController.IsHoldingHopball;
                    }
                }
                if(!wasHoldingHopball && HopballController.Instance != null && HopballController.Instance.IsEquipped &&
                   HopballController.Instance.HolderController != null &&
                   HopballController.Instance.HolderController.OwnerClientId == OwnerClientId) {
                    wasHoldingHopball = true;
                }
                if(_playerShadow != null) {
                    _playerShadow.ApplyDeathShadowState(wasHoldingHopball);
                }

                if(IsOwner && _fpCamera != null) {
                    var baseFov = 80f;
                    if(_lookController != null) {
                        baseFov = _lookController.BaseFov;
                    }
                    _fpCamera.Lens.FieldOfView = baseFov;
                }
            }

            StartCoroutine(RespawnTimer());
        }

        /// <summary>
        /// Reserves a spawn point for the player when they die.
        /// Called on server side to prevent race conditions.
        /// </summary>
        private void ReserveSpawnPointForDeath() {
            if(!IsServer) return;

            var matchSettings = MatchSettingsManager.Instance;
            var isTeamBased = matchSettings != null &&
                              MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

            SpawnPoint reservedPoint = null;
            if(isTeamBased) {
                var team = SpawnPoint.Team.TeamA;
                if(_teamManager != null && _teamManager.netTeam != null) {
                    team = _teamManager.netTeam.Value;
                }
                if(SpawnManager.Instance != null) {
                    reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(OwnerClientId, team);
                }
            } else {
                if(SpawnManager.Instance != null) {
                    reservedPoint = SpawnManager.Instance.ReserveSpawnPoint(OwnerClientId);
                }
            }

            _reservedSpawnPoint = reservedPoint;
        }

        private IEnumerator RespawnTimer() {
            yield return new WaitForSeconds(3f);

            RequestRespawnServerRpc();
        }

        [Rpc(SendTo.Server)]
        private void RequestRespawnServerRpc() {
            if(netIsDead is not { Value: true }) return;

            DoRespawnServer();
        }

        /// <summary>
        /// Orchestrates the respawn sequence on the server.
        /// </summary>
        private void DoRespawnServer() {
            PrepareRespawnClientRpc();

            Vector3 position;
            Quaternion rotation;
            var matchSettings = MatchSettingsManager.Instance;
            var isTeamBased = matchSettings != null &&
                              MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);
            var team = SpawnPoint.Team.TeamA;
            if(isTeamBased && _teamManager != null && _teamManager.netTeam != null) {
                team = _teamManager.netTeam.Value;
            }

            if(_reservedSpawnPoint != null) {
                var reservedSpawnTransform = _reservedSpawnPoint.transform;
                position = reservedSpawnTransform.position;
                rotation = reservedSpawnTransform.rotation;
            } else {
                if(isTeamBased) {
                    (position, rotation) = GetSpawnPointForTeam(team);
                } else {
                    (position, rotation) = GetSpawnPointFfa();
                }
            }

            if(IsYLevelOutOfBoundsKillEnabled()) {
                var outOfBoundsKillY = GetOutOfBoundsKillY();
                if(position.y <= outOfBoundsKillY) {
                    if(isTeamBased) {
                        (position, rotation) = GetSpawnPointForTeam(team);
                    } else {
                        (position, rotation) = GetSpawnPointFfa();
                    }

                    if(position.y <= outOfBoundsKillY) {
                        position.y = outOfBoundsKillY + OutOfBoundsRespawnYBuffer;
                    }
                }
            }
            FlowLog.Emit(FlowEventIds.PlayerRespawnStarted,
                ("player", OwnerClientId),
                ("spawnPoint", position),
                ("team", isTeamBased ? team.ToString() : "None"),
                ("wasRagdolled", _playerRagdoll != null && _playerRagdoll.IsRagdoll));

            StartCoroutine(TeleportAfterPreparation(position, rotation));
        }

        [Rpc(SendTo.Everyone)]
        private void PrepareRespawnClientRpc() {
            if(!IsOwner || SceneTransitionManager.Instance == null) return;
            if(_respawnFadeCoroutine != null) {
                StopCoroutine(_respawnFadeCoroutine);
            }

            if(SceneTransitionManager.Instance != null) {
                _respawnFadeCoroutine = StartCoroutine(SceneTransitionManager.Instance.FadeRespawnTransition());
            }
        }


        private static (Vector3 pos, Quaternion rot) GetSpawnPointForTeam(SpawnPoint.Team team) {
            SpawnPoint point = null;
            if(SpawnManager.Instance != null) {
                point = SpawnManager.Instance.GetNextSpawnPointForRespawn(team);
            }
            return point == null
                ? (Vector3.zero, Quaternion.identity)
                : (point.transform.position, point.transform.rotation);
        }

        private static (Vector3 pos, Quaternion rot) GetSpawnPointFfa() {
            SpawnPoint point = null;
            if(SpawnManager.Instance != null) {
                point = SpawnManager.Instance.GetNextSpawnPointForRespawn();
            }
            return point == null
                ? (Vector3.zero, Quaternion.identity)
                : (point.transform.position, point.transform.rotation);
        }

        private IEnumerator TeleportAfterPreparation(Vector3 position, Quaternion rotation) {
            const float fadeDuration = 0.5f;
            const float buffer = 0.15f;
            const float outOfBoundsGraceAfterRespawnSeconds = 2f;

            yield return new WaitForSeconds(fadeDuration + buffer);

            if(IsServer && _reservedSpawnPoint != null) {
                if(SpawnManager.Instance != null) {
                    SpawnManager.Instance.ReleaseReservation(OwnerClientId);
                }
                _reservedSpawnPoint = null;
            }

            DisableRagdollAndTeleportClientRpc(position, rotation);

            const float holdDuration = 0.5f;
            yield return new WaitForSeconds(holdDuration);

            if(playerController != null) {
                playerController.SetOutOfBoundsGraceWindow(outOfBoundsGraceAfterRespawnSeconds);
            }
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
            RestoreControlAfterFadeInClientRpc();
        }

        [Rpc(SendTo.Owner)]
        private void SignalFadeInStartClientRpc() {
            if(SceneTransitionManager.Instance != null) {
                SceneTransitionManager.Instance.SignalFadeInStart();
            }
        }

        [Rpc(SendTo.Owner)]
        private void RestoreControlAfterFadeInClientRpc() {
            if(_characterController != null) _characterController.enabled = true;
            FlowLog.Emit(FlowEventIds.PlayerControlState,
                ("player", OwnerClientId),
                ("enabled", true),
                ("reason", "RespawnComplete"));

            if(_lookController != null) {
                _lookController.ResetPitch();
            }

            if(playerController != null) {
                playerController.lookInput = Vector2.zero;
            }

            if(_movementController != null) {
                _movementController.ResetVelocity();
            }

            if(_fpCamera != null) {
                _fpCamera.transform.localRotation = Quaternion.identity;
            }

            if(HUDManager.Instance != null) {
                EventBus.Publish(new ShowHUDEvent());
            }

            EventBus.Publish(new PlayerRespawnedEvent(OwnerClientId));

            ShowRespawnVisualsClientRpc(_playerTransform.position);

            var animator = _playerAnimator;
            if(animator != null) {
                animator.Rebind();
                animator.Update(0f);
            }

            if(_weaponManager != null) {
                _weaponManager.ApplyTpWeaponStateOnRespawn();
            }

            if(playerController != null && playerController.PlayerInput != null) {
                var sampledMove = playerController.PlayerInput.ResampleHeldMovementInput("RespawnControlRestore");
                FlowLog.Emit(FlowEventIds.PlayerControlState,
                    ("player", OwnerClientId),
                    ("enabled", true),
                    ("reason", "RespawnControlRestoreSampled"),
                    ("sampledMove", sampledMove));
            }
        }

        [Rpc(SendTo.Everyone)]
        private void ShowRespawnVisualsClientRpc(Vector3 expectedSpawnPosition) {
            if(IsOwner) {
                if(_weaponManager != null && _weaponCameraController != null) {
                    _weaponCameraController.SetWeaponCameraEnabled(true);
                }

                if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                    _playerModelRoot.SetActive(true);
                }

                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                if(_visualController != null) {
                    _visualController.InvalidateRendererCache();
                }

                if(_playerShadow != null) {
                    _playerShadow.ApplyOwnerDefaultShadowState();
                }

                if(playerController != null) {
                    playerController.ResetWeaponState(resetAllAmmo: true, switchToWeapon0: true, updateHUD: true);
                }
            } else {
                StartCoroutine(ShowVisualsAfterPositionSync(expectedSpawnPosition));
            }

            if(_weaponManager != null) {
                _weaponManager.ApplyTpWeaponStateOnRespawn();
            }
        }

        [Rpc(SendTo.Everyone)]
        private void DisableRagdollAndTeleportClientRpc(Vector3 position, Quaternion rotation) {
            if(_playerRagdoll != null) {
                _playerRagdoll.DisableRagdoll();
            }

            if(_visualController != null) {
                _visualController.InvalidateRendererCache();
            }

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
            if(_animationController != null) {
                _animationController.ResetSpawnTime();
            }

            await UniTask.WaitForFixedUpdate();
            await UniTask.WaitForFixedUpdate();

            var currentPos = _playerTransform.position;
            var distanceMoved = Vector3.Distance(currentPos, spawn);
            if(distanceMoved > 0.1f) {
                await UniTask.Delay(50);
            }
        }

        private void HideVisuals() {
            if(_visualController != null) {
                _visualController.SetRenderersEnabled(false);
            }
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
            if(_visualController != null) {
                _visualController.InvalidateRendererCache();
            }

            if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                _playerModelRoot.SetActive(true);
            }

            if(_visualController != null) {
                _visualController.SetRenderersEnabled(true);
            }
            if(_playerShadow != null) {
                _playerShadow.ApplyVisibleShadowState();
            }

            if(_visualController != null) {
                _visualController.ForceRendererBoundsUpdate();
            }
        }

        /// <summary>
        /// Resets the player's health and regeneration state.
        /// </summary>
        public void ResetHealthAndRegenerationState() {
            if(netIsDead != null) {
                netIsDead.Value = false;
            }

            if(netHealth != null) {
                netHealth.Value = 100f;
            }

            _lastDamageTime = Time.time - RegenDelay;
            _isRegenerating = false;

            // Tag mode: reset tagged state on respawn
            if(_tagController != null) {
                _tagController.ResetTagState();
            }
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
                var controller = client.PlayerObject.GetComponent<PlayerController>();
                if(controller == null || controller.Assists == null) continue;
                controller.Assists.Value++;
            }

            assists.Clear();
        }

        private void TryForceHopballDrop(string reason) {
            if(HopballSpawnManager.Instance == null || HopballSpawnManager.Instance.CurrentHopballController == null) return;

            var hopball = HopballSpawnManager.Instance.CurrentHopballController;
            if(!hopball.IsEquipped || hopball.HolderController == null ||
               hopball.HolderController.OwnerClientId != OwnerClientId) {
                return;
            }

            if(playerController != null) {
                var hopballController = playerController.PlayerHopballController;
                if(hopballController != null) {
                    FlowLog.Emit(FlowEventIds.HopballForcedDrop,
                        ("player", OwnerClientId),
                        ("hopballNetId", hopball.NetworkObjectId),
                        ("reason", reason));
                    hopballController.DropHopballOnDeath();
                }
            }
        }

        private void StartRespawnTimeoutProbe() {
            if(!IsServer) return;
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
            if(!IsServer || netIsDead == null || netIsDead.Value == false) yield break;
            FlowLog.Emit(FlowEventIds.AnomalyDeathRespawnTimeout,
                ("player", OwnerClientId),
                ("elapsed", timeoutSeconds),
                ("phase", "DeadAwaitingRespawn"));
        }

        private float GetOutOfBoundsKillY() {
            if(playerController != null) {
                return playerController.GetOutOfBoundsKillY();
            }

            return OutOfBoundsKillYDefault;
        }

        private bool IsYLevelOutOfBoundsKillEnabled() {
            if(playerController != null) {
                return playerController.IsYLevelOutOfBoundsKillEnabled();
            }

            return true;
        }
    }
}
