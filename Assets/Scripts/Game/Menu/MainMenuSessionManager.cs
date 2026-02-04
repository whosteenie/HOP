using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Network;
using Network.Services;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.Menu {
    /// <summary>
    /// Manages session creation (Steam Lobbies) and player list display in the Main Menu.
    /// Adapted for Steamworks: Join Code Logic replaced by Steam Invites.
    /// </summary>
    public class MainMenuSessionManager : MonoBehaviour {
        [Header("References")]
        [SerializeField] private MainMenuUIManager uiManager;

        public UIDocument uiDocument;

        private VisualElement _root;

        // Global Party UI
        private VisualElement _partyMembersList;
        private Button _inviteButton;
        private VisualElement _partySeparator;
        private VisualElement _localProfileContainer;
        private ulong _lastLobbyId;
        private int _lastMemberCount;
        private bool _hasDrawnSolo;
        private bool _isSilentHosting;

        // Events
        public Action OnHostClicked;
        public Action<string> OnJoinClicked;
        public Action OnStartGameClicked;
        public Action OnBackFromLobbyClicked;
        public Action<bool, bool> OnHostStatusChanged; // isHost, wasHost
        public Func<bool> ShouldShowLobbyLeaveModal;

        /// <summary>
        /// Initializes the session manager, sets up UI event listeners, and ensures 
        /// the local player is correctly represented in the party UI.
        /// </summary>
        public void Initialize() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuSessionManager] UIDocument is not assigned during Initialize!");
                return;
            }

            if(_root != null) return;

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            RegisterUIEvents();

            if(uiManager == null) uiManager = GetComponent<MainMenuUIManager>();

            DrawSoloPlayer();

            if(SessionManager.Instance != null && !SessionManager.Instance.CurrentLobby.HasValue) {
                HandleHostClicked(silent: true).Forget();
            }
        }

        private void OnEnable() {
            if(SessionManager.Instance != null) {
                SessionManager.Instance.FrontStatusChanged += UpdateStatusText;
                SessionManager.Instance.OnPartyStateChanged += HandlePartyStateChanged;
            }
        }

        private void OnDisable() {
            if(SessionManager.HasInstance) {
                SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
                SessionManager.Instance.OnPartyStateChanged -= HandlePartyStateChanged;
            }
        }

        private void FindUIElements() {
            _root.Q<VisualElement>("loading-overlay");

            // Global Party UI
            _partyMembersList = _root.Q<VisualElement>("party-members-list");
            _inviteButton = _root.Q<Button>("invite-friends-button");
            _partySeparator = _root.Q<VisualElement>("party-separator");
            _localProfileContainer = _root.Q<VisualElement>("local-player-profile");
        }

        private async UniTaskVoid OpenSteamInviteOverlay() {
            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
            } else {
                bool success = await HandleHostClicked(silent: false);
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
            var isPartyMember =
                SessionManager.Instance.CurrentLobby.HasValue && !SessionManager.Instance.IsPartyLeader;

            // Update UI constraints based on party state
            var currentPartySize = SessionManager.Instance.CurrentLobby.HasValue
                ? SessionManager.Instance.CurrentLobby.Value.MemberCount
                : 1;

            if(_inviteButton != null) {
                var canInvite = currentPartySize < 10 && (!SessionManager.Instance.CurrentLobby.HasValue 
                                                          || SessionManager.Instance.IsPartyLeader);
                if(isSearching) canInvite = false;

                _inviteButton.style.display = canInvite ? DisplayStyle.Flex : DisplayStyle.None;
            }

            var canPlay = !isSearching && !isPartyMember || _isSilentHosting;

            var selectedMode = SessionManager.Instance.SelectedGameMode;
            if(!canPlay || Match.MatchSettingsManager.Instance == null) return;
            Match.MatchSettingsManager.Instance.GetGamemodeDef(selectedMode);

            if(uiManager != null) {
                if(currentPartySize > 5) {
                    uiManager.DisableButton(uiManager.GetPlayButtonMatchmaking());
                    if(!isSearching) uiManager.EnableButton(uiManager.GetPlayButtonPrivate());
                } else {
                    uiManager.SetMenuButtonsEnabled((!isSearching && !isPartyMember) || _isSilentHosting);
                }

                if(uiManager.StatusContainer != null) {
                    var targetDisplay = showStatus ? DisplayStyle.Flex : DisplayStyle.None;
                    if(uiManager.StatusContainer.style.display != targetDisplay) {
                        uiManager.StatusContainer.style.display = targetDisplay;
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
                    _lastLobbyId = 0;
                    _lastMemberCount = 0;
                }
            } else {
                _hasDrawnSolo = false;
            }
        }

        private void RegisterUIEvents() {
            if(_inviteButton != null) {
                _inviteButton.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OpenSteamInviteOverlay().Forget();
                };
            }

            if(uiManager == null) return;
            uiManager.OnCancelMatchmakingClicked = () => {
                UISoundService.PlayButtonClick(isBack: true);
                SessionManager.Instance.CancelMatchmaking();
            };

            // Listen to context menu interactions on the root to avoid late initialization issues
            _root.RegisterCallback<PointerDownEvent>(HandleContextMenuInteraction, TrickleDown.TrickleDown);
        }

        /// <summary>
        /// Global handler for context menu interactions (clicks on context buttons or backdrop).
        /// </summary>
        private void HandleContextMenuInteraction(PointerDownEvent evt) {
            if(uiManager == null || uiManager.PartyContextMenu == null ||
               uiManager.PartyContextMenu.ClassListContains("hidden")) {
                return;
            }

            var target = evt.target as VisualElement;
            if(target == null) return;

            var tName = target.name;

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
                case "ctx-profile":
                    HandleContextAction("Profile");
                    evt.StopPropagation();
                    return;
                case "ctx-steam-profile":
                    HandleContextAction("SteamProfile");
                    evt.StopPropagation();
                    return;
                case "ctx-mute-chat":
                    HideContextMenu();
                    evt.StopPropagation();
                    return;
                case "ctx-mute-voice":
                    HideContextMenu();
                    evt.StopPropagation();
                    return;
                case "ctx-block":
                    HideContextMenu();
                    evt.StopPropagation();
                    return;
                case "context-menu-backdrop":
                    HideContextMenu();
                    evt.StopPropagation();
                    return;
            }
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
        private void ShowContextMenu(Vector2 position, SteamId targetId, bool isMe, bool amIHost) {
            if(uiManager == null || uiManager.PartyContextMenu == null) return;

            _contextMenuTargetId = targetId;

            if(uiManager.CtxProfile != null) uiManager.CtxProfile.style.display = DisplayStyle.Flex;
            if(uiManager.CtxSteamProfile != null) uiManager.CtxSteamProfile.style.display = DisplayStyle.Flex;

            var isSolo = SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue &&
                         SessionManager.Instance.CurrentLobby.Value.MemberCount <= 1;
            var showLeave = isMe && !isSolo;
            if(uiManager.CtxLeave != null)
                uiManager.CtxLeave.style.display = showLeave ? DisplayStyle.Flex : DisplayStyle.None;

            var canManage = amIHost && !isMe;
            var showSeparator = showLeave || canManage;
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
                    SteamFriends.OpenUserOverlay(_contextMenuTargetId, "steamid");
                    break;
            }
        }

        private void HandlePartyStateChanged() {
            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                var lobby = SessionManager.Instance.CurrentLobby.Value;
                _lastLobbyId = lobby.Id;
                _lastMemberCount = lobby.MemberCount;
                RefreshPlayerList(lobby);
            } else {
                DrawSoloPlayer();
                _hasDrawnSolo = true;
                _lastLobbyId = 0;
                _lastMemberCount = 0;
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
            _partyMembersList?.Clear();
            _localProfileContainer?.Clear();
            _lastLobbyId = 0;
            _lastMemberCount = 0;
            _hasDrawnSolo = false;

            IsHost = false;
        }

        public async UniTask HandlePrivateMatchSelection(string mode) {
            UISoundService.PlayButtonClick();
            // Request SessionManager to start the synchronized load
            if(SessionManager.Instance != null) {
                await SessionManager.Instance.StartPrivateMatchSync(mode);
            }
        }

        /// <summary>
        /// Logic for hosting a private lobby. 
        /// </summary>
        /// <param name="silent">If true, does not show UI status changes.</param>
        public async UniTask<bool> HandleHostClicked(bool silent = false) {
            _isSilentHosting = silent;
            try {
                var success = await SessionManager.Instance.CreatePrivateLobbyAsync();
                if(success) {
                    IsHost = true;
                }

                return success;
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
                await SessionManager.Instance.FindGameAsync(mode);
            } catch(Exception e) {
                Debug.LogException(e);
                if(uiManager != null) uiManager.SetMenuButtonsEnabled(true);
            }
        }

        public static void HandleGamemodeSelected(string mode) {
            if(SessionManager.Instance == null) return;
            SessionManager.Instance.SetGamemode(mode);
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

            _partyMembersList?.Clear();
            _localProfileContainer?.Clear();

            // Draw just us in the local profile section
            CreatePlayerRow(SteamClient.Name, SteamClient.SteamId, true, _localProfileContainer).Forget();

            // Show invite button and separator
            if(_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if(_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Rebuilds the party UI containers (list of members and local profile) for a specific lobby.
        /// </summary>
        private async UniTaskVoid UpdateGlobalPartyUI(Lobby lobby) {
            if(uiManager == null) return;

            _partyMembersList?.Clear();
            _localProfileContainer?.Clear();

            var hostId = lobby.Owner.Id;
            var myPartyId = SessionManager.Instance?.CurrentPartyId;

            foreach(var member in lobby.Members) {
                var inMyParty = lobby.GetMemberData(member, "PartyId") == myPartyId;

                if(member.Id == SteamClient.SteamId) {
                    await CreatePlayerRow(member.Name, member.Id, true, _localProfileContainer, member.Id == hostId,
                        inMyParty);
                } else {
                    await CreatePlayerRow(member.Name, member.Id, false, _partyMembersList, member.Id == hostId,
                        inMyParty);
                }
            }

            if(_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if(_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        /// <summary>
        /// Creates and styles a single player row in the party UI.
        /// </summary>
        private async UniTask CreatePlayerRow(string playerName, SteamId id, bool isLocal, VisualElement targetContainer,
            bool isHost = false, bool isPartyMember = false) {
            if(targetContainer == null) return;

            if(uiManager != null && uiManager.PartyMemberTemplate != null) {
                var instance = uiManager.PartyMemberTemplate.Instantiate();
                var row = instance.Q("party-member-row");
                var avatarBox = instance.Q("avatar-box");
                var nameLabel = instance.Q<Label>("player-name-label");

                if(!isLocal) {
                    row.AddToClassList("party-member-entry");
                    row.style.backgroundColor = new StyleColor(new UnityEngine.Color(0, 0, 0, 0.4f));
                    row.style.marginRight = 8;
                } else {
                    row.style.backgroundColor = new StyleColor(StyleKeyword.Null);
                    row.style.marginRight = 0;
                }

                var showHostIndicator = isHost;
                if(isLocal && isHost) {
                    var memberCount = 1;
                    if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                        memberCount = SessionManager.Instance.CurrentLobby.Value.MemberCount;
                    }

                    if(memberCount <= 1) showHostIndicator = false;
                }

                var hostColor = new UnityEngine.Color(1, 0.8f, 0, 0.6f);
                var partyColor = new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f);

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

                var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
                if(avatarTex != null) {
                    avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
                }

                row.RegisterCallback<PointerDownEvent>(evt => {
                    if(evt.button != 1) return;
                    ShowContextMenu(evt.position, id, isLocal, IsHost);
                    evt.StopPropagation();
                });

                targetContainer.Add(row);
            } else {
                Debug.LogError("[MainMenuSessionManager] PartyMemberTemplate is missing in UIManager!");
            }
        }


        private void UpdateHostStatus(bool isHost) {
            OnHostStatusChanged?.Invoke(isHost, !isHost);
        }

        private bool IsHost { get; set; }
    }
}