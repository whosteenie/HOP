using Game.Match;
using Game.UI;
using Network.Core;
using Network.Events;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles all Gun Tag mode logic including tag transfers, stats, and visual effects.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerTagController : NetworkBehaviour {
        private bool HasTagAuthority => NetworkAuthority.HasGlobalAuthority(this);

        [Header("References")]
        [SerializeField] private PlayerController playerController;
        private PlayerTeamManager _teamManager;
        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private static readonly NetworkVariable<int> MissingIntState = new(0);
        private static readonly NetworkVariable<bool> MissingBoolState = new(false);

        // Tag mode network variables
        public NetworkVariable<int> tags => ResolvePlayerState()?.tags ?? MissingIntState;
        public NetworkVariable<int> tagged => ResolvePlayerState()?.tagged ?? MissingIntState;
        public NetworkVariable<int> timeTagged => ResolvePlayerState()?.timeTagged ?? MissingIntState; // Time tagged in seconds
        public NetworkVariable<bool> isTagged => ResolvePlayerState()?.isTagged ?? MissingBoolState;

        // Throttling for network updates (at 90Hz: 5 ticks = ~55ms, 2 ticks = ~22ms)
        public float lastTagStatsUpdateTime; // Public for cross-reference in HandleTagTransfer
        private float _lastIsTaggedUpdateTime;
        private const float TagStatsUpdateInterval = 0.055f; // ~5 ticks at 90Hz

        private float _timer;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[PlayerTagController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_teamManager == null) _teamManager = playerController.TeamManager;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(_teamManager == null) {
                _teamManager = GetComponent<PlayerTeamManager>();
            }

            // Network-dependent initialization
            // Subscribe to tag state changes
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;
            TryBindTagState();

            // Update outline on spawn if already tagged
            if(_teamManager != null) {
                _teamManager.UpdateOutlineColour();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            UnbindTagState();
        }

        private void Update() {
            if(!HasTagAuthority) return;

            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            _timer += Time.deltaTime;
            if(!(_timer >= 1f)) return;

            _timer = 0f;

            if(!isTagMode || !isTagged.Value) return;

            if(Time.time - lastTagStatsUpdateTime >= TagStatsUpdateInterval) {
                timeTagged.Value++;
                lastTagStatsUpdateTime = Time.time;
            } else {
                var current = timeTagged.Value;
                timeTagged.Value = current + 1;
            }
        }
        
        private void LateUpdate() {
            // Client-side progression tracking
            if (IsOwner && isTagged.Value && Progression.ProgressionManager.Instance != null) {
                 Progression.ProgressionManager.Instance.AddTimeTagged(Time.deltaTime);
            }
        }

        /// <summary>
        /// Handles tag transfer logic when a player is hit in Tag mode.
        /// Called from PlayerController.ApplyDamageServer_Auth when in tag mode.
        /// </summary>
        public void HandleTagTransfer(ulong attackerId, Vector3 hitPoint, float amount) {
            if(!HasTagAuthority) return;

            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) return;

            PlayerController attacker = null;
            if(attackerClient.PlayerObject != null) {
                attacker = attackerClient.PlayerObject.GetComponent<PlayerController>();
            }
            PlayerTagController attackerTagController = null;
            if(attacker != null) {
                attackerTagController = attacker.GetComponent<PlayerTagController>();
            }

            if(attackerTagController == null || !attackerTagController.isTagged.Value) {
                return;
            }

            if(playerController != null) {
                playerController.PlayHitEffectsClientRpc(hitPoint, amount);
            }

            var wasTagged = isTagged.Value;
            if(wasTagged) return;
            ApplyTagVictimAuthority();

            PlayTaggedSoundClientRpc();

            attackerTagController.ApplyTagAttackerAuthority();

            attackerTagController.PlayTaggingSoundClientRpc();

            BroadcastTagTransferClientRpc(attackerId, OwnerClientId);
        }

        /// <summary>
        /// Handles changes to the tagged state.
        /// </summary>
        private void OnTaggedStateChanged(bool oldValue, bool newValue) {
            // Update HUD for Tag mode
            if(IsOwner && playerController != null) {
                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                    EventBus.Publish(new UpdateTagStatusEvent(newValue));
                }
            }

            // Update outline color via PlayerTeamManager
            if(_teamManager != null) {
                _teamManager.UpdateOutlineColour();
            }

            // Update FP weapon glow (owner only)
            if(!IsOwner || playerController == null) return;
            var weaponManager = playerController.WeaponManager;
            if(weaponManager == null) return;
            weaponManager.UpdateAllFpArmTagGlow(newValue);
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void BroadcastTagTransferClientRpc(ulong taggerClientId, ulong taggedClientId) {
            // Get player names
            var taggerName = "Unknown";
            var taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggerClientId, out var taggerClient)) {
                PlayerController tagger = null;
                if(taggerClient.PlayerObject != null) {
                    tagger = taggerClient.PlayerObject.GetComponent<PlayerController>();
                }
                if(tagger != null) {
                    taggerName = tagger.PlayerName.Value.ToString();
                }
            }

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                PlayerController taggedPlayer = null;
                if(taggedClient.PlayerObject != null) {
                    taggedPlayer = taggedClient.PlayerObject.GetComponent<PlayerController>();
                }
                if(taggedPlayer != null) {
                    taggedName = taggedPlayer.PlayerName.Value.ToString();
                }
            }

            var isLocalTagger = NetworkManager.Singleton.LocalClientId == taggerClientId;
            if(KillFeedManager.Instance != null) {
                EventBus.Publish(new AddKillFeedEntryEvent(taggerName, taggedName, isLocalTagger, taggerClientId,
                    taggedClientId, wasKill: false));
            }
        }

        /// <summary>
        /// Broadcasts a tag transfer from HOP (initial designation) to the kill feed.
        /// Similar to OOB kills, uses ulong.MaxValue as the tagger client ID.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Global
        public void BroadcastTagTransferFromHopClientRpc(ulong taggedClientId) {
            var taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                PlayerController taggedPlayer = null;
                if(taggedClient.PlayerObject != null) {
                    taggedPlayer = taggedClient.PlayerObject.GetComponent<PlayerController>();
                }
                if(taggedPlayer != null) {
                    taggedName = taggedPlayer.PlayerName.Value.ToString();
                }
            }

            // HOP is never the local player, so isLocalTagger is always false
            if(KillFeedManager.Instance != null) {
                EventBus.Publish(new AddKillFeedEntryEvent("HOP", taggedName, false, ulong.MaxValue, taggedClientId, wasKill: false));
            }
        }

        /// <summary>
        /// Plays UI sound when this player gets tagged (called on the victim's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        // ReSharper disable once MemberCanBeMadeStatic.Global
        public void PlayTaggedSoundClientRpc() {
            if(Audio2.AudioService.Instance != null) {
                Audio2.AudioService.Instance.Play("ui.tag.tagged", Vector3.zero);
            }
        }

        /// <summary>
        /// Plays UI sound when this player tags someone (called on the attacker's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PlayTaggingSoundClientRpc() {
            if(Audio2.AudioService.Instance != null) {
                Audio2.AudioService.Instance.Play("ui.tag.tagger", Vector3.zero);
            }
            if (Progression.ProgressionManager.Instance != null) {
                Progression.ProgressionManager.Instance.RecordTag();
            }
        }

        /// <summary>
        /// Resets tag state (called on respawn).
        /// </summary>
        public void ResetTagState() {
            if(!HasTagAuthority) return;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                // Do NOT reset tag state on respawn - keep "It" status if they died/fell off map
                // isTagged.Value = false; 
            }
        }

        public void ApplyTimeTaggedDeltaAuthority(int delta) {
            timeTagged.Value = Mathf.Max(0, timeTagged.Value + delta);
        }

        public void ApplyTagVictimAuthority() {
            isTagged.Value = true;

            if(playerController != null && playerController.WeaponManager != null) {
                playerController.WeaponManager.DrainCurrentWeaponAmmoForTag();
            }

            tagged.Value++;
            lastTagStatsUpdateTime = Time.time;
        }

        public void ApplyTagAttackerAuthority() {
            isTagged.Value = false;
            tags.Value++;
            lastTagStatsUpdateTime = Time.time;
        }

        private MatchPlayerStateProxy ResolvePlayerState() {
            if(playerController == null) {
                return null;
            }

            if(_cachedPlayerState != null &&
               _cachedPlayerState.RepresentedClientId == playerController.OwnerClientId &&
               _cachedPlayerState.NetworkObject != null &&
               _cachedPlayerState.NetworkObject.IsSpawned) {
                return _cachedPlayerState;
            }

            _cachedPlayerState = MatchPlayerStateProxy.GetForPlayer(playerController.OwnerClientId);
            return _cachedPlayerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerClientId != playerController.OwnerClientId) {
                return;
            }

            _cachedPlayerState = proxy;
            TryBindTagState();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(playerController == null || playerClientId != playerController.OwnerClientId) {
                return;
            }

            if(_boundPlayerState == proxy) {
                UnbindTagState();
            }

            if(_cachedPlayerState == proxy) {
                _cachedPlayerState = null;
            }
        }

        private void TryBindTagState() {
            var playerState = ResolvePlayerState();
            if(playerState == null || _boundPlayerState == playerState) {
                return;
            }

            UnbindTagState();
            playerState.isTagged.OnValueChanged -= OnTaggedStateChanged;
            playerState.isTagged.OnValueChanged += OnTaggedStateChanged;
            _boundPlayerState = playerState;
        }

        private void UnbindTagState() {
            if(_boundPlayerState == null) {
                return;
            }

            _boundPlayerState.isTagged.OnValueChanged -= OnTaggedStateChanged;
            _boundPlayerState = null;
        }
    }
}
