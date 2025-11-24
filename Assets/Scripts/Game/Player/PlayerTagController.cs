using Network.Rpc;
using Network.Singletons;
using Unity.Netcode;
using Unity.Collections;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles all Gun Tag mode logic including tag transfers, stats, and visual effects.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerTagController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private PlayerTeamManager teamManager;

        // Tag mode network variables
        public NetworkVariable<int> tags = new();
        public NetworkVariable<int> tagged = new();
        public NetworkVariable<int> timeTagged = new(); // Time tagged in seconds
        public NetworkVariable<bool> isTagged = new();
        
        // Throttling for network updates (at 90Hz: 5 ticks = ~55ms, 2 ticks = ~22ms)
        public float _lastTagStatsUpdateTime; // Public for cross-reference in HandleTagTransfer
        private float _lastIsTaggedUpdateTime;
        public const float TagStatsUpdateInterval = 0.055f; // ~5 ticks at 90Hz
        private const float IsTaggedUpdateInterval = 0.022f; // ~2 ticks at 90Hz

        private float _timer;

        private void Awake() {
            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(teamManager == null) {
                teamManager = GetComponent<PlayerTeamManager>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(teamManager == null) {
                teamManager = GetComponent<PlayerTeamManager>();
            }

            // Network-dependent initialization
            // Subscribe to tag state changes
            isTagged.OnValueChanged -= OnTaggedStateChanged;
            isTagged.OnValueChanged += OnTaggedStateChanged;

            // Update outline on spawn if already tagged
            if(teamManager != null) {
                teamManager.UpdateOutlineColour();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            isTagged.OnValueChanged -= OnTaggedStateChanged;
        }

        private void Update() {
            if(!IsServer) return;

            // Tag mode: increment time tagged every second while tagged
            var matchSettings = MatchSettingsManager.Instance;
            bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            _timer += Time.deltaTime;
            if(_timer >= 1f) {
                _timer = 0f;

                if(isTagMode && isTagged.Value) {
                    // Tag mode: increment time tagged
                    // Throttle network updates - only send if enough time has passed
                    if(Time.time - _lastTagStatsUpdateTime >= TagStatsUpdateInterval) {
                        timeTagged.Value++;
                        _lastTagStatsUpdateTime = Time.time;
                    } else {
                        // Still increment locally, will sync on next update
                        var current = timeTagged.Value;
                        timeTagged.Value = current + 1;
                    }
                }
            }
        }

        /// <summary>
        /// Handles tag transfer logic when a player is hit in Tag mode.
        /// Called from PlayerController.ApplyDamageServer_Auth when in tag mode.
        /// </summary>
        public bool HandleTagTransfer(ulong attackerId, Vector3 hitPoint, float amount) {
            if(!IsServer) return false;

            // Only allow tagging if the attacker is "it" (tagged)
            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) {
                var attacker = attackerClient.PlayerObject?.GetComponent<PlayerController>();
                var attackerTagController = attacker?.GetComponent<PlayerTagController>();

                // If attacker is not tagged, they cannot tag others
                if(attackerTagController == null || !attackerTagController.isTagged.Value) {
                    return false; // No damage/kill, but also no tag transfer
                }

                // Attacker is tagged, proceed with tagging logic
                // Play hit effects (flinch animation) - this will be called on all clients
                if(playerController != null) {
                    playerController.PlayHitEffectsClientRpc(hitPoint, amount);
                }

                // Tag the player (1 bullet = tag) - only if victim is not already tagged
                bool wasTagged = isTagged.Value;
                if(!wasTagged) {
                    // isTagged is critical - update immediately, but throttle other stats
                    isTagged.Value = true;
                    
                    // Throttle tag stats updates
                    if(Time.time - _lastTagStatsUpdateTime >= TagStatsUpdateInterval) {
                        tagged.Value++;
                        _lastTagStatsUpdateTime = Time.time;
                    } else {
                        tagged.Value++; // Still update, will sync on next interval
                    }

                    // Play UI sound for getting tagged (on victim's client)
                    PlayTaggedSoundClientRpc();

                    // Untag the attacker (they successfully tagged someone)
                    // isTagged is critical - update immediately
                    attackerTagController.isTagged.Value = false;
                    
                    // Throttle tag stats updates
                    if(Time.time - attackerTagController._lastTagStatsUpdateTime >= TagStatsUpdateInterval) {
                        attackerTagController.tags.Value++;
                        attackerTagController._lastTagStatsUpdateTime = Time.time;
                    } else {
                        attackerTagController.tags.Value++; // Still update, will sync on next interval
                    }

                    // Play UI sound for tagging someone (on attacker's client)
                    attackerTagController.PlayTaggingSoundClientRpc();

                    // Broadcast tag transfer to kill feed (only on first bullet that tags)
                    BroadcastTagTransferClientRpc(attackerId, OwnerClientId);
                }

                return true; // Tag transfer handled
            } else {
                // Attacker not found, can't verify if they're tagged - don't allow tagging
                return false;
            }
        }

        private void OnTaggedStateChanged(bool oldValue, bool newValue) {
            // Update HUD for Tag mode
            if(IsOwner && playerController != null) {
                var matchSettings = MatchSettingsManager.Instance;
                if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                    HUDManager.Instance.UpdateTagStatus(newValue);
                }
            }

            // Update outline color via PlayerTeamManager
            if(teamManager != null) {
                teamManager.UpdateOutlineColour();
            }
        }

        [Rpc(SendTo.Everyone)]
        private void BroadcastTagTransferClientRpc(ulong taggerClientId, ulong taggedClientId) {
            // Get player names
            string taggerName = "Unknown";
            string taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggerClientId, out var taggerClient)) {
                var tagger = taggerClient.PlayerObject?.GetComponent<PlayerController>();
                if(tagger != null) {
                    taggerName = tagger.playerName.Value.ToString();
                }
            }

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                var taggedPlayer = taggedClient.PlayerObject?.GetComponent<PlayerController>();
                if(taggedPlayer != null) {
                    taggedName = taggedPlayer.playerName.Value.ToString();
                }
            }

            var isLocalTagger = NetworkManager.Singleton.LocalClientId == taggerClientId;
            KillFeedManager.Instance?.AddEntryToFeed(taggerName, taggedName, isLocalTagger, taggerClientId,
                taggedClientId);
        }

        /// <summary>
        /// Broadcasts a tag transfer from HOP (initial designation) to the kill feed.
        /// Similar to OOB kills, uses ulong.MaxValue as the tagger client ID.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        public void BroadcastTagTransferFromHopClientRpc(ulong taggedClientId) {
            string taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out var taggedClient)) {
                var taggedPlayer = taggedClient.PlayerObject?.GetComponent<PlayerController>();
                if(taggedPlayer != null) {
                    taggedName = taggedPlayer.playerName.Value.ToString();
                }
            }

            // HOP is never the local player, so isLocalTagger is always false
            KillFeedManager.Instance?.AddEntryToFeed("HOP", taggedName, false, ulong.MaxValue, taggedClientId);
        }

        /// <summary>
        /// Plays UI sound when this player gets tagged (called on the victim's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        public void PlayTaggedSoundClientRpc() {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.Tagged);
            }
        }

        /// <summary>
        /// Plays UI sound when this player tags someone (called on the attacker's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        public void PlayTaggingSoundClientRpc() {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.Tagging);
            }
        }

        /// <summary>
        /// Resets tag state (called on respawn).
        /// </summary>
        public void ResetTagState() {
            if(!IsServer) return;

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                isTagged.Value = false;
            }
        }
    }
}