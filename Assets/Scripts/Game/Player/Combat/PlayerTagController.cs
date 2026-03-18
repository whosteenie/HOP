using Diagnostics;
using Events;
using Game.Match;
using Game.Player.Contracts;
using Network.Core;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Combat {
    /// <summary>
    /// Handles all Gun Tag mode logic including tag transfers, stats, and visual effects.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerTagController : NetworkBehaviour {
        private bool HasTagAuthority => NetworkAuthority.HasGlobalAuthority(this);

        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;
        private IPlayerTagContext _playerContext;
        private MatchPlayerStateProxy _cachedPlayerState;
        private MatchPlayerStateProxy _boundPlayerState;
        private static readonly NetworkVariable<int> MissingIntState = new();
        private static readonly NetworkVariable<bool> MissingBoolState = new();

        // Tag mode network variables
        public NetworkVariable<int> Tags {
            get {
                var playerState = ResolvePlayerState();
                return playerState != null ? playerState.tags : MissingIntState;
            }
        }

        public NetworkVariable<int> Tagged {
            get {
                var playerState = ResolvePlayerState();
                return playerState != null ? playerState.tagged : MissingIntState;
            }
        }

        public NetworkVariable<int> TimeTagged {
            get {
                var playerState = ResolvePlayerState();
                return playerState != null ? playerState.timeTagged : MissingIntState;
            }
        } // Time tagged in seconds

        public NetworkVariable<bool> IsTagged {
            get {
                var playerState = ResolvePlayerState();
                return playerState != null ? playerState.isTagged : MissingBoolState;
            }
        }

        // Throttling for network updates (at 90Hz: 5 ticks = ~55ms, 2 ticks = ~22ms)
        public float lastTagStatsUpdateTime; // Public for cross-reference in HandleTagTransfer
        private float _lastIsTaggedUpdateTime;
        private const float TagStatsUpdateInterval = 0.055f; // ~5 ticks at 90Hz

        private float _timer;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) return;
            DevLog.LogError("[PlayerTagController] IPlayerTagContext not found!");
            enabled = false;
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component references should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            // Network-dependent initialization
            // Subscribe to tag state changes
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;
            EventBus.Subscribe<PlayerTagBootstrapSnapshotRequestedEvent>(OnPlayerTagBootstrapSnapshotRequested);
            EventBus.Subscribe<InitialTagDesignationRequestedEvent>(OnInitialTagDesignationRequested);
            TryBindTagState();

            // Update outline on spawn if already tagged
            if(_playerContext != null) {
                _playerContext.UpdateTeamOutlineColour();
            }
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            EventBus.Unsubscribe<PlayerTagBootstrapSnapshotRequestedEvent>(OnPlayerTagBootstrapSnapshotRequested);
            EventBus.Unsubscribe<InitialTagDesignationRequestedEvent>(OnInitialTagDesignationRequested);
            UnbindTagState();
        }

        private void Update() {
            if(!HasTagAuthority) return;

            _timer += Time.deltaTime;
            if(!(_timer >= 1f)) return;

            _timer = 0f;

            if(_playerContext is not { IsGunTagMode: true } || !IsTagged.Value) return;

            if(Time.time - lastTagStatsUpdateTime >= TagStatsUpdateInterval) {
                TimeTagged.Value++;
                lastTagStatsUpdateTime = Time.time;
            } else {
                var current = TimeTagged.Value;
                TimeTagged.Value = current + 1;
            }
        }
        
        private void LateUpdate() {
            // Client-side progression tracking
            if(IsOwner && IsTagged.Value) {
                EventBus.Publish(new PlayerTimeTaggedProgressionEvent(OwnerClientId, Time.deltaTime));
            }
        }

        /// <summary>
        /// Handles tag transfer logic when a player is hit in Tag mode.
        /// Called from PlayerController.ApplyDamageServer_Auth when in tag mode.
        /// </summary>
        public void HandleTagTransfer(ulong attackerId, Vector3 hitPoint, float amount) {
            if(!HasTagAuthority) return;

            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(attackerId, out var attackerClient)) return;

            PlayerTagController attackerTagController = null;
            if(attackerClient.PlayerObject != null) {
                attackerTagController = attackerClient.PlayerObject.GetComponent<PlayerTagController>();
            }

            if(attackerTagController == null || !attackerTagController.IsTagged.Value) {
                return;
            }

            if(_playerContext != null) {
                _playerContext.PlayHitEffects(hitPoint, amount);
            }

            var wasTagged = IsTagged.Value;
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
            if(IsOwner && _playerContext is { IsGunTagMode: true }) {
                EventBus.Publish(new UpdateTagStatusEvent(newValue));
            }

            if(_playerContext != null) {
                EventBus.Publish(new PlayerTagStateChangedEvent(_playerContext.OwnerClientId, newValue));
            }

            if(_playerContext != null) {
                _playerContext.UpdateTeamOutlineColour();
            }

            // Update FP weapon glow (owner only)
            if(!IsOwner || _playerContext == null) return;
            _playerContext.UpdateFpArmTagGlow(newValue);
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void BroadcastTagTransferClientRpc(ulong taggerClientId, ulong taggedClientId) {
            // Get player names
            var taggerName = "Unknown";
            var taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggerClientId, out _)) {
                var taggerState = MatchPlayerStateProxy.GetForPlayer(taggerClientId);
                if(taggerState != null) {
                    taggerName = taggerState.playerName.Value.ToString();
                }
            }

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out _)) {
                var taggedState = MatchPlayerStateProxy.GetForPlayer(taggedClientId);
                if(taggedState != null) {
                    taggedName = taggedState.playerName.Value.ToString();
                }
            }

            var isLocalTagger = NetworkManager.Singleton.LocalClientId == taggerClientId;
            EventBus.Publish(new AddKillFeedEntryEvent(taggerName, taggedName, isLocalTagger, taggerClientId,
                taggedClientId, wasKill: false));
        }

        /// <summary>
        /// Broadcasts a tag transfer from HOP (initial designation) to the kill feed.
        /// Similar to OOB kills, uses ulong.MaxValue as the tagger client ID.
        /// </summary>
        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Global
        private void BroadcastTagTransferFromHopClientRpc(ulong taggedClientId) {
            var taggedName = "Unknown";

            if(NetworkManager.Singleton.ConnectedClients.TryGetValue(taggedClientId, out _)) {
                var taggedState = MatchPlayerStateProxy.GetForPlayer(taggedClientId);
                if(taggedState != null) {
                    taggedName = taggedState.playerName.Value.ToString();
                }
            }

            // HOP is never the local player, so isLocalTagger is always false
            EventBus.Publish(new AddKillFeedEntryEvent("HOP", taggedName, false, ulong.MaxValue, taggedClientId,
                wasKill: false));
        }

        /// <summary>
        /// Plays UI sound when this player gets tagged (called on the victim's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PlayTaggedSoundClientRpc() {
            EventBus.Publish(new PlayLocalSoundIdEvent("ui.tag.tagged"));
        }

        /// <summary>
        /// Plays UI sound when this player tags someone (called on the attacker's client).
        /// </summary>
        [Rpc(SendTo.Owner)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void PlayTaggingSoundClientRpc() {
            EventBus.Publish(new PlayLocalSoundIdEvent("ui.tag.tagger"));
            EventBus.Publish(new PlayerTagRecordedProgressionEvent(OwnerClientId));
        }

        /// <summary>
        /// Resets tag state (called on respawn).
        /// </summary>
        public void ResetTagState() {
            if(!HasTagAuthority) return;

            if(_playerContext is { IsGunTagMode: true }) {
                // Do NOT reset tag state on respawn - keep "It" status if they died/fell off map
                // isTagged.Value = false; 
            }
        }

        public void ApplyTimeTaggedDeltaAuthority(int delta) {
            TimeTagged.Value = Mathf.Max(0, TimeTagged.Value + delta);
        }

        private void ApplyTagVictimAuthority() {
            IsTagged.Value = true;

            _playerContext?.DrainCurrentWeaponAmmoForTag();

            Tagged.Value++;
            lastTagStatsUpdateTime = Time.time;
        }

        private void ApplyTagAttackerAuthority() {
            IsTagged.Value = false;
            Tags.Value++;
            lastTagStatsUpdateTime = Time.time;
        }

        private void OnPlayerTagBootstrapSnapshotRequested(PlayerTagBootstrapSnapshotRequestedEvent evt) {
            if(evt == null || !IsSpawned || _playerContext is not { IsGunTagMode: true }) return;
            EventBus.Publish(new PlayerTagBootstrapStateReportedEvent(_playerContext.OwnerClientId, IsTagged.Value));
        }

        private void OnInitialTagDesignationRequested(InitialTagDesignationRequestedEvent evt) {
            if(evt == null || !HasTagAuthority || _playerContext == null || evt.PlayerClientId != _playerContext.OwnerClientId) {
                return;
            }

            IsTagged.Value = true;
            Tagged.Value++;
            PlayTaggedSoundClientRpc();
            BroadcastTagTransferFromHopClientRpc(_playerContext.OwnerClientId);
        }

        private MatchPlayerStateProxy ResolvePlayerState() {
            if(_playerContext == null) {
                return null;
            }

            if(_cachedPlayerState != null &&
               _cachedPlayerState.RepresentedClientId == _playerContext.OwnerClientId &&
               _cachedPlayerState.NetworkObject != null &&
               _cachedPlayerState.NetworkObject.IsSpawned) {
                return _cachedPlayerState;
            }

            _cachedPlayerState = MatchPlayerStateProxy.GetForPlayer(_playerContext.OwnerClientId);
            return _cachedPlayerState;
        }

        private void OnPlayerStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(_playerContext == null || playerClientId != _playerContext.OwnerClientId) {
                return;
            }

            _cachedPlayerState = proxy;
            TryBindTagState();
        }

        private void OnPlayerStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            if(_playerContext == null || playerClientId != _playerContext.OwnerClientId) {
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

