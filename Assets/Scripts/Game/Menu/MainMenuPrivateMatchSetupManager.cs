using System;
using System.Collections.Generic;
using System.Linq;
using Game.Match;
using Game.Social;
using Game.UI;
using Network;
using Network.Services;
using Network.Steam;
using Steamworks;
using Steamworks.Data;
using UnityEngine;
using UnityEngine.UIElements;
using Lobby = Steamworks.Data.Lobby;
using Cysharp.Threading.Tasks;

namespace Game.Menu {
    /// <summary>
    /// WS-C skeleton manager for private match setup.
    /// WS-D: map filtering, Gun Tag conditional visibility, validation and disabled start guardrails.
    /// Owns a runtime-only draft settings model and emits Back/Start actions.
    /// </summary>
    public class MainMenuPrivateMatchSetupManager : UIElementBase {
        public struct PrivateMatchDraftSettings {
            public string GamemodeId;
            public string MapId;
            public int MatchTimerSeconds;
            public int ScoreToWin;
            public int TaggedPlayers;
        }

        [Header("Defaults")]
        [SerializeField] private int defaultTaggedPlayers = 1;

        public Action OnBackRequested;
        public Action<PrivateMatchDraftSettings> OnStartRequested;

        private VisualTreeAsset _partyMemberTemplate;
        private MainMenuSessionManager _sessionManager;

        private DropdownField _gamemodeDropdown;
        private DropdownField _mapDropdown;
        private IntegerField _matchTimerField;
        private IntegerField _scoreToWinField;
        private IntegerField _taggedPlayersField;
        private Button _startButton;
        private Button _backButton;
        private Label _statusLabel;
        private Label _validationLabel;
        private VisualElement _taggedRow;

        // WS-E: Team preview (FFA list vs team A / VS / team B)
        private VisualElement _previewFfa;
        private VisualElement _previewTeams;
        private ScrollView _ffaList;
        private ScrollView _teamAList;
        private ScrollView _teamBList;

        /// <summary> Draft team index per SteamId (0 = team A, 1 = team B). Only used for team-based gamemodes. </summary>
        private readonly Dictionary<ulong, int> _draftTeamBySteamId = new();

        private readonly List<MapDefinition> _filteredMaps = new();
        private PrivateMatchDraftSettings _draft;
        private bool _suppressEvents;

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "private-match-gamemode-dropdown", typeof(DropdownField) },
                { "private-match-map-dropdown", typeof(DropdownField) },
                { "private-match-timer-input", typeof(IntegerField) },
                { "private-match-score-input", typeof(IntegerField) },
                { "private-match-tagged-input", typeof(IntegerField) },
                { "private-match-start-button", typeof(Button) }
            };
        }

        protected override void OnInitialize() {
            _gamemodeDropdown = QRequired<DropdownField>("private-match-gamemode-dropdown");
            _mapDropdown = QRequired<DropdownField>("private-match-map-dropdown");
            _matchTimerField = QRequired<IntegerField>("private-match-timer-input");
            _scoreToWinField = QRequired<IntegerField>("private-match-score-input");
            _taggedPlayersField = QRequired<IntegerField>("private-match-tagged-input");
            _startButton = QRequired<Button>("private-match-start-button");
            _backButton = QOptional<Button>("private-match-back-button");
            _statusLabel = QOptional<Label>("private-match-status-label");
            _validationLabel = QOptional<Label>("private-match-validation-label");
            _taggedRow = QOptional<VisualElement>("private-match-tagged-row");
            _previewFfa = Root?.Q("private-match-preview-ffa");
            _previewTeams = Root?.Q("private-match-preview-teams");
            _ffaList = Root?.Q<ScrollView>("private-match-ffa-list");
            _teamAList = Root?.Q<ScrollView>("private-match-team-a-list");
            _teamBList = Root?.Q<ScrollView>("private-match-team-b-list");

            SetupDefaults();
            BindEvents();
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshValidationAndStartButton();
            RefreshTeamPreview();
        }

        protected override void OnEnable() {
            base.OnEnable();
            if(SessionManager.Instance != null)
                SessionManager.Instance.OnPartyStateChanged += OnPartyStateChanged;
        }

        protected override void OnDisable() {
            if(SessionManager.HasInstance && SessionManager.Instance != null)
                SessionManager.Instance.OnPartyStateChanged -= OnPartyStateChanged;
            base.OnDisable();
        }

        private void OnPartyStateChanged() {
            RefreshTeamPreview();
        }

        public void SetPartyMemberTemplate(VisualTreeAsset template) {
            _partyMemberTemplate = template;
        }

        public void SetSessionManager(MainMenuSessionManager sessionManager) {
            _sessionManager = sessionManager;
        }

        public PrivateMatchDraftSettings GetDraftSettings() => _draft;

        /// <summary> Returns a read-only copy of draft team assignments (SteamId -> 0 or 1) for session launch. </summary>
        public IReadOnlyDictionary<ulong, int> GetDraftTeamAssignments() {
            if(_draftTeamBySteamId == null || _draftTeamBySteamId.Count == 0)
                return null;
            var copy = new Dictionary<ulong, int>(_draftTeamBySteamId);
            return copy;
        }

        /// <summary>
        /// Call when the private match panel is shown so dropdowns are populated (gamemode list and map list from Resources).
        /// </summary>
        public void RefreshDropdowns() {
            if(_gamemodeDropdown == null || _mapDropdown == null) return;
            var modeChoices = BuildGamemodeChoices();
            _gamemodeDropdown.choices = modeChoices;
            if(modeChoices.Count > 0 && string.IsNullOrWhiteSpace(_draft.GamemodeId)) {
                _draft.GamemodeId = modeChoices[0];
            }
            if(modeChoices.Count > 0) {
                var currentMode = _draft.GamemodeId;
                if(!modeChoices.Contains(currentMode)) currentMode = modeChoices[0];
                _draft.GamemodeId = currentMode;
                _suppressEvents = true;
                _gamemodeDropdown.value = currentMode;
                _suppressEvents = false;
            }
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshValidationAndStartButton();
        }

        public void SetInitialGamemode(string gamemodeId) {
            if(string.IsNullOrWhiteSpace(gamemodeId)) return;
            if(string.Equals(_draft.GamemodeId, gamemodeId, StringComparison.Ordinal)) return;

            _draft.GamemodeId = gamemodeId;
            ApplyGamemodeDefaults(gamemodeId);
            if(_gamemodeDropdown != null) {
                _suppressEvents = true;
                _gamemodeDropdown.value = gamemodeId;
                _suppressEvents = false;
            }
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshValidationAndStartButton();
        }

        private void SetupDefaults() {
            var modeChoices = BuildGamemodeChoices();
            _gamemodeDropdown.choices = modeChoices;
            var initialMode = modeChoices.Count > 0 ? modeChoices[0] : "Deathmatch";

            _draft = new PrivateMatchDraftSettings {
                GamemodeId = initialMode,
                MatchTimerSeconds = Mathf.Max(60, GetDefaultMatchTimerForGamemode(initialMode)),
                ScoreToWin = Mathf.Max(1, GetDefaultScoreToWinForGamemode(initialMode)),
                TaggedPlayers = Mathf.Max(1, defaultTaggedPlayers),
                MapId = string.Empty
            };

            _suppressEvents = true;
            _gamemodeDropdown.value = _draft.GamemodeId;
            _matchTimerField.value = _draft.MatchTimerSeconds;
            _scoreToWinField.value = _draft.ScoreToWin;
            _taggedPlayersField.value = _draft.TaggedPlayers;
            _suppressEvents = false;
            RefreshTaggedRowVisibility();
        }

        private void BindEvents() {
            _gamemodeDropdown.RegisterValueChangedCallback(OnGamemodeChanged);
            RegisterCleanup(() => _gamemodeDropdown.UnregisterValueChangedCallback(OnGamemodeChanged));

            _mapDropdown.RegisterValueChangedCallback(OnMapChanged);
            RegisterCleanup(() => _mapDropdown.UnregisterValueChangedCallback(OnMapChanged));

            _matchTimerField.RegisterValueChangedCallback(OnTimerChanged);
            RegisterCleanup(() => _matchTimerField.UnregisterValueChangedCallback(OnTimerChanged));
            RegisterSanitizeOnCommit(_matchTimerField, 60, int.MaxValue, v => _draft.MatchTimerSeconds = v);

            _scoreToWinField.RegisterValueChangedCallback(OnScoreChanged);
            RegisterCleanup(() => _scoreToWinField.UnregisterValueChangedCallback(OnScoreChanged));
            RegisterSanitizeOnCommit(_scoreToWinField, 1, int.MaxValue, v => _draft.ScoreToWin = v);

            _taggedPlayersField.RegisterValueChangedCallback(OnTaggedPlayersChanged);
            RegisterCleanup(() => _taggedPlayersField.UnregisterValueChangedCallback(OnTaggedPlayersChanged));
            RegisterSanitizeOnCommit(_taggedPlayersField, 1, int.MaxValue, v => _draft.TaggedPlayers = v);

            _startButton.clicked += OnStartClicked;
            RegisterCleanup(() => _startButton.clicked -= OnStartClicked);

            // Back button is wired by MainMenuManager so Back always returns to Gamemode Select
        }

        private List<string> BuildGamemodeChoices() {
            var choices = new List<string>();
            var settings = MatchSettingsManager.Instance;
            if(settings != null && settings.gamemodeDefinitions != null && settings.gamemodeDefinitions.Count > 0) {
                for(var i = 0; i < settings.gamemodeDefinitions.Count; i++) {
                    var id = settings.gamemodeDefinitions[i].id;
                    if(string.IsNullOrWhiteSpace(id)) continue;
                    choices.Add(id);
                }
            }
            if(choices.Count == 0) {
                choices.Add("Deathmatch");
                choices.Add("Team Deathmatch");
                choices.Add("Hopball");
                choices.Add("KOTH");
                choices.Add("Gun Tag");
            }
            return choices;
        }

        private static int GetDefaultMatchTimerForGamemode(string gamemodeId) {
            var settings = MatchSettingsManager.Instance;
            return settings != null ? Mathf.Max(60, settings.defaultMatchDurationSeconds) : 600;
        }

        private static int GetDefaultScoreToWinForGamemode(string gamemodeId) {
            if(string.IsNullOrWhiteSpace(gamemodeId)) return 50;
            if(string.Equals(gamemodeId, "Hopball", StringComparison.OrdinalIgnoreCase)) return 60;
            if(string.Equals(gamemodeId, "KOTH", StringComparison.OrdinalIgnoreCase)) return 200;
            return 50;
        }

        private void ApplyGamemodeDefaults(string gamemodeId) {
            _draft.MatchTimerSeconds = Mathf.Max(60, GetDefaultMatchTimerForGamemode(gamemodeId));
            _draft.ScoreToWin = Mathf.Max(1, GetDefaultScoreToWinForGamemode(gamemodeId));
            if(_matchTimerField != null) {
                _suppressEvents = true;
                _matchTimerField.value = _draft.MatchTimerSeconds;
                _suppressEvents = false;
            }
            if(_scoreToWinField != null) {
                _suppressEvents = true;
                _scoreToWinField.value = _draft.ScoreToWin;
                _suppressEvents = false;
            }
        }

        private void RefreshMapChoicesForGamemode(string gamemodeId) {
            _filteredMaps.Clear();
            var choices = new List<string>();
            var pool = Resources.Load<MapPoolDefinition>("MatchMapPoolDefinition");
            var poolOk = pool != null;
            var mapsCount = pool?.Maps?.Count ?? 0;
            if(poolOk && pool.Maps != null) {
                for(var i = 0; i < pool.Maps.Count; i++) {
                    var map = pool.Maps[i];
                    if(map == null || map.EnabledInRotation == false) continue;
                    if(map.SupportsGamemode(gamemodeId) == false) continue;
                    _filteredMaps.Add(map);
                    choices.Add(BuildMapChoiceLabel(map));
                }
            }

            if(choices.Count == 0) {
                _draft.MapId = MatchMapService.DefaultMapId;
                choices.Add(_draft.MapId.ToUpperInvariant());
                _mapDropdown.choices = choices;
                _suppressEvents = true;
                _mapDropdown.value = choices[0];
                _suppressEvents = false;
                RefreshValidationAndStartButton();
                return;
            }

            _mapDropdown.choices = choices;

            // Auto-correct: if current map not in filtered list, select first valid map (WS-D)
            var selectedIndex = 0;
            for(var i = 0; i < _filteredMaps.Count; i++) {
                if(string.Equals(_filteredMaps[i].MapId, _draft.MapId, StringComparison.OrdinalIgnoreCase)) {
                    selectedIndex = i;
                    break;
                }
            }

            _draft.MapId = _filteredMaps[selectedIndex].MapId;
            _suppressEvents = true;
            _mapDropdown.value = choices[selectedIndex];
            _suppressEvents = false;
            RefreshValidationAndStartButton();
        }

        private static bool IsGunTag(string gamemodeId) {
            return string.Equals(gamemodeId, "Gun Tag", StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshTaggedRowVisibility() {
            if(_taggedRow == null) return;
            var show = IsGunTag(_draft.GamemodeId);
            if(show) {
                _taggedRow.RemoveFromClassList("hidden");
                _taggedRow.style.display = DisplayStyle.Flex;
            } else {
                _taggedRow.AddToClassList("hidden");
                _taggedRow.style.display = DisplayStyle.None;
            }
        }

        private int GetPartySize() {
            return SessionManager.Instance != null ? SessionManager.Instance.CurrentPartySize : 1;
        }

        private void RefreshValidationAndStartButton() {
            var validationMessage = "";
            var canStart = true;

            // No valid maps for this gamemode: disable start and show explanation (WS-D)
            if(_filteredMaps.Count == 0) {
                canStart = false;
                validationMessage = "No maps available for this gamemode.";
            } else if(IsGunTag(_draft.GamemodeId)) {
                var partySize = GetPartySize();
                if(_draft.TaggedPlayers > partySize) {
                    validationMessage = $"Tagged players cannot exceed party size ({partySize}).";
                    _draft.TaggedPlayers = Mathf.Clamp(_draft.TaggedPlayers, 1, partySize);
                    if(_taggedPlayersField != null) {
                        _suppressEvents = true;
                        _taggedPlayersField.value = _draft.TaggedPlayers;
                        _suppressEvents = false;
                    }
                }
            }

            if(_validationLabel != null)
                _validationLabel.text = validationMessage;

            if(_startButton != null) {
                _startButton.SetEnabled(canStart);
            }
        }

        /// <summary> WS-E: Build team preview (FFA single list or team A / VS / team B). Updates on party, gamemode, and team swap. </summary>
        public void RefreshTeamPreview() {
            if(_previewFfa == null || _previewTeams == null) return;

            var isTeamBased = MatchSettingsManager.IsTeamBasedMode(_draft.GamemodeId);
            var members = GetCurrentMembersOrdered();
            EnsureDraftTeamsForMembers(members, isTeamBased);

            if(isTeamBased) {
                _previewFfa.AddToClassList("hidden");
                _previewFfa.style.display = DisplayStyle.None;
                _previewTeams.RemoveFromClassList("hidden");
                _previewTeams.style.display = DisplayStyle.Flex;
                var teamAContainer = _teamAList != null ? _teamAList.contentContainer : null;
                var teamBContainer = _teamBList != null ? _teamBList.contentContainer : null;
                if(teamAContainer != null) teamAContainer.Clear();
                if(teamBContainer != null) teamBContainer.Clear();
                foreach(var m in members) {
                    var team = _draftTeamBySteamId.TryGetValue(m.Id.Value, out var t) ? t : 0;
                    var container = team == 0 ? teamAContainer : teamBContainer;
                    if(container != null)
                        CreatePreviewRow(m, container, isTeamBased);
                }
            } else {
                _previewTeams.AddToClassList("hidden");
                _previewTeams.style.display = DisplayStyle.None;
                _previewFfa.RemoveFromClassList("hidden");
                _previewFfa.style.display = DisplayStyle.Flex;
                var ffaContainer = _ffaList != null ? _ffaList.contentContainer : null;
                if(ffaContainer != null) ffaContainer.Clear();
                foreach(var m in members)
                    if(ffaContainer != null)
                        CreatePreviewRow(m, ffaContainer, false);
            }
        }

        private List<Friend> GetCurrentMembersOrdered() {
            var list = new List<Friend>();
            if(SessionManager.Instance == null || !SessionManager.Instance.CurrentLobby.HasValue) {
                if(SteamClient.IsValid && SteamClient.IsLoggedOn)
                    list.Add(new Friend(SteamClient.SteamId));
                return list;
            }
            var lobby = SessionManager.Instance.CurrentLobby.Value;
            foreach(var member in lobby.Members)
                list.Add(member);
            return list;
        }

        private void EnsureDraftTeamsForMembers(List<Friend> members, bool isTeamBased) {
            if(!isTeamBased) return;
            var index = 0;
            foreach(var m in members) {
                if(!_draftTeamBySteamId.ContainsKey(m.Id.Value))
                    _draftTeamBySteamId[m.Id.Value] = index % 2;
                index++;
            }
            var toRemove = _draftTeamBySteamId.Keys.Where(k => members.All(f => f.Id.Value != k)).ToList();
            foreach(var k in toRemove) _draftTeamBySteamId.Remove(k);
        }

        /// <summary> WS-F: Host-only team switch for team modes. </summary>
        public void SwitchPlayerTeam(SteamId steamId) {
            if(!MatchSettingsManager.IsTeamBasedMode(_draft.GamemodeId)) return;
            if(_draftTeamBySteamId.TryGetValue(steamId.Value, out var current))
                _draftTeamBySteamId[steamId.Value] = 1 - current;
            else
                _draftTeamBySteamId[steamId.Value] = 1;
            RefreshTeamPreview();
        }

        private void CreatePreviewRow(Friend member, VisualElement container, bool isTeamBased) {
            if(container == null) return;
            if(_partyMemberTemplate == null) {
                Debug.LogWarning("[PrivateMatchSetup] CreatePreviewRow: _partyMemberTemplate is null, cannot create row.");
                return;
            }

            var instance = _partyMemberTemplate.Instantiate();
            var row = instance.Q("party-member-row");
            var avatarBox = instance.Q("avatar-box");
            var nameLabel = instance.Q<Label>("player-name-label");
            var localXpRow = instance.Q<VisualElement>("local-xp-row");
            if(row == null || nameLabel == null) return;
            if(avatarBox == null && Debug.isDebugBuild)
                Debug.LogWarning("[PrivateMatchSetup] CreatePreviewRow: template has no 'avatar-box', Steam pics will not show.");

            var displayName = member.Name;
            var avatarHidden = false;
            var iconId = "";
            if(SessionManager.Instance != null && SessionManager.Instance.CurrentLobby.HasValue) {
                var lobby = SessionManager.Instance.CurrentLobby.Value;
                var fromLobby = lobby.GetMemberData(member, "DisplayName");
                if(!string.IsNullOrEmpty(fromLobby)) displayName = fromLobby;
                avatarHidden = lobby.GetMemberData(member, "AvatarHidden") == "1";
                iconId = lobby.GetMemberData(member, "PlayerIcon");
            }
            if(string.IsNullOrEmpty(iconId))
                iconId = PlayerIconPicker.PickDeterministicIconId(member.Id.Value, avatarHidden);

            nameLabel.text = displayName;
            if(localXpRow != null) {
                localXpRow.AddToClassList("hidden");
                localXpRow.style.display = DisplayStyle.None;
            }

            if(avatarBox != null) {
                ApplyPreviewRowAvatarFallback(avatarBox, iconId, avatarHidden);
                LoadSteamAvatarIntoPreviewRow(avatarBox, member.Id, iconId, avatarHidden);
            }

            var steamId = member.Id;
            row.RegisterCallback<PointerDownEvent>(evt => {
                if(evt.button != 1) return;
                // Use current draft gamemode at click time. Host can switch any player (including themselves).
                var showSwitchTeam = MatchSettingsManager.IsTeamBasedMode(_draft.GamemodeId)
                    && _sessionManager != null && _sessionManager.IsHost;
                if(Debug.isDebugBuild) {
                    Debug.Log($"[PrivateMatchSetup] Row right-click: steamId={steamId.Value} gamemode={_draft.GamemodeId} showSwitchTeam={showSwitchTeam}");
                }
                _sessionManager?.ShowContextMenuForPartyMember(evt.position, steamId, showSwitchTeam);
                evt.StopPropagation();
            });
            container.Add(row);
        }

        private static void ApplyPreviewRowAvatarFallback(VisualElement avatarBox, string iconId, bool hideAvatar) {
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

        private static async void LoadSteamAvatarIntoPreviewRow(VisualElement avatarBox, SteamId id, string iconId, bool hideAvatar) {
            if(Debug.isDebugBuild) {
                Debug.Log($"[PrivateMatchSetup] LoadSteamAvatar: steamId={id.Value} avatarBox={avatarBox != null} hideAvatar={hideAvatar} SteamValid={SteamClient.IsValid} SteamLoggedOn={SteamClient.IsLoggedOn} SteamManager={SteamManager.Instance != null}");
            }
            if(avatarBox == null || hideAvatar || id.Value == 0) return;
            if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;
            if(SteamManager.Instance == null) return;

            avatarBox.RemoveFromClassList("steam-avatar-flip");
            avatarBox.RemoveFromClassList("default-avatar");
            avatarBox.RemoveFromClassList("player-icon-red");
            avatarBox.RemoveFromClassList("player-icon-orange");
            avatarBox.RemoveFromClassList("player-icon-yellow");
            avatarBox.RemoveFromClassList("player-icon-green");
            avatarBox.RemoveFromClassList("player-icon-blue");
            avatarBox.RemoveFromClassList("player-icon-purple");
            avatarBox.RemoveFromClassList("player-icon-white");

            try {
                var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
                if(Debug.isDebugBuild) {
                    Debug.Log($"[PrivateMatchSetup] LoadSteamAvatar result: steamId={id.Value} avatarTex={avatarTex != null} avatarBox={avatarBox != null} panel={avatarBox?.panel != null}");
                }
                // Apply texture when we have it; avoid relying on panel (often false when async completes before layout).
                if(avatarTex != null && avatarBox != null) {
                    avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
                    if(!avatarBox.ClassListContains("steam-avatar-flip"))
                        avatarBox.AddToClassList("steam-avatar-flip");
                }
            } catch (Exception ex) {
                if(Debug.isDebugBuild) Debug.LogWarning($"[PrivateMatchSetup] LoadSteamAvatar failed for {id.Value}: {ex.Message}");
            }
        }

        private static string BuildMapChoiceLabel(MapDefinition map) {
            if(map == null) return "UNKNOWN";
            if(string.IsNullOrWhiteSpace(map.MapId)) {
                return map.name.ToUpperInvariant();
            }
            return map.MapId.Trim().ToUpperInvariant();
        }

        private void OnGamemodeChanged(ChangeEvent<string> evt) {
            if(_suppressEvents) return;
            _draft.GamemodeId = string.IsNullOrWhiteSpace(evt.newValue) ? "Deathmatch" : evt.newValue;
            ApplyGamemodeDefaults(_draft.GamemodeId);
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshValidationAndStartButton();
            RefreshTeamPreview();
        }

        private void OnMapChanged(ChangeEvent<string> evt) {
            if(_suppressEvents) return;
            for(var i = 0; i < _filteredMaps.Count; i++) {
                var label = BuildMapChoiceLabel(_filteredMaps[i]);
                if(string.Equals(label, evt.newValue, StringComparison.OrdinalIgnoreCase)) {
                    _draft.MapId = _filteredMaps[i].MapId;
                    break;
                }
            }
            RefreshStatusLabel();
            RefreshValidationAndStartButton();
        }

        private void OnTimerChanged(ChangeEvent<int> evt) {
            if(_suppressEvents) return;
            _draft.MatchTimerSeconds = evt.newValue;
            RefreshStatusLabel();
        }

        private void OnScoreChanged(ChangeEvent<int> evt) {
            if(_suppressEvents) return;
            _draft.ScoreToWin = evt.newValue;
            RefreshStatusLabel();
        }

        /// <summary>
        /// Sanitize (clamp) the integer field only when the user commits (blur or Enter), not on every keystroke.
        /// </summary>
        private void RegisterSanitizeOnCommit(IntegerField field, int min, int max, Action<int> setDraft) {
            if(field == null) return;
            void SanitizeAndApply() {
                var raw = field.value;
                var clamped = Mathf.Clamp(raw, min, max);
                if(clamped != raw) {
                    _suppressEvents = true;
                    field.value = clamped;
                    _suppressEvents = false;
                    setDraft(clamped);
                } else {
                    setDraft(clamped);
                }
                RefreshStatusLabel();
                RefreshValidationAndStartButton();
            }

            EventCallback<BlurEvent> blurHandler = _ => SanitizeAndApply();
            EventCallback<KeyDownEvent> keyHandler = evt => {
                if(evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                SanitizeAndApply();
            };
            field.RegisterCallback(blurHandler);
            field.RegisterCallback(keyHandler);
            RegisterCleanup(() => field.UnregisterCallback(blurHandler));
            RegisterCleanup(() => field.UnregisterCallback(keyHandler));
        }

        private void OnTaggedPlayersChanged(ChangeEvent<int> evt) {
            if(_suppressEvents) return;
            _draft.TaggedPlayers = evt.newValue;
            RefreshStatusLabel();
        }

        private void OnStartClicked() {
            UISoundService.PlayButtonClick();
            OnStartRequested?.Invoke(_draft);
        }

        private void RefreshStatusLabel() {
            if(_statusLabel == null) return;
            var mode = string.IsNullOrWhiteSpace(_draft.GamemodeId) ? "UNKNOWN" : _draft.GamemodeId.ToUpperInvariant();
            var map = string.IsNullOrWhiteSpace(_draft.MapId) ? "UNKNOWN" : _draft.MapId.ToUpperInvariant();
            _statusLabel.text =
                $"MODE: {mode}  |  MAP: {map}  |  TIME: {_draft.MatchTimerSeconds}s  |  SCORE: {_draft.ScoreToWin}";
        }
    }
}
