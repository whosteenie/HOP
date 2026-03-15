using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Game.Player.Core;
using Network.Events;
using Network.Session;
using UnityEngine;
using Unity.Services.Authentication;
using Unity.Services.Vivox;
using Steamworks;
using Network.UGS;

namespace Game.Social {
    public class VoiceManager : MonoBehaviour {
        public static VoiceManager Instance { get; private set; }

        private bool IsInitialized { get; set; }
        public bool IsLoggedIn { get; private set; }
        public string LoggedInIdentity { get; private set; }

        // State
        private bool _isMicOpen;
        private bool _isPttActive;
        private string _currentChannelName;
        private readonly Dictionary<string, Action> _participantSpeechActions = new();
        private readonly SemaphoreSlim _channelOperationGate = new(1, 1);
        private float _nextClaimsMismatchLogTime;
        private float _nextJoinFailureLogTime;
        private float _claimsMismatchRetryCooldownUntil;
        private float _nextChannelRouteSyncTime;
        private float _nextRouteSyncLogTime;
        private bool _isRouteSyncInProgress;
        private const int MaxJoinAttempts = 3;
        private const int ClaimsMismatchRetryDelayMs = 350;
        private const int GenericJoinRetryDelayMs = 500;
        private const float ClaimsMismatchRetryCooldownSeconds = 5f;
        private const float ChannelRouteSyncIntervalSeconds = 1.5f;
        
        public bool TryGetJoinedChannelName(out string channelName) {
            channelName = null;
            if(!IsLoggedIn || VivoxService.Instance == null) return false;
            if(string.IsNullOrEmpty(_currentChannelName)) return false;
            if(IsChannelActive(_currentChannelName) == false) return false;
            channelName = _currentChannelName;
            return true;
        }

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
                EventBus.Unsubscribe<SocialSettingsChangedEvent>(OnSocialSettingsChanged);
                EventBus.Unsubscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);
                EventBus.Subscribe<SocialSettingsChangedEvent>(OnSocialSettingsChanged);
                EventBus.Subscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        private void OnDestroy() {
            EventBus.Unsubscribe<SocialSettingsChangedEvent>(OnSocialSettingsChanged);
            EventBus.Unsubscribe<PlayerMuteChangedEvent>(OnPlayerMuteChangedEvent);
            if(VivoxService.Instance == null) return;
            VivoxService.Instance.ParticipantAddedToChannel -= OnParticipantAddedToChannel;
            VivoxService.Instance.ParticipantRemovedFromChannel -= OnParticipantRemovedFromChannel;
        }

        private async Task InitializeVivoxAsync() {
            try {
                await UgsAuthService.InitAndSignInAsync();

                // Must be registered before initializing Vivox when Test Mode is disabled and no client signing key is present.
                VivoxService.Instance.SetTokenProvider(new VivoxCloudCodeTokenProvider());
                await VivoxService.Instance.InitializeAsync();
                
                IsInitialized = true;

                // Subscribe to service events
                VivoxService.Instance.ParticipantAddedToChannel += OnParticipantAddedToChannel;
                VivoxService.Instance.ParticipantRemovedFromChannel += OnParticipantRemovedFromChannel;

                // Login automatically if we have a user
                await EnsureLoggedInForIdentityAsync();

            } catch (Exception e) {
                Debug.LogError($"[VoiceManager] Initialization Failed: {e.Message}");
            }
        }

        private static bool IsVivoxClaimsMismatch(Exception ex) {
            if(ex == null || string.IsNullOrEmpty(ex.Message)) return false;
            return ex.Message.IndexOf("claims mismatch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ShouldEmitThrottledLog(ref float nextLogTime, float intervalSeconds) {
            var now = Time.unscaledTime;
            if(now < nextLogTime) return false;
            nextLogTime = now + intervalSeconds;
            return true;
        }

        private static string ResolvePreferredIdentity() {
            if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                return SteamClient.SteamId.ToString();
            }

            var ugsId = AuthenticationService.Instance.PlayerId;
            return string.IsNullOrEmpty(ugsId) ? null : ugsId;
        }

        private static string ResolvePreferredDisplayName() {
            return StreamerMode.GetLocalDisplayName();
        }

        private async Task<bool> EnsureLoggedInForIdentityAsync(bool forceRelogin = false) {
            if(!IsInitialized || VivoxService.Instance == null) return false;

            var identity = ResolvePreferredIdentity();
            if(string.IsNullOrEmpty(identity)) return false;

            if(!forceRelogin && IsLoggedIn && string.Equals(LoggedInIdentity, identity, StringComparison.Ordinal)) {
                return true;
            }

            if(VivoxService.Instance.IsLoggedIn) {
                try {
                    await LeaveCurrentChannelAsync("EnsureLoggedInForIdentity");
                } catch {
                    // Continue with logout even if channel leave reports stale state.
                }
                try {
                    await VivoxService.Instance.LogoutAsync();
                } catch {
                    // Continue to login attempt even if logout reports stale state.
                }
            }

            IsLoggedIn = false;
            _currentChannelName = null;
            LoggedInIdentity = null;

            await LoginAsync(identity, ResolvePreferredDisplayName(), joinLobbyChannelIfPresent: false);
            return IsLoggedIn;
        }

        private async Task LeaveCurrentChannelAsync(string reason) {
            if(VivoxService.Instance == null) {
                PublishVoiceOverlayReset();
                _currentChannelName = null;
                return;
            }

            if(string.IsNullOrEmpty(_currentChannelName)) {
                PublishVoiceOverlayReset();
                return;
            }

            var channelToLeave = _currentChannelName;
            try {
                if(VivoxService.Instance.ActiveChannels.ContainsKey(channelToLeave)) {
                    Debug.Log($"[VoiceManager] Leaving channel '{channelToLeave}' ({reason})");
                    await VivoxService.Instance.LeaveChannelAsync(channelToLeave);
                }
            } catch(Exception ex) {
                Debug.LogWarning($"[VoiceManager] Leave channel '{channelToLeave}' failed ({reason}): {ex.Message}");
            } finally {
                PublishVoiceOverlayReset();
                if(_currentChannelName == channelToLeave) {
                    _currentChannelName = null;
                }
            }
        }

        private async Task LoginAsync(string uniqueId, string displayName, bool joinLobbyChannelIfPresent = true) {
            if(!IsInitialized) return;
            
            try {
                var options = new LoginOptions {
                    DisplayName = displayName,
                    PlayerId = uniqueId, // Crucial fix
                    ParticipantUpdateFrequency = ParticipantPropertyUpdateFrequency.FivePerSecond
                };
                await VivoxService.Instance.LoginAsync(options);
                IsLoggedIn = true;
                LoggedInIdentity = uniqueId;
                
                ApplySettings(); // Apply initial volume/settings
                await ApplySavedMicSettingsAsync();
                
                // Optional route sync when caller requests immediate channel join.
                if(joinLobbyChannelIfPresent &&
                   SessionManager.Instance != null &&
                   SessionManager.Instance.TryGetActiveVoiceChannelName(out var canonicalChannelName) &&
                   string.IsNullOrEmpty(canonicalChannelName) == false) {
                    await EnsureChannelJoinedAsync(canonicalChannelName, context: "LoginRouteSync");
                }

            } catch (Exception e) {
                Debug.LogError($"[VoiceManager] Login Failed! If Vivox Test Mode is disabled, ensure Cloud Code Vivox token minting is deployed and reachable. Exception: {e.Message}");
            }
        }

        private static bool IsChannelActive(string channelName) {
            if(string.IsNullOrEmpty(channelName)) return false;
            return VivoxService.Instance != null && VivoxService.Instance.ActiveChannels.ContainsKey(channelName);
        }

        private async Task<bool> RecoverFromClaimsMismatchAsync(string channelName, int attempt) {
            if(ShouldEmitThrottledLog(ref _nextClaimsMismatchLogTime, 10f)) {
                Debug.LogWarning(
                    $"[VoiceManager] Vivox claims mismatch while joining '{channelName}' (attempt {attempt}/{MaxJoinAttempts}). Re-authenticating and retrying.");
            }

            try {
                var reloggedIn = await EnsureLoggedInForIdentityAsync(forceRelogin: true);
                if(reloggedIn == false && ShouldEmitThrottledLog(ref _nextJoinFailureLogTime, 5f)) {
                    Debug.LogWarning("[VoiceManager] Vivox re-authentication failed after claims mismatch.");
                }
                return reloggedIn;
            } catch(Exception ex) {
                if(ShouldEmitThrottledLog(ref _nextJoinFailureLogTime, 5f)) {
                    Debug.LogWarning($"[VoiceManager] Vivox re-authentication threw after claims mismatch: {ex.Message}");
                }
                return false;
            }
        }

        public async Task<bool> EnsureChannelJoinedAsync(string channelName, bool positional = true, string context = "Unknown") {
            if(string.IsNullOrEmpty(channelName)) {
                return false;
            }

            if(Time.unscaledTime < _claimsMismatchRetryCooldownUntil) {
                if(!ShouldEmitThrottledLog(ref _nextClaimsMismatchLogTime, 2f)) return false;
                var remaining = _claimsMismatchRetryCooldownUntil - Time.unscaledTime;
                Debug.LogWarning(
                    $"[VoiceManager] Skipping Vivox rejoin for '{channelName}' during claims-mismatch cooldown ({remaining:0.0}s remaining).");

                return false;
            }

            await _channelOperationGate.WaitAsync();
            try {
                if(IsChannelActive(channelName)) {
                    _currentChannelName = channelName;
                    _claimsMismatchRetryCooldownUntil = 0f;
                    return true;
                }

                Exception lastException = null;
                for(var attempt = 1; attempt <= MaxJoinAttempts; attempt++) {
                    if(await EnsureLoggedInForIdentityAsync() == false) {
                        if(ShouldEmitThrottledLog(ref _nextJoinFailureLogTime, 5f)) {
                            Debug.LogWarning("[VoiceManager] JoinChannelAsync aborted because Vivox login is unavailable.");
                        }
                        return false;
                    }

                    try {
                        if(!string.IsNullOrEmpty(_currentChannelName) && _currentChannelName != channelName) {
                            await LeaveCurrentChannelAsync("SwitchChannel");
                        }

                        if(!Application.isEditor && Debug.isDebugBuild &&
                           ShouldEmitThrottledLog(ref _nextRouteSyncLogTime, 1.5f)) {
                            Debug.Log(
                                $"[HOPFLOW][VIVOX] JOIN_BEGIN context={context} channel={channelName} editor={Application.isEditor} batch={Application.isBatchMode}");
                        }

                        if (positional) {
                            // 3D Positional Channel
                            var channel3D = new Channel3DProperties(32, 1, 1.0f, AudioFadeModel.InverseByDistance);
                            await VivoxService.Instance.JoinPositionalChannelAsync(channelName, ChatCapability.TextAndAudio, channel3D);
                        } else {
                            // 2D Team Channel
                            await VivoxService.Instance.JoinGroupChannelAsync(channelName, ChatCapability.TextAndAudio);
                        }

                        _currentChannelName = channelName;
                        if(!Debug.isDebugBuild) return true;
                        Debug.Log(
                            $"[VoiceManager] Joined channel '{channelName}'. ActiveChannels={VivoxService.Instance.ActiveChannels.Count}");
                        if(!Application.isEditor) {
                            Debug.Log(
                                $"[HOPFLOW][VIVOX] JOIN_OK context={context} channel={channelName} editor={Application.isEditor} batch={Application.isBatchMode}");
                        }

                        return true;
                    } catch(Exception ex) {
                        lastException = ex;
                        if(IsVivoxClaimsMismatch(ex)) {
                            var recovered = await RecoverFromClaimsMismatchAsync(channelName, attempt);
                            if(recovered == false) {
                                break;
                            }

                            await Task.Delay(ClaimsMismatchRetryDelayMs);
                            continue;
                        }

                        if(attempt < MaxJoinAttempts) {
                            await Task.Delay(GenericJoinRetryDelayMs);
                        }
                    }
                }

                if(lastException == null || !ShouldEmitThrottledLog(ref _nextJoinFailureLogTime, 5f)) return false;
                if(IsVivoxClaimsMismatch(lastException)) {
                    _claimsMismatchRetryCooldownUntil = Time.unscaledTime + ClaimsMismatchRetryCooldownSeconds;
                    Debug.LogWarning(
                        $"[VoiceManager] Join channel '{channelName}' failed after claims-mismatch recovery attempts.");
                } else {
                    Debug.LogError($"[VoiceManager] Join Channel Failed: {lastException.Message}");
                }
                if(!Application.isEditor && Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[HOPFLOW][VIVOX] JOIN_FAIL context={context} channel={channelName} editor={Application.isEditor} batch={Application.isBatchMode}");
                }
                return false;
            } catch(Exception e) {
                if(ShouldEmitThrottledLog(ref _nextJoinFailureLogTime, 5f)) {
                    Debug.LogError($"[VoiceManager] Join Channel Failed: {e.Message}");
                }
                if(!Application.isEditor && Debug.isDebugBuild) {
                    Debug.LogWarning(
                        $"[HOPFLOW][VIVOX] JOIN_FAIL context={context} channel={channelName} editor={Application.isEditor} batch={Application.isBatchMode}");
                }

                return false;
            } finally {
                _channelOperationGate.Release();
            }
        }

        public async Task LeaveChannelAsync() {
            await _channelOperationGate.WaitAsync();
            try {
                if(!IsLoggedIn && string.IsNullOrEmpty(_currentChannelName)) return;
                await LeaveCurrentChannelAsync("LeaveChannelAsync");
                Debug.Log("[VoiceManager] Channel left.");
            } finally {
                _channelOperationGate.Release();
            }
        }

        private void Update() {
            if(!IsLoggedIn) return;

            HandleInput();
            UpdatePosition();
            ReconcileSessionChannelRoute();
        }

        private void ReconcileSessionChannelRoute() {
            if(Time.unscaledTime < _nextChannelRouteSyncTime) return;
            _nextChannelRouteSyncTime = Time.unscaledTime + ChannelRouteSyncIntervalSeconds;
            if(_isRouteSyncInProgress) return;

            if(SessionManager.Instance == null) return;
            if(!SessionManager.Instance.TryGetActiveVoiceChannelName(out var canonicalChannelName)) return;
            if(string.IsNullOrEmpty(canonicalChannelName)) return;

            if(IsChannelActive(canonicalChannelName)) {
                _currentChannelName = canonicalChannelName;
                return;
            }

            _isRouteSyncInProgress = true;
            _ = EnsureCanonicalChannelAsync(canonicalChannelName);
        }

        private async Task EnsureCanonicalChannelAsync(string canonicalChannelName) {
            try {
                if(!Application.isEditor && Debug.isDebugBuild &&
                   ShouldEmitThrottledLog(ref _nextRouteSyncLogTime, 1.5f)) {
                    Debug.Log(
                        $"[HOPFLOW][VIVOX] ROUTE_RESOLVED channel={canonicalChannelName} current={_currentChannelName} editor={Application.isEditor} batch={Application.isBatchMode}");
                }

                await EnsureChannelJoinedAsync(canonicalChannelName, positional: true, context: "SessionRouteSync");
            } finally {
                _isRouteSyncInProgress = false;
            }
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
                EventBus.Publish(new VoiceLocalPttStateChangedEvent(_isMicOpen));
                
                // Update NetworkVariable on local PlayerController so other clients see the indicator
                var localPlayer = PlayerController.LocalPlayer;
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

        private void OnSocialSettingsChanged(SocialSettingsChangedEvent _) {
            ApplySettings();
        }

        private void OnPlayerMuteChangedEvent(PlayerMuteChangedEvent evt) {
            if(evt == null) return;
            OnPlayerMuteChanged(evt.PlayerId, evt.IsMuted);
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

        public static List<string> GetAvailableInputDevices() {
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

            EventBus.Publish(new VoiceParticipantRemovedEvent(participant.PlayerId));
        }

        private static void OnSpeechDetected(VivoxParticipant participant) {
            EventBus.Publish(new VoiceParticipantSpeechChangedEvent(
                participant.PlayerId,
                participant.DisplayName,
                participant.SpeechDetected));
        }

        private static void PublishVoiceOverlayReset() {
            EventBus.Publish(new VoiceOverlayResetEvent());
        }
    }
}
