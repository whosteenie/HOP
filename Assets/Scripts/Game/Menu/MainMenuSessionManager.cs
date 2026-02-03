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
            if (SessionManager.Instance != null) {
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

            // Only update buttons if state changed? 
            // Better to just enforce it, SetMenuButtonsEnabled checks current state anyway (mostly).
            // But checking local input/interaction state might be needed.
            // For now, enforcing it ensures we don't drift.
            if (uiManager != null) {
                uiManager.SetMenuButtonsEnabled((!isSearching && !isPartyMember) || _isSilentHosting);

                if (uiManager.StatusContainer != null) {
                    var targetDisplay = showStatus ? DisplayStyle.Flex : DisplayStyle.None;
                    if (uiManager.StatusContainer.style.display != targetDisplay) {
                        uiManager.StatusContainer.style.display = targetDisplay;
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

            var row = new VisualElement {
                style = {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    backgroundColor = isLocal ? StyleKeyword.Null : new UnityEngine.Color(0, 0, 0, 0.4f),
                    paddingBottom = 4, paddingTop = 4, paddingLeft = 8, paddingRight = 8,
                    borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    marginRight = isLocal ? 0 : 8
                }
            };
            
            if (!isLocal) row.AddToClassList("party-member-entry");

            bool showHostIndicator = isHost && !isLocal;

            // Avatar
            var avatarBox = new VisualElement {
                style = {
                    width = 40, height = 40,
                    backgroundColor = new UnityEngine.Color(0.2f, 0.2f, 0.2f),
                    marginRight = 10,
                    borderTopWidth = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0),
                    borderBottomWidth = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0),
                    borderLeftWidth = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0),
                    borderRightWidth = showHostIndicator ? 2 : (isPartyMember && !isLocal ? 1 : 0),
                    borderTopColor = showHostIndicator ? new UnityEngine.Color(1, 0.8f, 0, 0.6f) : (isPartyMember ? new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f) : StyleKeyword.Null),
                    borderBottomColor = showHostIndicator ? new UnityEngine.Color(1, 0.8f, 0, 0.6f) : (isPartyMember ? new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f) : StyleKeyword.Null),
                    borderLeftColor = showHostIndicator ? new UnityEngine.Color(1, 0.8f, 0, 0.6f) : (isPartyMember ? new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f) : StyleKeyword.Null),
                    borderRightColor = showHostIndicator ? new UnityEngine.Color(1, 0.8f, 0, 0.6f) : (isPartyMember ? new UnityEngine.Color(0.2f, 0.6f, 1f, 0.6f) : StyleKeyword.Null)
                }
            };
            avatarBox.AddToClassList("steam-avatar");
            
            var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
            if (avatarTex != null) {
                avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
            }
            row.Add(avatarBox);

            // Name
            var nameLabel = new Label(name) {
                style = {
                    color = UnityEngine.Color.white,
                    fontSize = 13,
                    unityFontStyleAndWeight = FontStyle.Bold
                }
            };
            row.Add(nameLabel);

            targetContainer.Add(row);
        }

        private void UpdateHostStatus(bool isHost) {
             OnHostStatusChanged?.Invoke(isHost, !isHost);
        }
        
        public bool IsHost { get; private set; }
    }
}
