using System;
using System.Collections.Generic;
using Game.Social;
using Network.Events;
using Network.Steam;
using UnityEngine;
using UnityEngine.UIElements;
using Cysharp.Threading.Tasks;
using Steamworks;
using Unity.Services.Vivox;
using Unity.Netcode;

namespace Game.UI {
    public class VoiceOverlayManager : UIElementBase {
        private VisualElement _overlayContainer;

        // State
        private readonly Dictionary<string, VisualElement> _activeSpeakerElements = new(); // SteamId string -> Element
        private readonly Dictionary<ulong, Player.PlayerController> _trackedPlayers = new(); // clientId -> player
        private readonly Dictionary<ulong, NetworkVariable<bool>.OnValueChangedDelegate> _pttHandlers = new();
        private readonly Dictionary<string, string> _participantToCanonicalId = new(StringComparer.Ordinal);
        private readonly List<string> _participantMappingsToRemove = new();
        private readonly HashSet<string> _localIdentityAliases = new(StringComparer.Ordinal);
        private string _localCanonicalId;
        private bool _networkCallbacksRegistered;
        private bool _playerLifecycleCallbacksRegistered;
        private float _nextLocalIdentityRefreshTime;
        private const float LocalIdentityRefreshIntervalSeconds = 0.5f;

        protected override void OnInitialize() {
            _overlayContainer = QOptional<VisualElement>("voice-overlay-container");

            if(VoiceManager.Instance != null) {
                VoiceManager.Instance.OnParticipantSpeechDetected += OnSpeechDetected;
                VoiceManager.Instance.OnParticipantRemoved += OnParticipantRemoved;
                VoiceManager.Instance.OnLocalPttStateChanged += OnLocalPttStateChanged;
            }

            // Listen for mute changes to update UI
            EventBus.Unsubscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);
            EventBus.Subscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);

            // Cache local player ID
            RefreshLocalIdentityContext();

            // Subscribe to lifecycle events and bootstrap existing players.
            RegisterNetworkCallbacks();
            RegisterPlayerLifecycleCallbacks();
            BootstrapTrackedPlayers();
        }

        private void Update() {
            var now = Time.unscaledTime;

            if(!(now >= _nextLocalIdentityRefreshTime)) return;
            RefreshLocalIdentityContext();
            _nextLocalIdentityRefreshTime = now + LocalIdentityRefreshIntervalSeconds;
        }

        private void RegisterNetworkCallbacks() {
            if(_networkCallbacksRegistered) return;

            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;

            networkManager.OnClientConnectedCallback += OnClientConnected;
            networkManager.OnClientDisconnectCallback += OnClientDisconnected;
            _networkCallbacksRegistered = true;
        }

        private void UnregisterNetworkCallbacks() {
            if(_networkCallbacksRegistered == false) return;

            var networkManager = NetworkManager.Singleton;
            if(networkManager != null) {
                networkManager.OnClientConnectedCallback -= OnClientConnected;
                networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            _networkCallbacksRegistered = false;
        }

        private void RegisterPlayerLifecycleCallbacks() {
            if(_playerLifecycleCallbacksRegistered) return;
            Player.PlayerController.PlayerSpawned += OnPlayerSpawned;
            Player.PlayerController.PlayerDespawned += OnPlayerDespawned;
            _playerLifecycleCallbacksRegistered = true;
        }

        private void UnregisterPlayerLifecycleCallbacks() {
            if(_playerLifecycleCallbacksRegistered == false) return;
            Player.PlayerController.PlayerSpawned -= OnPlayerSpawned;
            Player.PlayerController.PlayerDespawned -= OnPlayerDespawned;
            _playerLifecycleCallbacksRegistered = false;
        }

        private void OnClientConnected(ulong clientId) {
            TryTrackConnectedClient(clientId);
        }

        private void OnClientDisconnected(ulong clientId) {
            UnsubscribeClient(clientId);
        }

        private void OnPlayerSpawned(Player.PlayerController player) {
            SubscribeToRemotePtt(player);
        }

        private void OnPlayerDespawned(Player.PlayerController player) {
            if(player == null) return;
            UnsubscribeClient(player.OwnerClientId);
        }

        private void BootstrapTrackedPlayers() {
            foreach(var player in Player.PlayerController.SpawnedPlayers) {
                SubscribeToRemotePtt(player);
            }

            TrackConnectedClients();
        }

        private void TrackConnectedClients() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;

            foreach(var clientId in networkManager.ConnectedClientsIds) {
                TryTrackConnectedClient(clientId);
            }
        }

        private void TryTrackConnectedClient(ulong clientId) {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return;

            if(networkManager.ConnectedClients.TryGetValue(clientId, out var client) == false) {
                UnsubscribeClient(clientId);
                return;
            }

            var playerObject = client.PlayerObject;
            if(playerObject == null) return;
            if(playerObject.TryGetComponent<Player.PlayerController>(out var player) == false) return;
            SubscribeToRemotePtt(player);
        }

        protected override void OnCleanup() {
            if(VoiceManager.Instance != null) {
                VoiceManager.Instance.OnParticipantSpeechDetected -= OnSpeechDetected;
                VoiceManager.Instance.OnParticipantRemoved -= OnParticipantRemoved;
                VoiceManager.Instance.OnLocalPttStateChanged -= OnLocalPttStateChanged;
            }

            EventBus.Unsubscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);
            UnregisterPlayerLifecycleCallbacks();
            UnregisterNetworkCallbacks();
            foreach(var clientId in new List<ulong>(_trackedPlayers.Keys)) {
                UnsubscribeClient(clientId);
            }

            _trackedPlayers.Clear();
            _pttHandlers.Clear();
            _participantToCanonicalId.Clear();
            _participantMappingsToRemove.Clear();
            _localIdentityAliases.Clear();
            _localCanonicalId = null;
        }

        /// <summary>
        /// Called when a player's mute status changes. Remove their UI if they're now muted.
        /// </summary>
        private void OnPlayerMuteChanged(string playerId, bool isMuted) {
            if(isMuted) {
                RemoveSpeakerEntry(playerId);
            }
        }

        private void OnPlayerMuteChangedEvent(PlayerMuteChangedEvent evt) {
            if(evt == null) return;
            OnPlayerMuteChanged(evt.PlayerId, evt.IsMuted);
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "voice-overlay-container", typeof(VisualElement) }
            };
        }

        /// <summary>
        /// Called when a remote player's isPttActive NetworkVariable changes.
        /// </summary>
        private void OnRemotePttChanged(Player.PlayerController player, bool isActive) {
            if(_overlayContainer == null || player == null) return;

            var canonicalId = GetCanonicalIdentityForPlayer(player);
            if(string.IsNullOrEmpty(canonicalId)) return;

            // Skip local player (handled by OnLocalPttStateChanged)
            if(IsLocalIdentity(canonicalId)) return;

            // Skip muted players
            if(SocialSettings.IsMuted(canonicalId)) return;

            if(isActive) {
                if(!_activeSpeakerElements.ContainsKey(canonicalId)) {
                    CreateSpeakerEntry(player.PlayerName.Value.ToString(), player.steamId.Value, canonicalId);
                }
            } else {
                RemoveSpeakerEntry(canonicalId);
            }
        }

        /// <summary>
        /// Called when local PTT state changes. Shows/hides local player's indicator immediately.
        /// </summary>
        private void OnLocalPttStateChanged(bool isActive) {
            if(_overlayContainer == null) return;
            RefreshLocalIdentityContext();
            if(string.IsNullOrEmpty(_localCanonicalId)) return;

            if(isActive) {
                if(_activeSpeakerElements.ContainsKey(_localCanonicalId)) return;
                var displayName = SteamClient.Name;
                var steamId = SteamClient.SteamId.Value;
                CreateSpeakerEntry(displayName, steamId, _localCanonicalId);
            } else {
                RemoveSpeakerEntry(_localCanonicalId);
            }
        }

        /// <summary>
        /// Called when any participant's speech detection changes.
        /// For Open Mic mode, this controls the local indicator.
        /// For remote players using Open Mic, this also shows their indicator.
        /// </summary>
        private void OnSpeechDetected(VivoxParticipant participant) {
            if(participant == null || _overlayContainer == null) return;

            var rawParticipantId = participant.PlayerId;
            if(string.IsNullOrEmpty(rawParticipantId)) return;

            RefreshLocalIdentityContext();
            var canonicalId = ResolveCanonicalIdentity(rawParticipantId, out var resolvedPlayer);
            if(string.IsNullOrEmpty(canonicalId)) return;

            var isSpeaking = participant.SpeechDetected;

            // For local player in PTT mode, ignore speech detection (handled by OnLocalPttStateChanged)
            var isLocalPlayer = IsLocalIdentity(rawParticipantId) || IsLocalIdentity(canonicalId);
            switch(isLocalPlayer) {
                case true when SocialSettings.InputMode == VoiceInputMode.PushToTalk:
                // Skip muted players
                case false when SocialSettings.IsMuted(canonicalId):
                    return;
                // For remote players, check if they're using PTT (via NetworkVariable) - if so, ignore speech detection
                case false: {
                    if(resolvedPlayer != null) {
                        // If their isPttActive is being used, skip speech detection UI.
                        // (Their indicator is already controlled by OnRemotePttChanged)
                        if(resolvedPlayer.isPttActive.Value) {
                            return;
                        }
                    }

                    break;
                }
            }

            if(isSpeaking) {
                if(_activeSpeakerElements.ContainsKey(canonicalId)) return;
                _participantToCanonicalId[rawParticipantId] = canonicalId;

                if(resolvedPlayer != null) {
                    CreateSpeakerEntry(resolvedPlayer.PlayerName.Value.ToString(), resolvedPlayer.steamId.Value, canonicalId);
                } else {
                    ulong.TryParse(canonicalId, out var steamId);
                    CreateSpeakerEntry(participant.DisplayName, steamId, canonicalId);
                }
            } else {
                if(_participantToCanonicalId.TryGetValue(rawParticipantId, out var mappedCanonicalId)) {
                    RemoveSpeakerEntry(mappedCanonicalId);
                    _participantToCanonicalId.Remove(rawParticipantId);
                } else {
                    RemoveSpeakerEntry(canonicalId);
                }
            }
        }

        private void OnParticipantRemoved(VivoxParticipant participant) {
            if(participant == null) return;
            var participantId = participant.PlayerId;
            if(string.IsNullOrEmpty(participantId)) return;

            if(_participantToCanonicalId.TryGetValue(participantId, out var canonicalId)) {
                RemoveSpeakerEntry(canonicalId);
                _participantToCanonicalId.Remove(participantId);
                return;
            }

            RemoveSpeakerEntry(ResolveCanonicalIdentity(participantId, out _));
        }

        private void CreateSpeakerEntry(string displayName, ulong steamId, string id) {
            var entry = new VisualElement();
            entry.AddToClassList("voice-entry");

            var avatar = new VisualElement();
            avatar.AddToClassList("voice-entry-avatar");

            if(steamId != 0) {
                LoadAvatar(steamId, avatar).Forget();
            }

            var nameLabel = new Label();
            nameLabel.AddToClassList("voice-entry-name");
            nameLabel.text = displayName;

            entry.Add(avatar);
            entry.Add(nameLabel);

            _overlayContainer.Add(entry);
            _activeSpeakerElements[id] = entry;
        }

        private void RemoveSpeakerEntry(string id) {
            if(!_activeSpeakerElements.TryGetValue(id, out var entry)) return;
            _overlayContainer.Remove(entry);
            _activeSpeakerElements.Remove(id);
        }

        private void SubscribeToRemotePtt(Player.PlayerController player) {
            if(player == null || !player.IsSpawned) return;

            var clientId = player.OwnerClientId;
            if(_trackedPlayers.TryGetValue(clientId, out var existing)) {
                if(existing == player) return;
                UnsubscribeClient(clientId);
            }

            NetworkVariable<bool>.OnValueChangedDelegate handler = (_, curr) => OnRemotePttChanged(player, curr);
            player.isPttActive.OnValueChanged += handler;
            _trackedPlayers[clientId] = player;
            _pttHandlers[clientId] = handler;
            OnRemotePttChanged(player, player.isPttActive.Value);
        }

        private void UnsubscribeClient(ulong clientId) {
            var canonicalId = _trackedPlayers.TryGetValue(clientId, out var player)
                ? GetCanonicalIdentityForPlayer(player)
                : null;

            if(player != null && _pttHandlers.TryGetValue(clientId, out var handler)) {
                player.isPttActive.OnValueChanged -= handler;
            }

            _trackedPlayers.Remove(clientId);
            _pttHandlers.Remove(clientId);

            if(string.IsNullOrEmpty(canonicalId)) return;
            RemoveSpeakerEntry(canonicalId);
            RemoveParticipantMappingsForCanonicalId(canonicalId);
        }

        private void RemoveParticipantMappingsForCanonicalId(string canonicalId) {
            _participantMappingsToRemove.Clear();

            foreach(var (participantId, participantCanonicalId) in _participantToCanonicalId) {
                if(string.Equals(participantCanonicalId, canonicalId, StringComparison.Ordinal)) {
                    _participantMappingsToRemove.Add(participantId);
                }
            }

            foreach(var participantId in _participantMappingsToRemove) {
                _participantToCanonicalId.Remove(participantId);
            }

            _participantMappingsToRemove.Clear();
        }

        private void RefreshLocalIdentityContext() {
            _localIdentityAliases.Clear();
            _localCanonicalId = null;

            if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                var steamId = SteamClient.SteamId.ToString();
                if(string.IsNullOrEmpty(steamId) == false) {
                    _localIdentityAliases.Add(steamId);
                    _localCanonicalId = steamId;
                }
            }

            var localPlayer = Player.PlayerController.LocalPlayer;
            if(localPlayer != null) {
                var localSteamId = localPlayer.steamId.Value;
                if(localSteamId != 0) {
                    var localSteamIdString = localSteamId.ToString();
                    _localIdentityAliases.Add(localSteamIdString);
                    _localCanonicalId ??= localSteamIdString;
                }

                var localUgsId = localPlayer.ugsId.Value.ToString();
                if(string.IsNullOrEmpty(localUgsId) == false) {
                    _localIdentityAliases.Add(localUgsId);
                    _localCanonicalId ??= localUgsId;
                }
            }

            if(VoiceManager.Instance == null) return;
            var voiceIdentity = VoiceManager.Instance.LoggedInIdentity;
            if(string.IsNullOrEmpty(voiceIdentity)) return;
            _localIdentityAliases.Add(voiceIdentity);
            _localCanonicalId ??= voiceIdentity;
        }

        private bool IsLocalIdentity(string identity) {
            return !string.IsNullOrEmpty(identity) && _localIdentityAliases.Contains(identity);
        }

        private string ResolveCanonicalIdentity(string rawIdentity, out Player.PlayerController resolvedPlayer) {
            resolvedPlayer = null;
            if(string.IsNullOrEmpty(rawIdentity)) return rawIdentity;

            if(IsLocalIdentity(rawIdentity)) {
                return string.IsNullOrEmpty(_localCanonicalId) ? rawIdentity : _localCanonicalId;
            }

            if(TryResolveTrackedPlayer(rawIdentity, out resolvedPlayer, out var canonicalId)) {
                return canonicalId;
            }

            // Opportunistically track from connected clients if the first pass misses.
            TrackConnectedClients();
            return TryResolveTrackedPlayer(rawIdentity, out resolvedPlayer, out canonicalId) ? canonicalId : rawIdentity;
        }

        private bool TryResolveTrackedPlayer(string rawIdentity, out Player.PlayerController resolvedPlayer,
            out string canonicalId) {
            resolvedPlayer = null;
            canonicalId = null;

            foreach(var player in _trackedPlayers.Values) {
                if(player == null || player.IsSpawned == false) continue;

                var steamId = player.steamId.Value;
                var steamIdString = steamId != 0 ? steamId.ToString() : null;
                var ugsIdString = player.ugsId.Value.ToString();

                if((string.IsNullOrEmpty(steamIdString) ||
                    !string.Equals(steamIdString, rawIdentity, StringComparison.Ordinal)) &&
                   (string.IsNullOrEmpty(ugsIdString) ||
                    !string.Equals(ugsIdString, rawIdentity, StringComparison.Ordinal))) continue;
                resolvedPlayer = player;
                canonicalId = GetCanonicalIdentityForPlayer(player);
                return true;
            }

            return false;
        }

        private static string GetCanonicalIdentityForPlayer(Player.PlayerController player) {
            if(player == null) return null;

            if(player.steamId.Value != 0) {
                return player.steamId.Value.ToString();
            }

            var ugsId = player.ugsId.Value.ToString();
            return string.IsNullOrEmpty(ugsId) ? null : ugsId;
        }

        private static async UniTaskVoid LoadAvatar(ulong steamId, VisualElement avatarElement) {
            if(avatarElement == null || steamId == 0) return;
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;
            if(SteamManager.Instance == null) return;

            var texture = await SteamManager.Instance.GetAvatarAsync(steamId);
            if(texture != null) {
                avatarElement.style.backgroundImage = new StyleBackground(texture);
            }
        }
    }
}
