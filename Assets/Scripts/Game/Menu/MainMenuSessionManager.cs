using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Network;
using Network.Events;
using Network.Services; // Ensure namespace correct or remove if unused
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
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
        private VisualElement _loadingOverlay;

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

        private void Awake() {
            // No longer checking for uiDocument here as it might be assigned later by MainMenuManager
        }

        public void Initialize() {
            if(uiDocument == null) {
                Debug.LogError("[MainMenuSessionManager] UIDocument is not assigned during Initialize!");
                return;
            }

            if (_root != null) return; // Already initialized

            _root = uiDocument.rootVisualElement;
            FindUIElements();
            RegisterUIEvents();
            
            if (uiManager == null) uiManager = GetComponent<MainMenuUIManager>();

            string partyStatus = (uiManager != null && uiManager.PartyContainer != null) ? "Found" : "Missing";
            string statusStatus = (uiManager != null && uiManager.StatusContainer != null) ? "Found" : "Missing";
            Debug.Log($"[MainMenuSessionManager] Initialized. UI Manager: {uiManager != null}, Party UI: {partyStatus}, Status UI: {statusStatus}");

            // Draw initial solo state
            DrawSoloPlayer();

            // Proactive hosting: Create a private lobby in the background 
            // so things like "Invite Friends" are instant.
            if (SessionManager.Instance != null && !SessionManager.Instance.CurrentLobby.HasValue) {
                HandleHostClicked(silent: true).Forget();
            }
        }

        private void OnEnable() {
            // New EventBus? Or polling?
            // Since SteamManager updates loop, events might come from SessionManager.
            if (SessionManager.Instance != null) {
                SessionManager.Instance.FrontStatusChanged += UpdateStatusText;
            }
            
            // We should listen to Steam Callbacks wrapper events if we added them to session manager
            // Or just poll. For simple UI, polling in Update is fine, but lets assume SessionManager fires events.
            // But I didn't add the EventBus publishes in SessionManager yet (TODO).
            // So we will Poll for now.
        }

        private void OnDisable() {
            if (SessionManager.HasInstance) {
                SessionManager.Instance.FrontStatusChanged -= UpdateStatusText;
            }
        }
        

        private void FindUIElements() {
            _loadingOverlay = _root.Q<VisualElement>("loading-overlay");

            // Global Party UI
            _partyMembersList = _root.Q<VisualElement>("party-members-list");
            _inviteButton = _root.Q<Button>("invite-friends-button");
            _partySeparator = _root.Q<VisualElement>("party-separator");
            _localProfileContainer = _root.Q<VisualElement>("local-player-profile");
        }

        private async UniTaskVoid OpenSteamInviteOverlay() {
            if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
            } else {
                bool success = await HandleHostClicked(silent: false);
                if (success && SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                    SteamManager.Instance.OpenInviteOverlay(SessionManager.Instance.CurrentLobby.Value.Id);
                }
            }
        }

        private void Update() {
            if (SessionManager.Instance == null) return;


            // Handle Matchmaking Status & Locking
            bool isSearching = SessionManager.Instance.IsSearching;
            bool showStatus = SessionManager.Instance.ShowMatchmakingStatus;
            bool isPartyMember = SessionManager.Instance.CurrentLobby.HasValue && !SessionManager.Instance.IsPartyLeader;

            // --- Party Constraint Logic ---
            int currentPartySize = 1;
            if (SessionManager.Instance.CurrentLobby.HasValue) {
                currentPartySize = SessionManager.Instance.CurrentLobby.Value.MemberCount;
            }

            // Check Invite Button Visibility
            if (_inviteButton != null) {
                // Hide if party is full (10) or if we are not the host/leader
                bool canInvite = (currentPartySize < 10) && (SessionManager.Instance.CurrentLobby.HasValue ? SessionManager.Instance.IsPartyLeader : true);
                // Also can't invite if searching
                if (isSearching) canInvite = false;
                
                _inviteButton.style.display = canInvite ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Check Play Button Enable/Disable
            // 1. Must not be searching
            // 2. Must be party leader (or solo)
            // 3. Current Party Size must be <= Gamemode.MaxPartySize (for Public Matches)
            //    Note: We only know the "selected" gamemode via SessionManager/MatchSettingsManager
            
            bool canPlay = !isSearching && !isPartyMember; // Base rules
            if (_isSilentHosting) canPlay = true; // Allow interaction during silent host setup

            if (canPlay && Game.Match.MatchSettingsManager.Instance != null) {
                string selectedMode = SessionManager.Instance.SelectedGameMode;
                var def = Game.Match.MatchSettingsManager.Instance.GetGamemodeDef(selectedMode);
                
                // If attempting Public Match (standard Play button)
                // We assume "Play" button implies Public Queue unless "Private" is handled separately.
                // Actually, MainMenuUIManager handles separate buttons for "Play Matchmaking" vs "Private".
                // Here we just set "MenuButtonsEnabled". 
                // We should technically selectively disable the Matchmaking button but allow Private if party is large.
                // However, MainMenuUIManager.SetMenuButtonsEnabled toggles BOTH.
                // Refinement: 
                // If Party > MaxPartySize (e.g. 6 > 5), we must disable PUBLIC matchmaking.
                // But Private matchmaking should still be allowed? 
                // Current UI structure links them. 
                // Use a dedicated check for the Public Play button if possible, but for now we will disable globally 
                // if the constraint is violated, assuming user will switch to Private via logic or we block the click.
                // Better approach: Let's block the *State* or show a tooltip? 
                // Tooltips are hard in UI Toolkit without setup.
                // Let's just enforce the strict rule: "Parties of 6 or more are meant for private matches only".
                // If Party > 5, we should visually indicate this? 
                
                // For this pass, I'll stick to the requested implementation: 
                // "disable the play button when you have more than 5 total party members"
                
                if (currentPartySize > 5) { // Hard limit as per request "parties of 6 or more... private matches only"
                     // Wait, user said "disable the play button... parties of 6 or more are meant for private matches onluy"
                     // This implies the "Play" (Public) button is disabled, but "Private" might remain?
                     // My SetMenuButtonsEnabled disables ALL. 
                     // I will need to modify MainMenuUIManager to separate them if I want that granular control.
                     // For now, I will assume "Play" usually means the big green button (Matchmaking).
                     // But if I disable all, they can't start Private either.
                     // IMPORTANT: I will assume I need to disable ONLY the Matchmaking button if party is large.
                }
            }

            if (uiManager != null) {
                // If Party > 5, Disable Public Play, Enable Private
                if (currentPartySize > 5) {
                     uiManager.DisableButton(uiManager.GetPlayButtonMatchmaking()); // Need getter or public access
                     if (!isSearching) uiManager.EnableButton(uiManager.GetPlayButtonPrivate());
                } else {
                     // Normal logic
                     uiManager.SetMenuButtonsEnabled((!isSearching && !isPartyMember) || _isSilentHosting);
                }

                if (uiManager.StatusContainer != null) {
                    var targetDisplay = showStatus ? DisplayStyle.Flex : DisplayStyle.None;
                    if (uiManager.StatusContainer.style.display != targetDisplay) {
                        uiManager.StatusContainer.style.display = targetDisplay;
                    }

                    // Update Timer & Gamemode info
                    if (showStatus && isSearching) {
                        if (uiManager.QueueGamemodeLabel != null) {
                            uiManager.QueueGamemodeLabel.text = SessionManager.Instance.SelectedGameMode;
                        }

                        if (uiManager.QueueTimerLabel != null) {
                            float elapsed = Time.time - SessionManager.Instance.MatchmakingStartTime;
                            int minutes = Mathf.FloorToInt(elapsed / 60f);
                            int seconds = Mathf.FloorToInt(elapsed % 60f);
                            uiManager.QueueTimerLabel.text = $"{minutes:00}:{seconds:00}";
                        }
                    }
                }
            }

            // Check if we are in a lobby
            if (SessionManager.Instance.CurrentLobby.HasValue) {
                var lobby = SessionManager.Instance.CurrentLobby.Value;
                // Only rebuild if members changed or lobby changed
                if (lobby.Id != _lastLobbyId || lobby.MemberCount != _lastMemberCount) {
                    _lastLobbyId = lobby.Id;
                    _lastMemberCount = lobby.MemberCount;
                    RefreshPlayerList(lobby);
                }
                _hasDrawnSolo = false;
            } else {
                // Not in a lobby - just draw ourselves if we haven't already
                if (!_hasDrawnSolo) {
                     DrawSoloPlayer();
                     _hasDrawnSolo = true;
                     _lastLobbyId = 0;
                     _lastMemberCount = 0;
                }
            }
        }

        private void RegisterUIEvents() {

            if (_inviteButton != null) {
                _inviteButton.clicked += () => {
                    UISoundService.PlayButtonClick();
                    OpenSteamInviteOverlay().Forget();
                };
            }

            if (uiManager != null) {
                uiManager.OnCancelMatchmakingClicked = () => {
                    UISoundService.PlayButtonClick(isBack: true);
                    SessionManager.Instance.CancelMatchmaking();
                };
                
                // Close context menu on any click outside
                // OLD METHOD: TrickleDown on Root (Deleted - unreliable)
                
                
                // --- EVENT DELEGATION REFACTOR ---
                // We register ONE callback on the root to handle ALL context menu interactions.
                // This bypasses initialization order issues where 'uiManager.CtxLeave' might be null at Start.
                _root.RegisterCallback<PointerDownEvent>(HandleContextMenuInteraction, TrickleDown.TrickleDown);
            }
        }

        private void HandleContextMenuInteraction(PointerDownEvent evt) {
            // Only process if menu is showing
            if (uiManager == null || uiManager.PartyContextMenu == null || uiManager.PartyContextMenu.ClassListContains("hidden")) {
                return;
            }

            var target = evt.target as VisualElement;
            if (target == null) return;

            // 1. Check Buttons (Using Names to avoid Stale Reference/Zombie Object issues)
            string tName = target.name;
            
            // Note: Use evt.StopPropagation() if we handle it
            
            if (tName == "ctx-leave") { HandleContextAction("Leave"); evt.StopPropagation(); return; }
            if (tName == "ctx-kick") { HandleContextAction("Kick"); evt.StopPropagation(); return; }
            if (tName == "ctx-make-host") { HandleContextAction("Promote"); evt.StopPropagation(); return; }
            if (tName == "ctx-profile") { HandleContextAction("Profile"); evt.StopPropagation(); return; }
            if (tName == "ctx-steam-profile") { HandleContextAction("SteamProfile"); evt.StopPropagation(); return; }
            if (tName == "ctx-mute-chat") { HideContextMenu(); evt.StopPropagation(); return; }
            if (tName == "ctx-mute-voice") { HideContextMenu(); evt.StopPropagation(); return; }
            if (tName == "ctx-block") { HideContextMenu(); evt.StopPropagation(); return; }

            // 2. Check Backdrop
            if (tName == "context-menu-backdrop") {
                // Debug.Log("[MainMenuSessionManager] Backdrop Clicked. Hiding.");
                HideContextMenu();
                evt.StopPropagation();
                return;
            }

            // 3. Fallback: If we clicked *inside* the menu but missed a specific button (e.g. spacer, label)
            // AND the menu is open, we generally don't want to close it? 
            // Or maybe we do? Standard UI: Click background of menu -> do nothing.
            // Click outside -> close.
            // Our Backdrop covers "Outside". So if we are here, we are either:
            // a) Clicking the menu background (Descendant of Menu)
            // b) Clicking something else if Backdrop is missing priority?
            // Since Backdrop is full screen behind menu, any outside click hits Backdrop.
            // Any inside click hits Menu.
            // So we do nothing here.
        }

        private SteamId _contextMenuTargetId;

        private void HideContextMenu() {
            if(uiManager != null && uiManager.PartyContextMenu != null) {
                uiManager.PartyContextMenu.AddToClassList("hidden");
                if (uiManager.ContextMenuBackdrop != null) {
                     uiManager.ContextMenuBackdrop.AddToClassList("hidden");
                }
            }
        }

        private void ShowContextMenu(Vector2 position, SteamId targetId, bool isMe, bool amIHost, bool isTargetHost) {
            if(uiManager == null || uiManager.PartyContextMenu == null) return;

            _contextMenuTargetId = targetId;

            // Toggle buttons based on context
            // Profile & Steam Profile: Always Visible for everyone (Self & Others)
            if(uiManager.CtxProfile != null) uiManager.CtxProfile.style.display = DisplayStyle.Flex;
            if(uiManager.CtxSteamProfile != null) uiManager.CtxSteamProfile.style.display = DisplayStyle.Flex;
            
            // Leave: Only for Self, and only if NOT solo
            bool isSolo = SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue && SessionManager.Instance.CurrentLobby.Value.MemberCount <= 1;
            bool showLeave = isMe && !isSolo;
            if(uiManager.CtxLeave != null) uiManager.CtxLeave.style.display = showLeave ? DisplayStyle.Flex : DisplayStyle.None;

            // Separator Logic: Hide if Leave AND (Kick/Promote) are hidden
            bool canManage = amIHost && !isMe;
            bool showManageBlock = canManage; // Kick/Promote
            bool showSeparator = showLeave || showManageBlock;
            if (uiManager.CtxSeparatorManagement != null) {
                uiManager.CtxSeparatorManagement.style.display = showSeparator ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Kick / Make Host: Only if I am Host AND Target is NOT Me
            if(uiManager.CtxKick != null) uiManager.CtxKick.style.display = canManage ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxMakeHost != null) uiManager.CtxMakeHost.style.display = canManage ? DisplayStyle.Flex : DisplayStyle.None;
            
            // Mute/Block: Only if Not Me
            bool isOther = !isMe;
            if(uiManager.CtxMuteChat != null) uiManager.CtxMuteChat.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxMuteVoice != null) uiManager.CtxMuteVoice.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            if(uiManager.CtxBlock != null) uiManager.CtxBlock.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            
            // Hide the separator above Mute/Block if we are hiding Mute/Block (isOther == false)
            if (uiManager.CtxSeparatorMute != null) {
                uiManager.CtxSeparatorMute.style.display = isOther ? DisplayStyle.Flex : DisplayStyle.None;
            }

            // Position
            uiManager.PartyContextMenu.style.left = position.x;
            uiManager.PartyContextMenu.style.top = position.y;
            
            uiManager.PartyContextMenu.RemoveFromClassList("hidden");
            uiManager.PartyContextMenu.BringToFront();
            
            // Show Backdrop
            if (uiManager.ContextMenuBackdrop != null) {
                uiManager.ContextMenuBackdrop.RemoveFromClassList("hidden");
                uiManager.ContextMenuBackdrop.BringToFront();
                // Ensure Menu is ABOVE Backdrop
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
                     string targetName = "Unknown";
                     if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                         var member = SessionManager.Instance.CurrentLobby.Value.Members.FirstOrDefault(m => m.Id == _contextMenuTargetId);
                         if (member.Id != 0) targetName = member.Name;
                     } 
                     
                     bool isMe = _contextMenuTargetId == SteamClient.SteamId;
                     var mainMenuManager = FindFirstObjectByType<MainMenuManager>();
                     
                     if (isMe) {
                         if (mainMenuManager != null) mainMenuManager.ShowLoadoutPanel();
                         break;
                     }
                     
                     if (mainMenuManager != null) {
                         mainMenuManager.ShowProfileView(_contextMenuTargetId, targetName, false);
                     }
                     break;
                 case "SteamProfile":
                     SteamFriends.OpenUserOverlay(_contextMenuTargetId, "steamid");
                     break;
             }
        }

        private void UpdateStatusText(string msg) {
            // Update global status
            if (uiManager != null && uiManager.MatchmakingStatusLabel != null && !string.IsNullOrEmpty(msg)) {
                uiManager.MatchmakingStatusLabel.text = msg;
            }

            // Sync Gamemode Label
            if (uiManager != null && uiManager.GamemodeDisplayLabel != null && SessionManager.Instance != null) {
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
            if (SessionManager.Instance != null) {
                await SessionManager.Instance.StartPrivateMatchSync(mode);
            }
        }

        public async UniTask<bool> HandleHostClicked(bool silent = false) {
            _isSilentHosting = silent;
            try {
                bool success = await SessionManager.Instance.CreatePrivateLobbyAsync();
                if (success) {
                    IsHost = true;
                }
                return success;
            } catch (Exception e) {
                Debug.LogException(e);
                return false;
            } finally {
                _isSilentHosting = false;
            }
        }

        public async UniTaskVoid HandleFindGameClicked(string mode = null) {
             try {
                if (uiManager != null) uiManager.SetMenuButtonsEnabled(false);
                await SessionManager.Instance.FindGameAsync(mode);
             } catch(Exception e) {
                Debug.LogException(e);
                if (uiManager != null) uiManager.SetMenuButtonsEnabled(true);
             }
        }

        public void HandleGamemodeSelected(string mode) {
            if (SessionManager.Instance == null) return;
            SessionManager.Instance.SetGamemode(mode);
        }

        public void ToggleGamemodeDropdown() {
             if (uiManager == null || uiManager.GamemodeDropdownMenu == null) return;
             
             // Only host can toggle
             if (!IsHost) return;
 
             bool isHidden = uiManager.GamemodeDropdownMenu.ClassListContains("hidden");
             if (isHidden) {
                 uiManager.GamemodeDropdownMenu.RemoveFromClassList("hidden");
                 UISoundService.PlayButtonClick();
             } else {
                 uiManager.GamemodeDropdownMenu.AddToClassList("hidden");
                 UISoundService.PlayButtonClick(isBack: true);
             }
        }

        public void HandleCancelMatchmakingClicked() {
            if (uiManager != null) {
                // Play sound (back) handled by UI button usually, but if not:
                // Check if user complained about double sound. "cancel button has two button click sounds"
                // So we remove it here.
                
                // Call session manager logic
                if (SessionManager.Instance != null) {
                    SessionManager.Instance.CancelMatchmaking();
                }
                
                // Re-enable global buttons immediately if we want instant feedback?
                // But SessionManager update loop should handle it via 'IsSearching' property check.
                // But let's force it to ensure responsiveness.
                uiManager.SetMenuButtonsEnabled(true);
            }
        }

        private void RefreshPlayerList(Lobby lobby) {
            if (uiManager == null || uiManager.PartyContainer == null) return;
            
            // Update Global Party UI (Top Right)
            UpdateGlobalPartyUI(lobby).Forget();

            // Check host status (ownership transfer?)
            bool amIHost = lobby.Owner.Id == SteamClient.SteamId;
            if (amIHost != IsHost) {
                IsHost = amIHost;
                UpdateHostStatus(IsHost);
            }
        }

        private void DrawSoloPlayer() {
            if (uiManager == null) return;
            
            _partyMembersList?.Clear();
            _localProfileContainer?.Clear();

            // Draw just us in the local profile section
            CreatePlayerRow(SteamClient.Name, SteamClient.SteamId, true, _localProfileContainer).Forget();

            // Show invite button and separator
            if (_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if (_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        private async UniTaskVoid UpdateGlobalPartyUI(Lobby lobby) {
            if (uiManager == null) return;

            _partyMembersList?.Clear();
            _localProfileContainer?.Clear();

            // Host indicator logic: find who is host
            SteamId hostId = lobby.Owner.Id;
            string myPartyId = SessionManager.Instance?.CurrentPartyId;

            foreach (var member in lobby.Members) {
                bool inMyParty = lobby.GetMemberData(member, "PartyId") == myPartyId;

                if (member.Id == SteamClient.SteamId) {
                    // Local player always goes to the profile section
                    await CreatePlayerRow(member.Name, member.Id, true, _localProfileContainer, member.Id == hostId, inMyParty);
                } else {
                    // Other members go to the party members row
                    await CreatePlayerRow(member.Name, member.Id, false, _partyMembersList, member.Id == hostId, inMyParty);
                }
            }

            // Always show invite button and separator if in a lobby
            if (_inviteButton != null) _inviteButton.style.display = DisplayStyle.Flex;
            if (_partySeparator != null) _partySeparator.style.display = DisplayStyle.Flex;
        }

        private async UniTask CreatePlayerRow(string name, SteamId id, bool isLocal, VisualElement targetContainer, bool isHost = false, bool isPartyMember = false) {
            if (targetContainer == null) return;

            if (uiManager != null && uiManager.PartyMemberTemplate != null) {
                // Instantiate from UXML Template
                TemplateContainer instance = uiManager.PartyMemberTemplate.Instantiate();
                VisualElement row = instance.Q("party-member-row");
                VisualElement avatarBox = instance.Q("avatar-box");
                Label nameLabel = instance.Q<Label>("player-name-label");

                // Logic: Local Player vs Party Member styles
                // NOTE: Most styles should be in USS classes on 'party-member-row', 
                // but preserving existing dynamic logic here for parity.
                
                if (!isLocal) {
                    row.AddToClassList("party-member-entry");
                    row.style.backgroundColor = new StyleColor(new UnityEngine.Color(0, 0, 0, 0.4f));
                    row.style.marginRight = 8;
                } else {
                    row.style.backgroundColor = new StyleColor(StyleKeyword.Null);
                    row.style.marginRight = 0;
                }

                // Host Indicator Check
                bool showHostIndicator = isHost;
                if (isLocal && isHost) {
                     int memberCount = 1;
                     if (SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                         memberCount = SessionManager.Instance.CurrentLobby.Value.MemberCount;
                     }
                     if (memberCount <= 1) showHostIndicator = false;
                }

                // Apply Avatar Box Border Styles (Host/Party Colors)
                // Ideally this would be state based in USS (e.g. .host-border), but keeping logic for now.
                var hostColor = new UnityEngine.Color(1, 0.8f, 0, 0.6f);
                var partyColor = new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f);
                
                float borderSize = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0);
                StyleColor borderColor = showHostIndicator ? new StyleColor(hostColor) : (isPartyMember ? new StyleColor(partyColor) : new StyleColor(StyleKeyword.Null));

                avatarBox.style.borderTopWidth = borderSize;
                avatarBox.style.borderBottomWidth = borderSize;
                avatarBox.style.borderLeftWidth = borderSize;
                avatarBox.style.borderRightWidth = borderSize;
                
                avatarBox.style.borderTopColor = borderColor;
                avatarBox.style.borderBottomColor = borderColor;
                avatarBox.style.borderLeftColor = borderColor;
                avatarBox.style.borderRightColor = borderColor;

                // Set Data
                nameLabel.text = name;

                var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
                if (avatarTex != null) {
                    avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
                }

                // Events
                row.RegisterCallback<PointerDownEvent>(evt => {
                    if(evt.button == 1) { // Right Click
                        ShowContextMenu(evt.position, id, isLocal, IsHost, isHost);
                        evt.StopPropagation(); 
                    }
                });

                // Add to Container (Unwrap from TemplateContainer)
                targetContainer.Add(row);
            } else {
                Debug.LogError("[MainMenuSessionManager] PartyMemberTemplate is missing in UIManager!");
            }
        }


        private void UpdateHostStatus(bool isHost) {
             OnHostStatusChanged?.Invoke(isHost, !isHost);
        }
        
        public bool IsHost { get; private set; }
    }
}
