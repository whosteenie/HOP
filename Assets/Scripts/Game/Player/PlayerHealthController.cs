using System.Collections;
using Cysharp.Threading.Tasks;
using Game.Weapons;
using Network;
using Network.Rpc;
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
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private PlayerTagController tagController;
        [SerializeField] private PlayerVisualController visualController;
        [SerializeField] private PlayerAnimationController animationController;
        [SerializeField] private PlayerShadow playerShadow;
        [SerializeField] private PlayerRagdoll playerRagdoll;
        [SerializeField] private DeathCamera deathCamera;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private CharacterController characterController;
        [SerializeField] private ClientNetworkTransform clientNetworkTransform;
        [SerializeField] private Transform tr;
        [SerializeField] private PlayerLookController lookController;
        [SerializeField] private PlayerMovementController movementController;
        [SerializeField] private PlayerTeamManager teamManager;
        [SerializeField] private GameObject worldModelRoot;
        [SerializeField] private Transform worldWeaponSocket; // Socket containing all world weapon GameObjects
        [SerializeField] private Animator characterAnimator;
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private CinemachineImpulseSource impulseSource;
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
        public NetworkVariable<int> kills;
        public NetworkVariable<int> deaths;
        public NetworkVariable<int> assists;
        public NetworkVariable<float> damageDealt;

        private void Awake() {
            // Initialize transform reference
            if(tr == null) {
                tr = transform;
            }

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(tagController == null) {
                tagController = GetComponent<PlayerTagController>();
            }

            if(visualController == null) {
                visualController = GetComponent<PlayerVisualController>();
            }

            if(animationController == null) {
                animationController = GetComponent<PlayerAnimationController>();
            }

            if(playerShadow == null) {
                playerShadow = GetComponent<PlayerShadow>();
            }

            if(playerRagdoll == null) {
                playerRagdoll = GetComponent<PlayerRagdoll>();
            }

            if(deathCamera == null) {
                deathCamera = GetComponent<DeathCamera>();
            }

            if(weaponManager == null) {
                weaponManager = GetComponent<WeaponManager>();
            }

            if(characterController == null) {
                characterController = GetComponent<CharacterController>();
            }

            if(clientNetworkTransform == null) {
                clientNetworkTransform = GetComponent<ClientNetworkTransform>();
            }
            
            if(lookController == null) {
                lookController = GetComponent<PlayerLookController>();
            }
            
            if(movementController == null) {
                movementController = GetComponent<PlayerMovementController>();
            }
            
            if(teamManager == null) {
                teamManager = GetComponent<PlayerTeamManager>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            // Get network variables from PlayerController (network-dependent)
            if(playerController != null) {
                netHealth = playerController.netHealth;
                netIsDead = playerController.netIsDead;
                kills = playerController.kills;
                deaths = playerController.deaths;
                assists = playerController.assists;
                damageDealt = playerController.damageDealt;
            }
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

        public bool ApplyDamageServer_Auth(float amount, Vector3 hitPoint, Vector3 hitDirection, ulong attackerId, string bodyPartTag = null, bool isHeadshot = false) {
            if(!IsServer || netIsDead == null || netIsDead.Value) return false;

            // Check if we're in Tag mode
            var matchSettings = MatchSettingsManager.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

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
                bool nonTaggedShootingTagged = false;
                if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                    var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                    var attackerTagController = attacker?.GetComponent<PlayerTagController>();
                    
                    // If attacker is NOT tagged and victim IS tagged, reduce attacker's timeTagged
                    if(attackerTagController != null && !attackerTagController.isTagged.Value && 
                       tagController != null && tagController.isTagged.Value) {
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
                if(tagController != null && !nonTaggedShootingTagged) {
                    tagController.HandleTagTransfer(attackerId, hitPoint, amount);
                }

                return false; // No kill in tag mode (except OOB)
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

                if(newHp <= 0f && !netIsDead.Value && !(PostMatchManager.Instance?.PostMatchFlowStarted ?? false)) {
                    netIsDead.Value = true;
                    
                    // Drop hopball if holding one when player dies (server-side)
                    // Check hopball's equipped state and holder ID directly
                    if(HopballSpawnManager.Instance != null && HopballSpawnManager.Instance.CurrentHopball != null) {
                        var hopball = HopballSpawnManager.Instance.CurrentHopball;
                        // Check if this player is the current holder by comparing OwnerClientId
                        if(hopball.IsEquipped && hopball.HolderController != null && hopball.HolderController.OwnerClientId == OwnerClientId) {
                            Debug.Log($"[PlayerHealthController] Player {OwnerClientId} died while holding hopball, dropping immediately");
                            var hopballController = playerController.GetComponent<HopballController>();
                            if(hopballController != null) {
                                hopballController.DropHopballOnDeath();
                            }
                        }
                    }

                    // Handle OOB kills (attackerId == ulong.MaxValue) or normal kills
                    if(attackerId == ulong.MaxValue) {
                        // OOB kill - use "HOP" as the killer name
                        var victimName = playerController.playerName != null
                            ? playerController.playerName.Value.ToString()
                            : "Player";
                        BroadcastKillClientRpc("HOP", victimName, attackerId, OwnerClientId);
                    } else if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var killerClient)) {
                        var killer = killerClient.PlayerObject?.GetComponent<PlayerController>();
                        if(killer != null) {
                            killer.kills.Value++;
                            var killerName = killer.playerName != null ? killer.playerName.Value.ToString() : "Player";
                            var victimName = playerController.playerName != null
                                ? playerController.playerName.Value.ToString()
                                : "Player";
                            BroadcastKillClientRpc(killerName, victimName, attackerId, OwnerClientId);
                        }
                    }

                    deaths.Value++;
                    
                    // Reserve spawn point immediately when player dies (server-side)
                    ReserveSpawnPointForDeath();
                    
                    DieClientRpc(_lastHitPoint ?? tr.position, _lastHitDirection ?? Vector3.up, _lastBodyPartTag);
                    return true;
                }

                return false;
            }
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
                    if(hopball.IsEquipped && hopball.HolderController != null && hopball.HolderController.OwnerClientId == OwnerClientId) {
                        Debug.Log($"[PlayerHealthController] Player {OwnerClientId} died (OOB) while holding hopball, dropping immediately");
                        var hopballController = playerController.GetComponent<HopballController>();
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

            DieClientRpc(_lastHitPoint ?? tr.position, _lastHitDirection ?? Vector3.up, _lastBodyPartTag);
        }

        [Rpc(SendTo.Everyone)]
        private void DieClientRpc(Vector3 hitPoint, Vector3 hitDirection, string bodyPartTag = null) {
            if(playerRagdoll != null) {
                if(_lastHitPoint.HasValue && _lastHitDirection.HasValue)
                    playerRagdoll.EnableRagdoll(_lastHitPoint, _lastHitDirection, bodyPartTag);
                else
                    playerRagdoll.EnableRagdoll();
            }

            if(visualController != null) {
                visualController.SetRenderersEnabled(true);
            }

            if(IsOwner) {
                if(weaponManager != null && weaponManager.WeaponCameraController != null) {
                    weaponManager.WeaponCameraController.SetWeaponCameraEnabled(false);
                }

                HUDManager.Instance?.HideHUD();
                if(deathCamera != null) {
                    deathCamera.EnableDeathCamera();
                }

                // Set shadow mode on current world weapon
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null) {
                    var weaponRenderer = currentWorldWeapon.GetComponent<MeshRenderer>();
                    if(weaponRenderer != null) {
                        weaponRenderer.shadowCastingMode = ShadowCastingMode.On;
                    }
                }

                if(IsOwner && fpCamera != null) {
                    float baseFov = lookController != null ? lookController.BaseFov : 80f;
                    fpCamera.Lens.FieldOfView = baseFov;
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
            bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

            SpawnPoint reservedPoint = null;
            if(isTeamBased) {
                var team = teamManager != null && teamManager.netTeam != null
                    ? teamManager.netTeam.Value
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
            if(netIsDead == null || !netIsDead.Value) return;

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
                bool isTeamBased = matchSettings != null && IsTeamBasedMode(matchSettings.selectedGameModeId);

                if(isTeamBased) {
                    var team = teamManager != null && teamManager.netTeam != null
                        ? teamManager.netTeam.Value
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
            if(IsOwner && SceneTransitionManager.Instance != null) {
                if(_respawnFadeCoroutine != null) {
                    StopCoroutine(_respawnFadeCoroutine);
                }

                if(SceneTransitionManager.Instance != null) {
                    _respawnFadeCoroutine = StartCoroutine(SceneTransitionManager.Instance.FadeRespawnTransition());
                }
            }
        }

        private static bool IsTeamBasedMode(string modeId) => modeId switch {
            "Team Deathmatch" => true,
            "Hopball" => true,
            "CTF" => true,
            "Oddball" => true,
            "KOTH" => true,
            _ => false
        };

        private (Vector3 pos, Quaternion rot) GetSpawnPointForTeam(SpawnPoint.Team team) {
            var point = SpawnManager.Instance?.GetNextSpawnPointForRespawn(team);
            if(point == null) {
                return (Vector3.zero, Quaternion.identity);
            }
            return (point.transform.position, point.transform.rotation);
        }

        private (Vector3 pos, Quaternion rot) GetSpawnPointFfa() {
            var point = SpawnManager.Instance?.GetNextSpawnPointForRespawn();
            if(point == null) {
                return (Vector3.zero, Quaternion.identity);
            }
            return (point.transform.position, point.transform.rotation);
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
            if(characterController != null) characterController.enabled = true;

            // Reset pitch and velocity
            if(lookController != null) {
                lookController.ResetPitch();
            }

            if(playerController != null) {
                playerController.lookInput = Vector2.zero;
            }

            if(movementController != null) {
                movementController.ResetVelocity();
            }

            if(fpCamera != null) {
                fpCamera.transform.localRotation = Quaternion.identity;
            }

            HUDManager.Instance?.ShowHUD();

            ShowRespawnVisualsClientRpc(tr.position);
        }

        [Rpc(SendTo.Everyone)]
        private void ShowRespawnVisualsClientRpc(Vector3 expectedSpawnPosition) {
            if(IsOwner) {
                // Owner: enable weapon camera and set up shadows-only mode for world model
                if(weaponManager != null && weaponManager.WeaponCameraController != null) {
                    weaponManager.WeaponCameraController.SetWeaponCameraEnabled(true);
                }

                // Ensure world model root and weapon are active for shadows
                if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                    worldModelRoot.SetActive(true);
                }

                // Get current world weapon and ensure it's active for shadows
                var currentWorldWeapon = GetCurrentWorldWeapon();
                if(currentWorldWeapon != null && !currentWorldWeapon.activeSelf) {
                    currentWorldWeapon.SetActive(true);
                }

                // Set renderers to shadows-only mode (owner sees shadows but not the model)
                if(visualController != null) {
                    visualController.InvalidateRendererCache();
                }

                // Set shadow mode to ShadowsOnly for owner (renderers must be enabled to cast shadows)
                if(playerShadow != null) {
                    // Enable renderers and set to shadows-only mode
                    playerShadow.SetSkinnedMeshRenderersShadowMode(ShadowCastingMode.ShadowsOnly,
                        ShadowCastingMode.ShadowsOnly, true);
                    playerShadow.SetWorldWeaponRenderersShadowMode(ShadowCastingMode.ShadowsOnly, true);
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
            if(playerRagdoll != null) {
                playerRagdoll.DisableRagdoll();
            }

            if(visualController != null) {
                visualController.InvalidateRendererCache();
            }

            HideVisuals();

            ResetAnimatorState(characterAnimator);

            if(IsOwner) {
                if(deathCamera != null) {
                    deathCamera.DisableDeathCamera();
                }

                TeleportOwnerClientRpc(position, rotation);
            }
        }

        [Rpc(SendTo.Owner)]
        private void TeleportOwnerClientRpc(Vector3 spawn, Quaternion rotation) {
            _ = TeleportAndNotifyAsync(spawn, rotation);
        }

        private async UniTaskVoid TeleportAndNotifyAsync(Vector3 spawn, Quaternion rotation) {
            if(characterController != null) characterController.enabled = false;

            if(clientNetworkTransform != null) {
                clientNetworkTransform.Teleport(spawn, rotation, Vector3.one);
            } else {
                tr.SetPositionAndRotation(spawn, rotation);
            }

            // Track respawn time to prevent landing sounds on respawn
            if(animationController != null) {
                animationController.ResetSpawnTime();
            }

            await UniTask.WaitForFixedUpdate();
            await UniTask.WaitForFixedUpdate();

            var currentPos = tr.position;
            var distanceMoved = Vector3.Distance(currentPos, spawn);
            if(distanceMoved > 0.1f) {
                await UniTask.Delay(50);
            }
        }

        private void HideVisuals() {
            if(visualController != null) {
                visualController.SetRenderersEnabled(false);
            }
        }

        private IEnumerator ShowVisualsAfterPositionSync(Vector3 expectedPosition) {
            int maxWaitFrames = 10;
            int framesWaited = 0;

            while(framesWaited < maxWaitFrames) {
                var distance = Vector3.Distance(tr.position, expectedPosition);
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
            if(visualController != null) {
                visualController.InvalidateRendererCache();
            }

            // Ensure world model root and weapon are active
            if(worldModelRoot != null && !worldModelRoot.activeSelf) {
                worldModelRoot.SetActive(true);
            }

            if(visualController != null) {
                visualController.SetRenderersEnabled(true, true, ShadowCastingMode.On);
            }

            // Force bounds update immediately
            if(visualController != null) {
                visualController.ForceRendererBoundsUpdate();
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
            if(tagController != null) {
                tagController.ResetTagState();
            }
        }

        private void ResetAnimatorState(Animator animator) {
            if(animator != null) {
                animator.Rebind();
                animator.Update(0f);
            }
        }

        /// <summary>
        /// Gets the currently equipped world weapon GameObject from the weapon socket.
        /// </summary>
        private GameObject GetCurrentWorldWeapon() {
            // Try to get from WeaponManager first (most reliable)
            if(weaponManager != null) {
                var weaponData = weaponManager.GetWeaponDataByIndex(weaponManager.CurrentWeaponIndex);
                if(weaponData != null && !string.IsNullOrEmpty(weaponData.worldWeaponName) &&
                   worldWeaponSocket != null) {
                    var worldObj = worldWeaponSocket.Find(weaponData.worldWeaponName);
                    if(worldObj != null && worldObj.gameObject.activeSelf) {
                        return worldObj.gameObject;
                    }
                }
            }

            // Fallback: find the first active child in the weapon socket
            if(worldWeaponSocket != null) {
                foreach(Transform child in worldWeaponSocket) {
                    if(child.gameObject.activeSelf) {
                        return child.gameObject;
                    }
                }
            }

            return null;
        }
    }
}