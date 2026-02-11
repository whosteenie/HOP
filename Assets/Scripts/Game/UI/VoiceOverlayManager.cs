using System;
using System.Collections.Generic;
using Game.Social;
using UnityEngine;
using UnityEngine.UIElements;
using Cysharp.Threading.Tasks;
using Steamworks;
using Unity.Services.Vivox;

namespace Game.UI {
    public class VoiceOverlayManager : UIElementBase {
        private VisualElement _overlayContainer;

        // State
        private readonly Dictionary<string, VisualElement> _activeSpeakerElements = new(); // SteamId string -> Element
        private readonly Dictionary<ulong, Player.PlayerController> _trackedPlayers = new();
        private string _localPlayerId;

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
            if(SteamClient.IsValid) {
                _localPlayerId = SteamClient.SteamId.ToString();
            }

            // Subscribe to existing players
            SubscribeToAllPlayers();
        }

        private void Update() {
            // Check for new players and subscribe to their isPttActive
            var allPlayers = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
            foreach(var player in allPlayers) {
                if(player != null && player.steamId.Value != 0 &&
                   _trackedPlayers.TryAdd(player.steamId.Value, player)) {
                    player.isPttActive.OnValueChanged += (_, curr) => OnRemotePttChanged(player, curr);
                }
            }
        }

        private void SubscribeToAllPlayers() {
            var allPlayers = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
            foreach(var player in allPlayers) {
                if(player == null || player.steamId.Value == 0) continue;
                _trackedPlayers[player.steamId.Value] = player;
                player.isPttActive.OnValueChanged += (_, curr) => OnRemotePttChanged(player, curr);
            }
        }

        protected override void OnCleanup() {
            if(VoiceManager.Instance != null) {
                VoiceManager.Instance.OnParticipantSpeechDetected -= OnSpeechDetected;
                VoiceManager.Instance.OnParticipantRemoved -= OnParticipantRemoved;
                VoiceManager.Instance.OnLocalPttStateChanged -= OnLocalPttStateChanged;
            }

            SocialSettings.OnPlayerMuteChanged -= OnPlayerMuteChanged;
            _trackedPlayers.Clear();
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

            var idStr = player.steamId.Value.ToString();

            // Skip local player (handled by OnLocalPttStateChanged)
            if(idStr == _localPlayerId) return;

            // Skip muted players
            if(SocialSettings.IsMuted(idStr)) return;

            if(isActive) {
                if(!_activeSpeakerElements.ContainsKey(idStr)) {
                    CreateSpeakerEntry(player.PlayerName.Value.ToString(), player.steamId.Value, idStr);
                }
            } else {
                RemoveSpeakerEntry(idStr);
            }
        }

        /// <summary>
        /// Called when local PTT state changes. Shows/hides local player's indicator immediately.
        /// </summary>
        private void OnLocalPttStateChanged(bool isActive) {
            if(_overlayContainer == null || string.IsNullOrEmpty(_localPlayerId)) return;

            if(isActive) {
                if(_activeSpeakerElements.ContainsKey(_localPlayerId)) return;
                var displayName = SteamClient.Name;
                var steamId = SteamClient.SteamId.Value;
                CreateSpeakerEntry(displayName, steamId, _localPlayerId);
            } else {
                RemoveSpeakerEntry(_localPlayerId);
            }
        }

        /// <summary>
        /// Called when any participant's speech detection changes.
        /// For Open Mic mode, this controls the local indicator.
        /// For remote players using Open Mic, this also shows their indicator.
        /// </summary>
        private void OnSpeechDetected(VivoxParticipant participant) {
            if(participant == null || _overlayContainer == null) return;

            var idStr = participant.PlayerId;
            var isSpeaking = participant.SpeechDetected;

            // For local player in PTT mode, ignore speech detection (handled by OnLocalPttStateChanged)
            var isLocalPlayer = idStr == _localPlayerId;
            switch(isLocalPlayer) {
                case true when SocialSettings.InputMode == VoiceInputMode.PushToTalk:
                // Skip muted players
                case false when SocialSettings.IsMuted(idStr):
                    return;
                // For remote players, check if they're using PTT (via NetworkVariable) - if so, ignore speech detection
                case false: {
                    // Find their PlayerController to check isPttActive
                    var players = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
                    foreach(var player in players) {
                        if(player == null || player.steamId.Value.ToString() != idStr) continue;
                        // If their isPttActive is being used, skip speech detection UI
                        // (Their indicator is already controlled by OnRemotePttChanged)
                        if(player.isPttActive.Value || _activeSpeakerElements.ContainsKey(idStr)) {
                            return;
                        }

                        break;
                    }

                    break;
                }
            }

            if(isSpeaking) {
                if(_activeSpeakerElements.ContainsKey(idStr)) return;
                var players = FindObjectsByType<Player.PlayerController>(FindObjectsSortMode.None);
                Player.PlayerController foundPlayer = null;
                foreach(var player in players) {
                    if(player == null || player.steamId.Value.ToString() != idStr) continue;
                    foundPlayer = player;
                    break;
                }

                if(foundPlayer != null) {
                    CreateSpeakerEntry(foundPlayer.PlayerName.Value.ToString(), foundPlayer.steamId.Value, idStr);
                } else {
                    ulong.TryParse(idStr, out var steamId);
                    CreateSpeakerEntry(participant.DisplayName, steamId, idStr);
                }
            } else {
                RemoveSpeakerEntry(idStr);
            }
        }

        private void OnParticipantRemoved(VivoxParticipant participant) {
            if(participant != null) {
                RemoveSpeakerEntry(participant.PlayerId);
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