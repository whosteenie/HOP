using System;
using System.Collections.Generic;
using Game.Social;
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
        private readonly HashSet<string> _localIdentityAliases = new(StringComparer.Ordinal);
        private string _localCanonicalId;

        protected override void OnInitialize() {
            _overlayContainer = QOptional<VisualElement>("voice-overlay-container");

            if(VoiceManager.Instance != null) {
                VoiceManager.Instance.OnParticipantSpeechDetected += OnSpeechDetected;
                VoiceManager.Instance.OnParticipantRemoved += OnParticipantRemoved;
                VoiceManager.Instance.OnLocalPttStateChanged += OnLocalPttStateChanged;
            }

            // Listen for mute changes to update UI
            SocialSettings.OnPlayerMuteChanged += OnPlayerMuteChanged;

            // Cache local player ID
            RefreshLocalIdentityContext();

            // Subscribe to existing players
            SubscribeToAllPlayers();
        }

        private void Update() {
            RefreshLocalIdentityContext();

            // Check for new players and subscribe to their isPttActive
            var allPlayers = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
            foreach(var player in allPlayers) {
                SubscribeToRemotePtt(player);
            }
        }

        private void SubscribeToAllPlayers() {
            var allPlayers = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
            foreach(var player in allPlayers) {
                SubscribeToRemotePtt(player);
            }
        }

        protected override void OnCleanup() {
            if(VoiceManager.Instance != null) {
                VoiceManager.Instance.OnParticipantSpeechDetected -= OnSpeechDetected;
                VoiceManager.Instance.OnParticipantRemoved -= OnParticipantRemoved;
                VoiceManager.Instance.OnLocalPttStateChanged -= OnLocalPttStateChanged;
            }

            SocialSettings.OnPlayerMuteChanged -= OnPlayerMuteChanged;
            foreach(var kvp in _trackedPlayers) {
                var clientId = kvp.Key;
                var player = kvp.Value;
                if(player == null) continue;
                if(_pttHandlers.TryGetValue(clientId, out var handler)) {
                    player.isPttActive.OnValueChanged -= handler;
                }
            }

            _trackedPlayers.Clear();
            _pttHandlers.Clear();
            _participantToCanonicalId.Clear();
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
            if(participant != null) {
                var participantId = participant.PlayerId;
                if(string.IsNullOrEmpty(participantId)) return;

                if(_participantToCanonicalId.TryGetValue(participantId, out var canonicalId)) {
                    RemoveSpeakerEntry(canonicalId);
                    _participantToCanonicalId.Remove(participantId);
                    return;
                }

                RemoveSpeakerEntry(ResolveCanonicalIdentity(participantId, out _));
            }
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
            if(_trackedPlayers.ContainsKey(clientId)) return;

            NetworkVariable<bool>.OnValueChangedDelegate handler = (_, curr) => OnRemotePttChanged(player, curr);
            player.isPttActive.OnValueChanged += handler;
            _trackedPlayers[clientId] = player;
            _pttHandlers[clientId] = handler;
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

            if(VoiceManager.Instance != null) {
                var voiceIdentity = VoiceManager.Instance.LoggedInIdentity;
                if(string.IsNullOrEmpty(voiceIdentity) == false) {
                    _localIdentityAliases.Add(voiceIdentity);
                    _localCanonicalId ??= voiceIdentity;
                }
            }
        }

        private bool IsLocalIdentity(string identity) {
            if(string.IsNullOrEmpty(identity)) return false;
            return _localIdentityAliases.Contains(identity);
        }

        private string ResolveCanonicalIdentity(string rawIdentity, out Player.PlayerController resolvedPlayer) {
            resolvedPlayer = null;
            if(string.IsNullOrEmpty(rawIdentity)) return rawIdentity;

            if(IsLocalIdentity(rawIdentity)) {
                return string.IsNullOrEmpty(_localCanonicalId) ? rawIdentity : _localCanonicalId;
            }

            var players = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
            foreach(var player in players) {
                if(player == null) continue;

                var steamId = player.steamId.Value;
                var steamIdString = steamId != 0 ? steamId.ToString() : null;
                var ugsIdString = player.ugsId.Value.ToString();

                if((string.IsNullOrEmpty(steamIdString) == false && string.Equals(steamIdString, rawIdentity, StringComparison.Ordinal)) ||
                   (string.IsNullOrEmpty(ugsIdString) == false && string.Equals(ugsIdString, rawIdentity, StringComparison.Ordinal))) {
                    resolvedPlayer = player;
                    return GetCanonicalIdentityForPlayer(player);
                }
            }

            return rawIdentity;
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
            var image = await SteamFriends.GetLargeAvatarAsync(steamId);
            if(image.HasValue) {
                var width = (int)image.Value.Width;
                var height = (int)image.Value.Height;
                var data = image.Value.Data;

                // Flip the image data (Steam returns it top-down, Unity UI expects bottom-up for LoadRawTextureData)
                var flippedData = new byte[data.Length];
                var stride = width * 4; // RGBA32
                for(var y = 0; y < height; y++) {
                    Array.Copy(data, y * stride, flippedData, (height - 1 - y) * stride, stride);
                }

                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.LoadRawTextureData(flippedData);
                texture.Apply();
                if(avatarElement != null) avatarElement.style.backgroundImage = new StyleBackground(texture);
            }
        }
    }
}
