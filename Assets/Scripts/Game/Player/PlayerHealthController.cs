using System.Collections;
using Cysharp.Threading.Tasks;
using Game.Weapons;
using Network;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

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
        private const float RegenDelay = 3f;
        private const float RegenRate = 10f;
        private const float MaxHealth = 100f;

        // Health state
        private Vector3? _lastHitPoint;
        private Vector3? _lastHitDirection;
        private float _lastDamageTime;
        private string _lastBodyPartTag;
        private bool _isRegenerating;
        private Coroutine _respawnFadeCoroutine;

        // Spawn reservation
        private SpawnPoint _reservedSpawnPoint;

        // Network variables (from PlayerController)
        public NetworkVariable<float> netHealth;
        public NetworkVariable<bool> netIsDead;
        public NetworkVariable<int> deaths;

        private void Awake() {
            playerController ??= GetComponent<PlayerController>();
            
            _playerTransform ??= playerController.PlayerTransform;

            _tagController ??= playerController.TagController;

            _visualController ??= playerController.VisualController;

            _animationController ??= playerController.AnimationController;

            _playerShadow ??= playerController.PlayerShadow;

            _playerRagdoll ??= playerController.PlayerRagdoll;

            _deathCameraController ??= playerController.DeathCameraController;

            _weaponManager ??= playerController.WeaponManager;

            _characterController ??= playerController.CharacterController;

            _clientNetworkTransform ??= playerController.ClientNetworkTransform;

            _lookController ??= playerController.LookController;

            _movementController ??= playerController.MovementController;

            _teamManager ??= playerController.TeamManager;

            _playerModelRoot ??= playerController.PlayerModelRoot;

            _worldWeaponSocket ??= playerController.WorldWeaponSocket;

            _playerAnimator ??= playerController.PlayerAnimator;

            _fpCamera ??= playerController.FpCamera;

            _impulseSource ??= playerController.ImpulseSource;

            _weaponCameraController ??= playerController.WeaponCameraController;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            // Get network variables from PlayerController (network-dependent)
            if(playerController == null) return;
            netHealth = playerController.NetHealth;
            netIsDead = playerController.NetIsDead;
            deaths = playerController.Deaths;
        }

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

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId,
            string bodyPartTag = null, bool isHeadshot = false) {
            if(!IsServer || netIsDead == null || netIsDead.Value) return false;

            // Check if we're in Tag mode
            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            _lastHitPoint = hitPoint;
            _lastHitDirection = hitDirection;
            _lastDamageTime = Time.time;
            _isRegenerating = false;
            _lastBodyPartTag = bodyPartTag; // Store for ragdoll force application

            // TODO: Apply headshot multiplier when balancing weapons
            // if(isHeadshot) {
            //     amount *= headshotMultiplier; // e.g., 2.0f for double damage
            // }

            if(isTagMode) {
                // Tag mode: check if non-tagged player is shooting tagged player
                // If so, reduce the attacker's accumulated timeTagged by 1 (capped at 0)
                var nonTaggedShootingTagged = false;
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                    var attackerTagController = attacker?.GetComponent<PlayerTagController>();

                    // If attacker is NOT tagged and victim IS tagged, reduce attacker's timeTagged
                    if(attackerTagController != null && !attackerTagController.isTagged.Value &&
                       _tagController != null && _tagController.isTagged.Value) {
                        nonTaggedShootingTagged = true;
                        // Reduce by 1, but cap at 0
                        if(attackerTagController.timeTagged.Value > 0) {
                            attackerTagController.timeTagged.Value--;
                        }

                        // Play hit effects even though no tag transfer occurs
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

                if(playerController != null) {
                    playerController.PlayHitEffectsClientRpc(hitPoint, amount);
                }

                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                    if(attacker != null && attacker.damageDealt != null) {
                        attacker.damageDealt.Value += actualDealt;
                    }
                }

                if(!(newHp <= 0f) || netIsDead.Value || (PostMatchManager.Instance?.PostMatchFlowStarted ?? false))
                    return false; // No kill in tag mode (except OOB)
                netIsDead.Value = true;

                // Drop hopball if holding one when player dies (server-side)
                // Check hopball's equipped state and holder ID directly
                if(HopballSpawnManager.Instance != null && HopballSpawnManager.Instance.CurrentHopball != null) {
                    var hopball = HopballSpawnManager.Instance.CurrentHopball;
                    // Check if this player is the current holder by comparing OwnerClientId
                    if(hopball.IsEquipped && hopball.HolderController != null &&
                       hopball.HolderController.OwnerClientId == OwnerClientId) {
                        var hopballController = playerController?.HopballController;
                        if(hopballController != null) {
                            hopballController.DropHopballOnDeath();
                        }
                    }
                }

                // Handle OOB kills (attackerId == ulong.MaxValue) or normal kills
                if(attackerId == ulong.MaxValue) {
                    // OOB kill - use "HOP" as the killer name
                    var victimName = playerController != null && playerController.PlayerName != null
                        ? playerController.PlayerName.Value.ToString()
                        : "Player";
                    BroadcastKillClientRpc("HOP", victimName, attackerId, OwnerClientId);
                } else if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                    var killer = killerClient.PlayerObject?.GetComponent<PlayerController>();
                    if(killer != null) {
                        killer.kills.Value++;
                        var killerName = killer.playerName != null ? killer.playerName.Value.ToString() : "Player";
                        var victimName = playerController != null && playerController.PlayerName != null
                            ? playerController.PlayerName.Value.ToString()
                            : "Player";
                        BroadcastKillClientRpc(killerName, victimName, attackerId, OwnerClientId);
                    }
                }

                deaths.Value++;

                // Reserve spawn point immediately when player dies (server-side)
                ReserveSpawnPointForDeath();

                DieClientRpc(_lastHitPoint ?? _playerTransform.position, _lastHitDirection ?? Vector3.up, _lastBodyPartTag);
                return true;
            }

            return false; // No kill in tag mode (except OOB)
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastKillClientRpc(string killerName, string victimName, ulong killerClientId,
            ulong victimClientId) {
            var isLocalKiller = NetworkManager.Singleton.LocalClientId == killerClientId;
            KillFeedManager.Instance?.AddEntryToFeed(killerName, victimName, isLocalKiller, killerClientId,
                victimClientId);
        }

        private void DieServer() {
            if(!IsServer) return;

            if(netIsDead != null) {
                netIsDead.Value = true;

                // Drop hopball if holding one when player dies (OOB death)
                // Check hopball's equipped state and holder ID directly
                if(HopballSpawnManager.Instance != null && HopballSpawnManager.Instance.CurrentHopball != null) {
                    var hopball = HopballSpawnManager.Instance.CurrentHopball;
                    // Check if this player is the current holder by comparing OwnerClientId
                    if(hopball.IsEquipped && hopball.HolderController != null &&
                       hopball.HolderController.OwnerClientId == OwnerClientId) {
                        Debug.Log(
                            $"[PlayerHealthController] Player {OwnerClientId} died (OOB) while holding hopball, dropping immediately");
                        var hopballController = playerController?.HopballController;
                        if(hopballController != null) {
                            hopballController.DropHopballOnDeath();
                        }
                    }
                }
            }

            if(deaths != null) {
                deaths.Value++;
            }

            // Reserve spawn point immediately when player dies (server-side)
            ReserveSpawnPointForDeath();

            DieClientRpc(_lastHitPoint ?? _playerTransform.position, _lastHitDirection ?? Vector3.up, _lastBodyPartTag);
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(Vector3 hitPoint, Vector3 hitDirection, string bodyPartTag = null) {
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
                playerController?.PlayerInput?.ForceDisableSniperOverlay(false);
                if(_weaponManager != null && _weaponCameraController != null) {
                    _weaponCameraController.SetWeaponCameraEnabled(false);
                }

                HUDManager.Instance?.HideHUD();
                if(_deathCameraController != null) {
                    _deathCameraController.EnableDeathCamera();
                }

                // Set shadow mode on current world weapon
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null) {
                    var weaponRenderer = currentWorldWeapon.GetComponent<MeshRenderer>();
                    if(weaponRenderer != null) {
                        weaponRenderer.shadowCastingMode = ShadowCastingMode.On;
                    }
                }

                if(IsOwner && _fpCamera != null) {
                    var baseFov = _lookController != null ? _lookController.BaseFov : 80f;
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
            var isTeamBased = matchSettings != null && MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

            SpawnPoint reservedPoint;
            if(isTeamBased) {
                var team = _teamManager != null && _teamManager.netTeam != null
                    ? _teamManager.netTeam.Value
                    : SpawnPoint.Team.TeamA;
                reservedPoint = SpawnManager.Instance?.ReserveSpawnPoint(OwnerClientId, team);
            } else {
                reservedPoint = SpawnManager.Instance?.ReserveSpawnPoint(OwnerClientId);
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

        private void DoRespawnServer() {
            PrepareRespawnClientRpc();

            Vector3 position;
            Quaternion rotation;

            // Use reserved spawn point if available, otherwise fall back to finding a new one
            if(_reservedSpawnPoint != null) {
                position = _reservedSpawnPoint.transform.position;
                rotation = _reservedSpawnPoint.transform.rotation;
            } else {
                // Fallback: get a new spawn point (should rarely happen)
                var matchSettings = MatchSettingsManager.Instance;
                var isTeamBased = matchSettings != null && MatchSettingsManager.IsTeamBasedMode(matchSettings.selectedGameModeId);

                if(isTeamBased) {
                    var team = _teamManager != null && _teamManager.netTeam != null
                        ? _teamManager.netTeam.Value
                        : SpawnPoint.Team.TeamA;
                    (position, rotation) = GetSpawnPointForTeam(team);
                } else {
                    (position, rotation) = GetSpawnPointFfa();
                }
            }

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
            var point = SpawnManager.Instance?.GetNextSpawnPointForRespawn(team);
            return point == null
                ? (Vector3.zero, Quaternion.identity)
                : (point.transform.position, point.transform.rotation);
        }

        private (Vector3 pos, Quaternion rot) GetSpawnPointFfa() {
            var point = SpawnManager.Instance?.GetNextSpawnPointForRespawn();
            return point == null
                ? (Vector3.zero, Quaternion.identity)
                : (point.transform.position, point.transform.rotation);
        }

        private IEnumerator TeleportAfterPreparation(Vector3 position, Quaternion rotation) {
            const float fadeDuration = 0.5f;
            const float buffer = 0.15f;

            yield return new WaitForSeconds(fadeDuration + buffer);

            // Reset health and death state RIGHT BEFORE teleport to prevent race conditions
            // This ensures netIsDead is false immediately before teleporting, preventing OOB checks
            // from triggering again if the player is still below y=600f during the brief moment before teleport
            ResetHealthAndRegenerationState();

            // Release spawn point reservation when player actually spawns
            if(IsServer && _reservedSpawnPoint != null) {
                SpawnManager.Instance?.ReleaseReservation(OwnerClientId);
                _reservedSpawnPoint = null;
            }

            DisableRagdollAndTeleportClientRpc(position, rotation);

            const float holdDuration = 0.5f;
            yield return new WaitForSeconds(holdDuration);

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

            // Reset pitch and velocity
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

            HUDManager.Instance?.ShowHUD();

            ShowRespawnVisualsClientRpc(_playerTransform.position);
        }

        [Rpc(SendTo.Everyone)]
        private void ShowRespawnVisualsClientRpc(Vector3 expectedSpawnPosition) {
            if(IsOwner) {
                // Owner: enable weapon camera and set up shadows-only mode for world model
                if(_weaponManager != null && _weaponCameraController != null) {
                    _weaponCameraController.SetWeaponCameraEnabled(true);
                }

                // Ensure world model root and weapon are active for shadows
                if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                    _playerModelRoot.SetActive(true);
                }

                // Get current world weapon and ensure it's active for shadows
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                // Set renderers to shadows-only mode (owner sees shadows but not the model)
                if(_visualController != null) {
                    _visualController.InvalidateRendererCache();
                }

                // Set shadow mode to ShadowsOnly for owner (renderers must be enabled to cast shadows)
                if(_playerShadow != null) {
                    // Enable renderers and set to shadows-only mode
                    _playerShadow.SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly,
                        ShadowCastingMode.ShadowsOnly, true);
                    _playerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.ShadowsOnly);
                }

                // Reset weapon state for owner (enables FP weapon renderers)
                if(playerController != null) {
                    playerController.ResetWeaponState(resetAllAmmo: true, switchToWeapon0: true);
                }
            } else {
                // Non-owner: show world model after position sync
                StartCoroutine(ShowVisualsAfterPositionSync(expectedSpawnPosition));
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

        private void ShowVisuals() {
            // Invalidate renderer cache to force refresh (like respawn does)
            if(_visualController != null) {
                _visualController.InvalidateRendererCache();
            }

            // Ensure world model root and weapon are active
            if(_playerModelRoot != null && !_playerModelRoot.activeSelf) {
                _playerModelRoot.SetActive(true);
            }

            if(_visualController != null) {
                _visualController.SetRenderersEnabled(true, true, ShadowCastingMode.On);
            }

            // Force bounds update immediately
            if(_visualController != null) {
                _visualController.ForceRendererBoundsUpdate();
            }
        }

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
            // Try to get from WeaponManager first (most reliable)
            if(_weaponManager != null) {
                var weaponData = _weaponManager.GetWeaponDataByIndex(_weaponManager.CurrentWeaponIndex);
                if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName) &&
                   _worldWeaponSocket != null) {
                    var worldObj = _worldWeaponSocket.Find(weaponData.worldWeaponName);
                    if(worldObj != null && worldObj.gameObject.activeSelf) {
                        return worldObj.gameObject;
                    }
                }
            }

            // Fallback: find the first active child in the weapon socket
            if(_worldWeaponSocket == null) return null;
            foreach(Transform child in _worldWeaponSocket) {
                if(child.gameObject.activeSelf) {
                    return child.gameObject;
                }
            }

            return null;
        }
    }
}