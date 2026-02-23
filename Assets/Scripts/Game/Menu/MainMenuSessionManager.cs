using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Progression;
using Game.Settings;
using Game.UI;
using Network;
using Network.Services;
using Network.Steam;
using Game.Social; // Added
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;
using Image = UnityEngine.UIElements.Image;

namespace Game.Menu {
    /// <summary>
    /// Manages session creation (Steam Lobbies) and player list display in the Main Menu.
    /// Adapted for Steamworks: Join Code Logic replaced by Steam Invites.
    /// </summary>
    public class MainMenuSessionManager : UIElementBase {
        [Header("References")]
        [SerializeField] private MainMenuUIManager uiManager;

        // Global Party UI
        private VisualElement _partyMembersList;
        private Button _inviteButton;
        private Image _inviteIcon;
        private VisualElement _partySeparator;
        private VisualElement _localProfileContainer;
        private VisualElement _localXpRow;
        private ProgressBar _localXpBar;
        private Label _localLevelLabel;
        private bool _localXpElementsErrorLogged;
        private bool _partyMemberTemplateMissingLogged;
        private bool _partyMemberTemplateInvalidLogged;
        private bool _hasDrawnSolo;
        private bool _isSilentHosting;
        private bool _silentHostInFlight;
        private UniTask<bool> _silentHostTask;
        private bool _privateMatchStartInFlight;

        // Events
        public Action OnHostClicked;
        public Action<string> OnJoinClicked;
        public Action OnStartGameClicked;
        public Action OnBackFromLobbyClicked;
        public Action<bool, bool> OnHostStatusChanged; // isHost, wasHost
        public Func<bool> ShouldShowLobbyLeaveModal;
        /// <summary> When set, returns whether to show "Switch Team" in the party context menu (e.g. when on Private Match Setup with a team gamemode). </summary>
        public Func<bool> ShouldShowSwitchTeamInContextMenu;
        /// <summary> Fired when the host chooses "Switch Team" for a player in the private match setup (team modes only). </summary>
        public Action<SteamId> OnSwitchTeamRequested;

        protected override void OnInitialize() {
            FindUIElements();
            RegisterUIEvents();

            if(uiManager == null) uiManager = GetComponent<MainMenuUIManager>();

            DrawSoloPlayer();

            // Auto-host a Steam private lobby when online (used for party/invite UX).
            // When Steam is offline, we stay solo and allow "offline private match" when selecting a gamemode.
            if(SessionManager.Instance != null && !SessionManager.Instance.CurrentLobby.HasValue
               && SteamClient.IsValid && SteamClient.IsLoggedOn) {
                BeginSilentAutoHost();
            }
        }

        private void BeginSilentAutoHost() {
            if(_silentHostInFlight) return;
            if(SessionManager.Instance == null) return;

            _silentHostInFlight = true;
            _silentHostTask = HandleHostClicked(silent: true).Preserve();
            TrackSilentHostTask(_silentHostTask).Forget();
        }

        private async UniTaskVoid TrackSilentHostTask(UniTask<bool> task) {
            try {
                await task;
            } catch(Exception ex) {
                Debug.LogWarning($"[MainMenuSessionManager] Silent auto-host failed: {ex.Message}");
            } finally {
                _silentHostInFlight = false;
            }
        }

        /// <summary>
        /// Public Initialize method for external calls. Calls base Initialize() and then custom logic.
        /// </summary>
        public new void Initialize() {
            base.Initialize();
        }

        protected override void OnEnable() {
            base.OnEnable();
            if(SessionManager.Instance == null) return;
            SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
            SessionManager.Instance.OnPartyStateChanged -= HandlePartyStateChanged;
            SessionManager.Instance.FrontStatusChanged += UpdateStatusText;
            SessionManager.Instance.OnPartyStateChanged += HandlePartyStateChanged;
        }

        protected override void OnDisable() {
            if(SessionManager.HasInstance) {
                SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
                SessionManager.Instance.OnPartyStateChanged -= HandlePartyStateChanged;
            }
            base.OnDisable();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "party-members-list", typeof(VisualElement) },
                { "invite-friends-button", typeof(Button) },
                { "local-player-profile", typeof(VisualElement) }
            };
        }

        private void FindUIElements() {
            QOptional<VisualElement>("loading-overlay");

            // Global Party UI
            _partyMembersList = QRequired<VisualElement>("party-members-list");
            _inviteButton = QRequired<Button>("invite-friends-button");
            _inviteIcon = QOptional<Image>("invite-icon");
            _partySeparator = QOptional<VisualElement>("party-separator");
            _localProfileContainer = QRequired<VisualElement>("local-player-profile");
        }

        private async UniTaskVoid OpenSteamInviteOverlay() {
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) {
                if(uiManager != null) {
                    uiManager.ShowToast("Steam is offline. Invites unavailable.", _inviteButton);
                }
                return;
            }

            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
            } else {
                var success = await HandleHostClicked(silent: false);
                if(success && SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                    SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
                }
            }
        }

        private void Update() {
            if(SessionManager.Instance == null) return;
            
            // Handle Matchmaking Status & Locking
            var isSearching = SessionManager.Instance.IsSearching;
            var showStatus = SessionManager.Instance.ShowMatchmakingStatus;
            var isPartyMember = SessionManager.Instance.IsPartyMemberResolved;
            var sessionBusy = SessionManager.Instance.IsSessionBusy;

            var steamOnline = SteamClient.IsValid && SteamClient.IsLoggedOn;

            var isNetworkOffline = Application.internetReachability == NetworkReachability.NotReachable;
            // Update UI constraints based on party state
            var currentPartySize = SessionManager.Instance.CurrentPartySize;

            if(_inviteButton != null) {
                var canInvite = currentPartySize < 10 && SessionManager.Instance.IsLocalPartyLeaderResolved;
                if(isSearching) canInvite = false;

                _inviteButton.style.display = canInvite ? DisplayStyle.Flex : DisplayStyle.None;
                _inviteButton.SetEnabled(canInvite);
                _inviteButton.tooltip = steamOnline ? "Invite friends" : "Steam is offline. Invites unavailable.";

                if(steamOnline) {
                    _inviteButton.RemoveFromClassList("steam-offline");
                    if(_inviteIcon != null) {
                        _inviteIcon.RemoveFromClassList("offline-icon");
                        if(!_inviteIcon.ClassListContains("plus-icon")) {
                            _inviteIcon.AddToClassList("plus-icon");
                        }
                    }
                } else {
                    _inviteButton.AddToClassList("steam-offline");
                    if(_inviteIcon != null) {
                        _inviteIcon.RemoveFromClassList("plus-icon");
                        if(!_inviteIcon.ClassListContains("offline-icon")) {
                            _inviteIcon.AddToClassList("offline-icon");
                        }
                    }
                }
            }

            var canUseMenuButtons = ((!isSearching && !isPartyMember) || _isSilentHosting) && !sessionBusy;

            if(uiManager != null) {
                var playMatchmakingButton = uiManager.GetPlayButtonMatchmaking();
                if(playMatchmakingButton != null) {
                    if(isNetworkOffline) {
                        playMatchmakingButton.tooltip = "Offline. Matchmaking unavailable.";
                    } else if(currentPartySize > 5) {
                        playMatchmakingButton.tooltip = "Party too large for matchmaking (max 5).";
                    } else {
                        playMatchmakingButton.tooltip = "Play matchmaking.";
                    }
                }

                if(isNetworkOffline) {
                    uiManager.SetMenuButtonsEnabled(true); // Play opens gamemode select; user picks Private Match there.
                } else if(currentPartySize > 5) {
                    MainMenuUIManager.DisableButton(uiManager.GetPlayButtonMatchmaking());
                } else {
                    uiManager.SetMenuButtonsEnabled(canUseMenuButtons);
                }

                if(uiManager.StatusContainer != null) {
                    if(showStatus) {
                        uiManager.StatusContainer.RemoveFromClassList("hidden");
                        uiManager.StatusContainer.style.display = DisplayStyle.Flex;
                    } else {
                        uiManager.StatusContainer.AddToClassList("hidden");
                        uiManager.StatusContainer.style.display = DisplayStyle.None;
                    }

                    // Update Timer & Gamemode info
                    if(showStatus && isSearching) {
                        if(uiManager.QueueGamemodeLabel != null) {
                            uiManager.QueueGamemodeLabel.text = SessionManager.Instance.SelectedGameMode;
                        }

                        if(uiManager.QueueTimerLabel != null) {
                            var elapsed = Time.time - SessionManager.Instance.MatchmakingStartTime;
                            var minutes = Mathf.FloorToInt(elapsed / 60f);
                            var seconds = Mathf.FloorToInt(elapsed % 60f);
                            uiManager.QueueTimerLabel.text = $"{minutes:00}:{seconds:00}";
                        }
                    }
                }
            }

            // Internal logic for drawing solo player if not in lobby
            if(!SessionManager.Instance.CurrentLobby.HasValue) {
                if(!_hasDrawnSolo) {
                    DrawSoloPlayer();
                    _hasDrawnSolo = true;
                }
            } else {
                _hasDrawnSolo = false;
            }

            UpdateLocalProgressionDisplay();
        }

        private void RegisterUIEvents() {
            if(_inviteButton != null) {
                Action inviteHandler = () => {
                    UISoundService.PlayButtonClick();
                    OpenSteamInviteOverlay().Forget();
                };
                _inviteButton.clicked += inviteHandler;
                RegisterCleanup(() => _inviteButton.clicked -= inviteHandler);
            }

            if(uiManager == null) return;
            uiManager.OnCancelMatchmakingClicked = () => {
                UISoundService.PlayButtonClick(isBack: true);
                if(SessionManager.Instance != null) {
                    SessionManager.Instance.CancelMatchmaking();
                }
            };

            // Listen to context menu interactions on the root to avoid late initialization issues
            EventCallback<PointerDownEvent> contextMenuHandler = HandleContextMenuInteraction;
            Root.RegisterCallback(contextMenuHandler, TrickleDown.TrickleDown);
            RegisterCleanup(() => Root.UnregisterCallback(contextMenuHandler));
        }

        /// <summary>
        /// Global handler for context menu interactions (clicks on context buttons or backdrop).
        /// Resolves the action by walking up from the click target so clicking a button's label still works.
        /// </summary>
        private void HandleContextMenuInteraction(PointerDownEvent evt) {
            if(uiManager == null || uiManager.PartyContextMenu == null ||
               uiManager.PartyContextMenu.ClassListContains("hidden")) {
                return;
            }

            if(evt.target is not VisualElement target) return;

            var tName = GetContextMenuActionName(target);

            switch(tName) {
                case "ctx-leave":
                    HandleContextAction("Leave");
                    evt.StopPropagation();
                    return;
                case "ctx-kick":
                    HandleContextAction("Kick");
                    evt.StopPropagation();
                    return;
                case "ctx-make-host":
                    HandleContextAction("Promote");
                    evt.StopPropagation();
                    return;
                case "ctx-switch-team":
                    HandleContextAction("SwitchTeam");
                    evt.StopPropagation();
                    return;
                case "ctx-profile":
                    HandleContextAction("Profile");
                    evt.StopPropagation();
                    return;
                case "ctx-steam-profile":
                    HandleContextAction("SteamProfile");
                    evt.StopPropagation();
                    return;
                case "ctx-mute-chat":
                    HandleContextAction("MuteChat");
                    evt.StopPropagation();
                    return;
                case "ctx-mute-voice":
                    HandleContextAction("MuteVoice");
                    evt.StopPropagation();
                    return;
                case "ctx-block":
                    HandleContextAction("Block");
                    evt.StopPropagation();
                    return;
                case "context-menu-backdrop":
                    HideContextMenu();
                    evt.StopPropagation();
                    return;
            }
        }

        private static string GetContextMenuActionName(VisualElement target) {
            for(var el = target; el != null; el = el.parent) {
                if(string.IsNullOrEmpty(el.name)) continue;
                if(el.name.StartsWith("ctx-", StringComparison.Ordinal) || el.name == "context-menu-backdrop")
                    return el.name;
            }
            return target.name;
        }

        private SteamId _contextMenuTargetId;

        private void HideContextMenu() {
            if(uiManager == null || uiManager.PartyContextMenu == null) return;
            uiManager.PartyContextMenu.AddToClassList("hidden");
            if(uiManager.ContextMenuBackdrop != null) {
                uiManager.ContextMenuBackdrop.AddToClassList("hidden");
            }
        }

        /// <summary>
        /// Displays the context menu for a specific party member at the given screen position.
        /// </summary>
        private void ShowContextMenu(Vector2 position, SteamId targetId, bool isMe, bool amIHost,
            bool showSwitchTeam = false) {
            if(uiManager == null || uiManager.PartyContextMenu == null) return;

            _contextMenuTargetId = targetId;

            if(uiManager.CtxProfile != null) uiManager.CtxProfile.style.display = DisplayStyle.Flex;
            if(uiManager.CtxSteamProfile != null) uiManager.CtxSteamProfile.style.display = DisplayStyle.Flex;

            var isSolo = SessionManager.Instance == null || SessionManager.Instance.HasRealPartyMembers == false;
            var showLeave = isMe && !isSolo;
            if(uiManager.CtxLeave != null)
                uiManager.CtxLeave.style.display = showLeave ? DisplayStyle.Flex : DisplayStyle.None;

            var canManage = amIHost && !isMe;
            if(uiManager.CtxSwitchTeam != null) {
                uiManager.CtxSwitchTeam.style.display = showSwitchTeam ? DisplayStyle.Flex : DisplayStyle.None;
                if(Debug.isDebugBuild) {
                    Debug.Log($"[PrivateMatchSetup] ShowContextMenu: showSwitchTeam={showSwitchTeam} CtxSwitchTeam.display={uiManager.CtxSwitchTeam.style.display}");
                }
            } else if(Debug.isDebugBuild && showSwitchTeam) {
                Debug.LogWarning("[PrivateMatchSetup] ShowContextMenu: showSwitchTeam=true but CtxSwitchTeam (ctx-switch-team) is null. Check UXML.");
            }

            var showSeparator = showLeave || canManage || showSwitchTeam;
            if(uiManager.CtxSeparatorManagement != null) {
                uiManager.CtxSeparatorManagement.style.display = showSeparator ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if(uiManager.CtxKick != null)
                uiManager.CtxKick.style.display = canManage ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxMakeHost != null)
                uiManager.CtxMakeHost.style.display = canManage ? DisplayStyle.Flex : DisplayStyle.None;

            var isOther = !isMe;
            if(uiManager.CtxMuteChat != null)
                uiManager.CtxMuteChat.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxMuteVoice != null)
                uiManager.CtxMuteVoice.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxBlock != null)
                uiManager.CtxBlock.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;

            if(uiManager.CtxSeparatorMute != null) {
                uiManager.CtxSeparatorMute.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            }

            uiManager.PartyContextMenu.style.left = position.x;
            uiManager.PartyContextMenu.style.top = position.y;

            uiManager.PartyContextMenu.RemoveFromClassList("hidden");
            uiManager.PartyContextMenu.BringToFront();

            if(uiManager.ContextMenuBackdrop != null) {
                uiManager.ContextMenuBackdrop.RemoveFromClassList("hidden");
                uiManager.ContextMenuBackdrop.BringToFront();
                uiManager.PartyContextMenu.BringToFront();
            }

            UISoundService.PlayButtonClick();
        }

        private void HandleContextAction(string action) {
            HideContextMenu();
            if(_contextMenuTargetId.Value == 0) return;

            Debug.Log($"[MainMenuSessionManager] Context Action: {action}");

            switch(action) {
                case "Leave":
                    if(SessionManager.Instance != null && SessionManager.Instance.HasRealPartyMembers == false) {
                        if(uiManager != null) {
                            uiManager.ShowToast("You're not in a party.");
                        }
                        break;
                    }
                    // Trigger Leave Logic
                    SessionManager.Instance.LeaveLobby();
                    // TODO: This might need confirmation modal?
                    // Currently just leaves.
                    break;
                case "Kick":
                    SessionManager.Instance.KickMember(_contextMenuTargetId);
                    break;
                case "Promote":
                    SessionManager.Instance.PromoteMember(_contextMenuTargetId);
                    break;
                case "Profile":
                    var targetName = "Unknown";
                    if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                        var member =
                            SessionManager.Instance.CurrentLobby.Value.Members.FirstOrDefault(m =>
                                m.Id == _contextMenuTargetId);
                        if(member.Id != 0) targetName = member.Name;
                    }

                    var isMe = _contextMenuTargetId == SteamClient.SteamId;
                    var mainMenuManager = FindFirstObjectByType<MainMenuManager>();

                    if(isMe) {
                        if(mainMenuManager != null) mainMenuManager.ShowLoadoutPanel();
                        break;
                    }

                    if(mainMenuManager != null) {
                        mainMenuManager.ShowProfileView(_contextMenuTargetId, targetName, false);
                    }

                    break;
                case "SteamProfile":
                    // Open Steam profile page in overlay browser
                    var profileUrl = $"https://steamcommunity.com/profiles/{_contextMenuTargetId.Value}";
                    SteamFriends.OpenWebOverlay(profileUrl);
                    break;
                case "MuteChat":
                    // For now, blocking chat mutes voice too usually, or just chat
                    // SocialSettings only has "Muted" (Audio) or Blocked (Both)
                    // Let's implement MuteChat as Block for now? Or just ignore chat?
                    // The prompt asked for specific behaviors.
                    // User Request: "maybe mute just mutes audio, where block mutes audio and chat?"
                    // So MuteVoice = SocialSettings.SetMuted
                    // Block = SocialSettings.SetBlocked
                    // MuteChat? Maybe not supported yet or just mute voice?
                    // Let's assume MuteVoice is the primary mute.
                    Debug.LogWarning("Mute Chat standalone not fully implemented, separate lists needed.");
                    break;
                case "MuteVoice":
                    var isMuted = SocialSettings.IsMuted(_contextMenuTargetId.ToString());
                    SocialSettings.SetMuted(_contextMenuTargetId.ToString(), !isMuted);
                    // Also update Vivox
                    VoiceManager.Instance.MuteUser(_contextMenuTargetId.ToString(), !isMuted);
                    break;
                case "Block":
                    var isBlocked = SocialSettings.IsBlocked(_contextMenuTargetId.ToString());
                    SocialSettings.SetBlocked(_contextMenuTargetId.ToString(), !isBlocked);
                    // Update Vivox if blocked
                    VoiceManager.Instance.MuteUser(_contextMenuTargetId.ToString(), !isBlocked);
                    break;
                case "SwitchTeam":
                    OnSwitchTeamRequested?.Invoke(_contextMenuTargetId);
                    break;
            }
        }

        /// <summary>
        /// Shows the party context menu for a player row (e.g. in private match setup).
        /// Use showSwitchTeam when in team-based gamemode and current user is host.
        /// </summary>
        public void ShowContextMenuForPartyMember(Vector2 position, SteamId targetId, bool showSwitchTeam) {
            var isMe = targetId == SteamClient.SteamId;
            var amIHost = IsHost;
            if(Debug.isDebugBuild) {
                Debug.Log($"[PrivateMatchSetup] ShowContextMenuForPartyMember: position=({position.x},{position.y}) " +
                          $"targetId={targetId.Value} showSwitchTeam={showSwitchTeam} isMe={isMe} amIHost={amIHost} " +
                          $"CtxSwitchTeamElement={uiManager != null && uiManager.CtxSwitchTeam != null}");
            }
            ShowContextMenu(position, targetId, isMe, amIHost, showSwitchTeam);
        }

        private void HandlePartyStateChanged() {
            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                var lobby = SessionManager.Instance.CurrentLobby.Value;
                RefreshPlayerList(lobby);
            } else {
                DrawSoloPlayer();
                _hasDrawnSolo = true;
            }
        }

        /// <summary>
        /// Updates the matchmaking status label and synchronized gamemode display.
        /// </summary>
        /// <param name="msg">The status message to display.</param>
        private void UpdateStatusText(string msg) {
            if(uiManager != null && uiManager.MatchmakingStatusLabel != null && !string.IsNullOrEmpty(msg)) {
                uiManager.MatchmakingStatusLabel.text = msg;
            }

            if(uiManager != null && uiManager.GamemodeDisplayLabel != null && SessionManager.Instance != null) {
                uiManager.GamemodeDisplayLabel.text = SessionManager.Instance.SelectedGameMode;
            }
        }

        public void ResetLobbyUI() {
            if(_partyMembersList != null) _partyMembersList.Clear();
            if(_localProfileContainer != null) _localProfileContainer.Clear();
            _hasDrawnSolo = false;

            IsHost = false;
        }

        /// <summary>
        /// Starts a private match with the given draft settings and optional team assignments.
        /// Applies gamemode, map, timer, score-to-win, tagged players, and draft teams before starting.
        /// </summary>
        public async UniTask HandlePrivateMatchSelection(
            string mode,
            string mapId,
            int matchTimerSeconds,
            bool usePreMatchCountdown,
            bool swapWeaponsOnDeath,
            int scoreToWin,
            int kothHillSpeed,
            int taggedPlayers,
            IReadOnlyDictionary<ulong, int> teamAssignments) {
            if(SessionManager.Instance == null) return;
            if(_privateMatchStartInFlight) return;

            _privateMatchStartInFlight = true;
            try {
                SessionManager.Instance.ApplyPrivateMatchSettings(
                    mode, mapId, matchTimerSeconds, usePreMatchCountdown, swapWeaponsOnDeath, scoreToWin, kothHillSpeed, taggedPlayers,
                    teamAssignments);

                if(Application.internetReachability == NetworkReachability.NotReachable) {
                    if(uiManager != null) {
                        uiManager.ShowToast("Offline. Starting offline private match.");
                    }
                    await SessionManager.Instance.StartOfflinePrivateMatchAsync(mode);
                    return;
                }

                if(_silentHostInFlight) {
                    var waitStart = Time.realtimeSinceStartup;
                    while(_silentHostInFlight && Time.realtimeSinceStartup - waitStart < 8f) {
                        await UniTask.Yield();
                    }

                    if(_silentHostInFlight) {
                        Debug.LogWarning(
                            "[MainMenuSessionManager] Silent host setup timed out while starting private match.");
                        if(uiManager != null) {
                            uiManager.ShowToast("Preparing private match. Please try again.");
                        }
                        return;
                    }
                }

                var matchSettings = Match.MatchSettingsManager.Instance;
                var maxPlayers = 10;
                if(matchSettings != null) {
                    var def = matchSettings.GetGamemodeDef(mode);
                    if(def.maxPlayers > 0) maxPlayers = def.maxPlayers;
                }

                if(!SessionManager.Instance.HasPartyLobby) {
                    await SessionManager.Instance.CreatePartyLobbyAsync(maxPlayers, true);
                }
                await SessionManager.Instance.StartPrivateMatchAsync(mode, maxPlayers);
            } catch(Exception ex) {
                Debug.LogError($"[MainMenuSessionManager] Failed to start private match for mode '{mode}': {ex}");
                if(uiManager != null) {
                    uiManager.ShowToast("Failed to start private match. Please try again.");
                }
            } finally {
                _privateMatchStartInFlight = false;
            }
        }

        /// <summary>
        /// Logic for hosting a private lobby. 
        /// </summary>
        /// <param name="silent">If true, does not show UI status changes.</param>
        public async UniTask<bool> HandleHostClicked(bool silent = false) {
            _isSilentHosting = silent;
            try {
                if(SessionManager.Instance == null) return false;

                var matchSettings = Match.MatchSettingsManager.Instance;
                var maxPlayers = 10;
                if(matchSettings != null) {
                    var def = matchSettings.GetGamemodeDef(SessionManager.Instance.SelectedGameMode);
                    if(def.maxPlayers > 0) maxPlayers = def.maxPlayers;
                }

                await SessionManager.Instance.CreatePartyLobbyAsync(maxPlayers, true);
                IsHost = true;
                return true;
            } catch(Exception e) {
                Debug.LogException(e);
                return false;
            } finally {
                _isSilentHosting = false;
            }
        }

        /// <summary>
        /// Starts searching for a public game in the selected or default mode.
        /// </summary>
        public async UniTaskVoid HandleFindGameClicked(string mode = null) {
            try {
                if(uiManager != null) uiManager.SetMenuButtonsEnabled(false);
                if(SessionManager.Instance != null) {
                    await SessionManager.Instance.StartMatchmakerQuickPlayAsync(mode);
                }
            } catch(Exception e) {
                Debug.LogException(e);
                if(uiManager != null) uiManager.SetMenuButtonsEnabled(true);
            }
        }

        public static void HandleGamemodeSelected(string mode) {
            if(SessionManager.Instance == null) return;
            SessionManager.Instance.SetGameMode(mode);
        }

        public void ToggleGamemodeDropdown() {
            if(uiManager == null || uiManager.GamemodeDropdownMenu == null) return;

            // Only host can toggle
            if(!IsHost) return;

            var isHidden = uiManager.GamemodeDropdownMenu.ClassListContains("hidden");
            if(isHidden) {
                uiManager.GamemodeDropdownMenu.RemoveFromClassList("hidden");
                UISoundService.PlayButtonClick();
            } else {
                uiManager.GamemodeDropdownMenu.AddToClassList("hidden");
                UISoundService.PlayButtonClick(isBack: true);
            }
        }

        public void HandleCancelMatchmakingClicked() {
            if(uiManager == null) return;

            // Call session manager logic
            if(SessionManager.Instance != null) {
                SessionManager.Instance.CancelMatchmaking();
            }

            uiManager.SetMenuButtonsEnabled(true);
        }

        private void RefreshPlayerList(Lobby lobby) {
            if(uiManager == null || uiManager.PartyContainer == null) return;

            // Update Global Party UI (Top Right)
            UpdateGlobalPartyUI(lobby).Forget();

            // Check host status (ownership transfer?)
            var amIHost = lobby.Owner.Id == SteamClient.SteamId;
            if(amIHost == IsHost) return;
            IsHost = amIHost;
            UpdateHostStatus(IsHost);
        }

        private void DrawSoloPlayer() {
            if(uiManager == null) return;

            if(_partyMembersList != null) _partyMembersList.Clear();
            if(_localProfileContainer != null) _localProfileContainer.Clear();
            ResetLocalProgressionReferences();

            // Draw just us in the local profile section
            var steamOnline = SteamClient.IsValid && SteamClient.IsLoggedOn;
            var displayName = StreamerMode.GetLocalDisplayName();
            var displayId = steamOnline ? SteamClient.SteamId : default;

            var hide = !steamOnline || StreamerMode.Enabled;
            var iconId = PlayerIconPicker.PickIconIdFromBaseColor(GameSettings.Data.player.customization.baseColor, hide);
            CreatePlayerRow(displayName, displayId, iconId, true, _localProfileContainer).Forget();

            // Show invite button and separator
            if(_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if(_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Rebuilds the party UI containers (list of members and local profile) for a specific lobby.
        /// </summary>
        private async UniTaskVoid UpdateGlobalPartyUI(Lobby lobby) {
            if(uiManager == null) return;

            if(_partyMembersList != null) _partyMembersList.Clear();
            if(_localProfileContainer != null) _localProfileContainer.Clear();
            ResetLocalProgressionReferences();

            var hostId = lobby.Owner.Id;
            var myPartyId = "";
            var session = SessionManager.Instance;
            if(session != null) {
                myPartyId = session.CurrentPartyId;
            }

            foreach(var member in lobby.Members) {
                var inMyParty = lobby.GetMemberData(member, "PartyId") == myPartyId;
                var displayName = lobby.GetMemberData(member, "DisplayName");
                if(string.IsNullOrEmpty(displayName)) {
                    displayName = member.Name;
                }

                var avatarHidden = lobby.GetMemberData(member, "AvatarHidden") == "1";
                var iconId = lobby.GetMemberData(member, "PlayerIcon");
                if(string.IsNullOrEmpty(iconId)) {
                    iconId = PlayerIconPicker.PickDeterministicIconId(member.Id.Value, avatarHidden);
                }

                if(member.Id == SteamClient.SteamId) {
                    await CreatePlayerRow(displayName, member.Id, iconId, true, _localProfileContainer, member.Id == hostId,
                        inMyParty, avatarHidden);
                } else {
                    await CreatePlayerRow(displayName, member.Id, iconId, false, _partyMembersList, member.Id == hostId,
                        inMyParty, avatarHidden);
                }
            }

            if(_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if(_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Creates and styles a single player row in the party UI.
        /// </summary>
        private async UniTask CreatePlayerRow(string playerName, SteamId id, string iconId, bool isLocal,
            VisualElement targetContainer, bool isHost = false, bool isPartyMember = false, bool hideAvatar = false) {
            if(targetContainer == null) return;

            if(uiManager == null || uiManager.PartyMemberTemplate == null) {
                if(_partyMemberTemplateMissingLogged) return;
                _partyMemberTemplateMissingLogged = true;
                Debug.LogError(
                    "[MainMenuSessionManager] PartyMemberTemplate is required on MainMenuUIManager.",
                    this);
                return;
            }

            var instance = uiManager.PartyMemberTemplate.Instantiate();
            var row = instance.Q("party-member-row");
            var avatarBox = instance.Q("avatar-box");
            var nameLabel = instance.Q<Label>("player-name-label");
            var localXpRow = instance.Q<VisualElement>("local-xp-row");
            var localXpBar = instance.Q<ProgressBar>("local-xp-bar");
            var localLevelLabel = instance.Q<Label>("local-level-label");

            if(row == null || avatarBox == null || nameLabel == null) {
                if(_partyMemberTemplateInvalidLogged) return;
                _partyMemberTemplateInvalidLogged = true;
                Debug.LogError(
                    "[MainMenuSessionManager] PartyMemberTemplate is missing required elements: " +
                    "`party-member-row`, `avatar-box`, `player-name-label`.",
                    this);
                return;
            }

            _partyMemberTemplateMissingLogged = false;
            _partyMemberTemplateInvalidLogged = false;

            if(!isLocal) {
                row.AddToClassList("party-member-entry");
                row.style.marginRight = 8;
            } else {
                row.style.marginRight = 0;
            }
            row.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.4f));

            var showHostIndicator = isHost;
            if(isLocal && isHost) {
                var memberCount = 1;
                if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                    memberCount = SessionManager.Instance.CurrentLobby.Value.MemberCount;
                }

                if(memberCount <= 1) showHostIndicator = false;
            }

            var hostColor = new Color(1, 0.8f, 0, 0.6f);
            var partyColor = new Color(0.2f, 0.6f, 1f, 0.6f);

            float borderSize = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0);
            var borderColor = showHostIndicator
                ? new StyleColor(hostColor)
                : isPartyMember ? new StyleColor(partyColor) : new StyleColor(StyleKeyword.Null);

            avatarBox.style.borderTopWidth = borderSize;
            avatarBox.style.borderBottomWidth = borderSize;
            avatarBox.style.borderLeftWidth = borderSize;
            avatarBox.style.borderRightWidth = borderSize;

            avatarBox.style.borderTopColor = borderColor;
            avatarBox.style.borderBottomColor = borderColor;
            avatarBox.style.borderLeftColor = borderColor;
            avatarBox.style.borderRightColor = borderColor;

            nameLabel.text = playerName;
            row.RegisterCallback<PointerDownEvent>(evt => {
                if(evt.button != 1) return;
                var showSwitchTeam = ShouldShowSwitchTeamInContextMenu != null && ShouldShowSwitchTeamInContextMenu.Invoke();
                ShowContextMenu(evt.position, id, isLocal, IsHost, showSwitchTeam);
                evt.StopPropagation();
            });
            targetContainer.Add(row);

            if(isLocal) {
                if(localXpRow == null || localXpBar == null || localLevelLabel == null) {
                    if(!_localXpElementsErrorLogged) {
                        Debug.LogError(
                            "[MainMenuSessionManager] PartyMemberRow template is missing required local XP elements: local-xp-row, local-xp-bar, local-level-label.",
                            this);
                        _localXpElementsErrorLogged = true;
                    }
                } else {
                    _localXpRow = localXpRow;
                    _localXpBar = localXpBar;
                    _localLevelLabel = localLevelLabel;
                    _localXpRow.RemoveFromClassList("hidden");
                    _localXpElementsErrorLogged = false;
                    UpdateLocalProgressionDisplay();
                }
            }

            // Prefer Steam avatar when online; fallback only when Steam can't be reached or avatar fetch fails.
            var steamOnline = SteamClient.IsValid && SteamClient.IsLoggedOn;
            if(!steamOnline || hideAvatar || id.Value == 0) {
                ApplyIconFallback();
            } else {
                // Clear icon classes before setting background image.
                avatarBox.RemoveFromClassList("steam-avatar-flip");
                avatarBox.RemoveFromClassList("default-avatar");
                avatarBox.RemoveFromClassList("player-icon-red");
                avatarBox.RemoveFromClassList("player-icon-orange");
                avatarBox.RemoveFromClassList("player-icon-yellow");
                avatarBox.RemoveFromClassList("player-icon-green");
                avatarBox.RemoveFromClassList("player-icon-blue");
                avatarBox.RemoveFromClassList("player-icon-purple");
                avatarBox.RemoveFromClassList("player-icon-white");

                var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
                if(avatarTex != null) {
                    avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
                    if(!avatarBox.ClassListContains("steam-avatar-flip")) {
                        avatarBox.AddToClassList("steam-avatar-flip");
                    }
                } else {
                    ApplyIconFallback();
                }
            }

            return;

            void ApplyIconFallback() {
                // Only use fallback icon when Steam isn't available or fetching failed.
                avatarBox.style.backgroundImage = StyleKeyword.Null;
                avatarBox.RemoveFromClassList("steam-avatar-flip");

                avatarBox.RemoveFromClassList("default-avatar");
                avatarBox.RemoveFromClassList("player-icon-red");
                avatarBox.RemoveFromClassList("player-icon-orange");
                avatarBox.RemoveFromClassList("player-icon-yellow");
                avatarBox.RemoveFromClassList("player-icon-green");
                avatarBox.RemoveFromClassList("player-icon-blue");
                avatarBox.RemoveFromClassList("player-icon-purple");
                avatarBox.RemoveFromClassList("player-icon-white");

                var resolved = hideAvatar ? PlayerIconPicker.White : iconId;
                if(string.IsNullOrEmpty(resolved)) resolved = PlayerIconPicker.White;
                avatarBox.AddToClassList("player-icon-" + resolved);
            }
        }

        private void UpdateLocalProgressionDisplay() {
            if(_localXpBar == null || _localLevelLabel == null) return;

            var progression = ProgressionManager.Instance;
            if(progression == null || progression.Data == null) {
                if(_localXpRow != null) _localXpRow.AddToClassList("hidden");
                return;
            }

            var level = Mathf.Max(1, progression.Data.level);
            var requiredXp = Mathf.Max(1, progression.GetXpRequiredForLevel(level));
            var currentXp = Mathf.Clamp(progression.Data.currentXp, 0, requiredXp);

            if(_localXpRow != null) _localXpRow.RemoveFromClassList("hidden");
            _localXpBar.lowValue = 0;
            _localXpBar.highValue = requiredXp;
            _localXpBar.value = currentXp;
            _localLevelLabel.text = $"LVL {level}";
        }

        private void ResetLocalProgressionReferences() {
            _localXpRow = null;
            _localXpBar = null;
            _localLevelLabel = null;
        }


        private void UpdateHostStatus(bool isHost) {
            if(OnHostStatusChanged != null) {
                OnHostStatusChanged.Invoke(isHost, !isHost);
            }
        }

        public bool IsHost { get; private set; }
    }
}

