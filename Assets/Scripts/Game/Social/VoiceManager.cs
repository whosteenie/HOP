using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Vivox;
using Steamworks;

namespace Game.Social {
    public class VoiceManager : MonoBehaviour {
        public static VoiceManager Instance { get; private set; }

        private bool IsInitialized { get; set; }
        public bool IsLoggedIn { get; private set; }
        
        // State
        private bool _isMicOpen;
        private bool _isPttActive;
        private string _currentChannelName;
        private readonly Dictionary<string, Action> _participantSpeechActions = new();
        
        // Events
        public event Action<VivoxParticipant> OnParticipantSpeechDetected;
        public event Action<VivoxParticipant> OnParticipantAdded;
        public event Action<VivoxParticipant> OnParticipantRemoved;
        public event Action<bool> OnLocalPttStateChanged; // Fires when local PTT state changes

        public void SetPttActive(bool active) {
            _isPttActive = active;
        }

        private void Awake() {
            if (Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start() {
            try {
                await InitializeVivoxAsync();
            
                // Listen for SocialSettings changes
                SocialSettings.OnSettingsChanged += ApplySettings;
                SocialSettings.OnPlayerMuteChanged += OnPlayerMuteChanged;
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnDestroy() {
            SocialSettings.OnSettingsChanged -= ApplySettings;
            SocialSettings.OnPlayerMuteChanged -= OnPlayerMuteChanged;
            if(VivoxService.Instance == null) return;
            VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAddedToChannel;
            VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemovedFromChannel;
        }

        private async Task InitializeVivoxAsync() {
            try {
                await UnityServices.InitializeAsync();
                await VivoxService.Instance.InitializeAsync();
                
                IsInitialized = true;

                // Subscribe to service events
                VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAddedToChannel;
                VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemovedFromChannel;

                // Login automatically if we have a user
                if(SteamClient.IsValid) {
                    await LoginAsync(SteamClient.SteamId.ToString(), SteamClient.Name);
                }

            } catch (Exception e) {
                Debug.LogError($"[VoiceManager] Initialization Failed: {e.Message}");
            }
        }

        private async Task LoginAsync(string uniqueId, string displayName) {
            if(!IsInitialized) return;
            
            try {
                var options = new LoginOptions {
                    DisplayName = displayName,
                    PlayerId = uniqueId, // Crucial fix
                    ParticipantUpdateFrequency = ParticipantPropertyUpdateFrequency.FivePerSecond
                };
                await VivoxService.Instance.LoginAsync(options);
                IsLoggedIn = true;
                
                ApplySettings(); // Apply initial volume/settings
                await ApplySavedMicSettingsAsync();
                
                // Check if we're already in a lobby - join voice channel now
                if (Network.SessionManager.Instance != null && Network.SessionManager.Instance.CurrentLobby.HasValue) {
                    var lobbyId = Network.SessionManager.Instance.CurrentLobby.Value.Id;
                    await JoinChannelAsync("match_" + lobbyId);
                }

            } catch (Exception e) {
                Debug.LogError($"[VoiceManager] Login Failed! Check if Test Mode is enabled in Project Settings > Services > Vivox. Exception: {e.Message}");
            }
        }

        public async Task JoinChannelAsync(string channelName, bool positional = true) {
            if(!IsLoggedIn) {
                Debug.LogWarning("[VoiceManager] JoinChannelAsync called before login!");
                return;
            }
            
            // Leave old channel if needed
            if(!string.IsNullOrEmpty(_currentChannelName)) {
                Debug.Log($"[VoiceManager] Leaving old channel '{_currentChannelName}' before joining '{channelName}'");
                await VivoxService.Instance.LeaveChannelAsync(_currentChannelName);
            }

            try {
                _currentChannelName = channelName;
                
                if (positional) {
                    // 3D Positional Channel
                    var channel3D = new Channel3DProperties(32, 1, 1.0f, AudioFadeModel.InverseByDistance);
                    Debug.Log($"[VoiceManager] Joining positional channel: {channelName}");
                    await VivoxService.Instance.JoinPositionalChannelAsync(channelName, ChatCapability.AudioOnly, channel3D);
                } else {
                    // 2D Team Channel
                    Debug.Log($"[VoiceManager] Joining group channel: {channelName}");
                    await VivoxService.Instance.JoinGroupChannelAsync(channelName, ChatCapability.AudioOnly);
                }
                
                Debug.Log($"[VoiceManager] Successfully joined channel: {channelName}. ActiveChannels Count: {VivoxService.Instance.ActiveChannels.Count}");

            } catch (Exception e) {
                Debug.LogError($"[VoiceManager] Join Channel Failed: {e.Message}");
            }
        }
        
        public async Task LeaveChannelAsync() {
             if(!IsLoggedIn || string.IsNullOrEmpty(_currentChannelName)) return;
             Debug.Log($"[VoiceManager] Leaving channel: {_currentChannelName}");
             await VivoxService.Instance.LeaveChannelAsync(_currentChannelName);
             _currentChannelName = null;
             Debug.Log("[VoiceManager] Channel left.");
        }

        private void Update() {
            if(!IsLoggedIn) return;

            HandleInput();
            UpdatePosition();
        }

        private void HandleInput() {
            var shouldBeOpen = SocialSettings.InputMode switch {
                VoiceInputMode.OpenMic => true,
                VoiceInputMode.PushToTalk => _isPttActive,
                _ => false
            };

            // State change check
            if(shouldBeOpen == _isMicOpen) return;
            _isMicOpen = shouldBeOpen;
            
            // Fire event for local UI
            if (SocialSettings.InputMode == VoiceInputMode.PushToTalk) {
                OnLocalPttStateChanged?.Invoke(_isMicOpen);
                
                // Update NetworkVariable on local PlayerController so other clients see the indicator
                var localPlayer = Game.Player.PlayerController.LocalPlayer;
                if (localPlayer != null) {
                    localPlayer.isPttActive.Value = _isMicOpen;
                }
            }
            
            if (_isMicOpen) {
                VivoxService.Instance.UnmuteInputDevice();
            } else {
                VivoxService.Instance.MuteInputDevice();
            }
        }

        private void UpdatePosition() {
            if(!IsLoggedIn || string.IsNullOrEmpty(_currentChannelName)) return;

            // Verify channel is actually joined before calling Set3DPosition
            if(!VivoxService.Instance.ActiveChannels.ContainsKey(_currentChannelName)) return;

            // Update 3D position if in a positional channel (assuming Camera/Player position)
            // TODO: Get actual player head transform
            if(Camera.main == null) return;
            var cameraTransform = Camera.main.transform;
            VivoxService.Instance.Set3DPosition(cameraTransform.position, Vector3.zero, cameraTransform.forward, cameraTransform.up, _currentChannelName);
        }

        private void ApplySettings() {
            if(!IsLoggedIn) return;

            // Output Volume (0-100)
            VivoxService.Instance.SetOutputDeviceVolume((int)(SocialSettings.VoiceVolume * 100));
            
            // Input Volume (0-100)
            VivoxService.Instance.SetInputDeviceVolume((int)(SocialSettings.VoiceInputVolume * 100));
        }
        
        private void OnPlayerMuteChanged(string playerId, bool isMuted) {
            MuteUser(playerId, isMuted);
        }

        private async Task ApplySavedMicSettingsAsync() {
            if (!IsLoggedIn || VivoxService.Instance == null) return;
            await SetActiveMicAsync(SocialSettings.InputDevice);
        }

        public async Task SetActiveMicAsync(string deviceName) {
            if (!IsLoggedIn || VivoxService.Instance == null) return;

            var devices = VivoxService.Instance.AvailableInputDevices;
            var target = devices.FirstOrDefault(d => d.DeviceName == deviceName);
            
            if (target != null) {
                Debug.Log($"[VoiceManager] Setting active mic to: {deviceName}");
                await VivoxService.Instance.SetActiveInputDeviceAsync(target);
            } else if (deviceName == "Default") {
                Debug.Log("[VoiceManager] Setting active mic to system default.");
                // By default, Vivox uses the system default if no specific device is set.
                // We can explicitly unset if needed, or just let it be. 
                // However, most SDKs prefer we just don't call SetActive if we want system default.
            } else {
                Debug.LogWarning($"[VoiceManager] Requested mic '{deviceName}' not found. Falling back to default.");
            }
        }

        public List<string> GetAvailableInputDevices() {
            return VivoxService.Instance == null ? new List<string> { "Default" } : 
                VivoxService.Instance.AvailableInputDevices.Select(d => d.DeviceName).ToList();
        }

        public void MuteUser(string userId, bool muted) {
             if(!IsLoggedIn) return;
             
             // Vivox Mute
             if(muted) {
                 VivoxService.Instance.BlockPlayerAsync(userId); // Block stops audio
             } else {
                 VivoxService.Instance.UnblockPlayerAsync(userId);
             }
        }

        public bool IsSpeaking(string playerId) {
            if (!IsLoggedIn || VivoxService.Instance == null) return false;
            
            // Iterate over channels to find participant
            foreach (var channel in VivoxService.Instance.ActiveChannels) {
                if (channel.Value == null) continue;
                
                foreach(var participant in channel.Value) {
                    if(participant.PlayerId == playerId) {
                        return participant.SpeechDetected;
                    }
                }
            }
            
            return false;
        }

        private void OnParticipantAddedToChannel(VivoxParticipant participant) {
            // Subscribe to participant-level events
            Action speechAction = () => OnSpeechDetected(participant);
            _participantSpeechActions[participant.PlayerId] = speechAction;
            
            participant.ParticipantSpeechDetected += speechAction;
            OnParticipantAdded?.Invoke(participant);
            
            // Apply mute if this player is in the muted list
            if (SocialSettings.IsMuted(participant.PlayerId)) {
                MuteUser(participant.PlayerId, true);
            }
        }

        private void OnParticipantRemovedFromChannel(VivoxParticipant participant) {
            if (_participantSpeechActions.TryGetValue(participant.PlayerId, out var action)) {
                participant.ParticipantSpeechDetected -= action;
                _participantSpeechActions.Remove(participant.PlayerId);
            }
            OnParticipantRemoved?.Invoke(participant);
        }

        private void OnSpeechDetected(VivoxParticipant participant) {
            OnParticipantSpeechDetected?.Invoke(participant);
        }
    }
}
