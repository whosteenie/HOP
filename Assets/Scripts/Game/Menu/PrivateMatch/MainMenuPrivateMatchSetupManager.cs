using System;
using System.Collections.Generic;
using System.Linq;
using Diagnostics;
using Events;
using Game.Match;
using Game.Social;
using Game.UI.Core;
using Network.Steam;
using Steamworks;
using UnityEngine;
using UnityEngine.UIElements;
using SessionManager = Network.Session.SessionManager;

namespace Game.Menu.PrivateMatch {
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
            public bool UsePreMatchCountdown;
            public bool SwapWeaponsOnDeath;
            public int ScoreToWin;
            public int KothHillSpeed;
            public int TaggedPlayers;
        }

        [Header("Defaults")]
        [SerializeField] private int defaultTaggedPlayers = 1;

        public Action OnBackRequested;
        public Action<PrivateMatchDraftSettings> OnStartRequested;

        private Func<bool> _isHostResolver;
        private Action<Vector2, SteamId, bool> _showContextMenuForPartyMember;

        private DropdownField _gamemodeDropdown;
        private DropdownField _mapDropdown;
        private IntegerField _matchTimerField;
        private Button _preMatchCountdownToggle;
        private Button _swapWeaponsOnDeathToggle;
        private IntegerField _scoreToWinField;
        private SliderInt _kothHillSpeedSlider;
        private Label _kothHillSpeedValueLabel;
        private IntegerField _taggedPlayersField;
        private Button _startButton;
        private Button _backButton;
        private Label _statusLabel;
        private Label _validationLabel;
        private VisualElement _scoreToWinRow;
        private VisualElement _kothHillSpeedRow;
        private VisualElement _taggedRow;
        private VisualElement _mapPreviewImage;
        private Label _mapPreviewWipLabel;
        private Label _mapPreviewTitleLabel;

        // WS-E: Team preview (FFA list vs team A / VS / team B)
        private VisualElement _previewFfa;
        private VisualElement _previewTeams;
        private ScrollView _ffaList;
        private ScrollView _teamAList;
        private ScrollView _teamBList;

        /// <summary> Draft team index per SteamId (0 = team A, 1 = team B). Only used for team-based gamemodes. </summary>
        private readonly Dictionary<ulong, int> _draftTeamBySteamId = new();
        private const int MaxLobbySlots = 10;
        private const int TeamSlots = 5;

        private readonly List<MapDefinition> _filteredMaps = new();
        private PrivateMatchDraftSettings _draft;
        private bool _suppressEvents;
        private const string PrivateMatchDropdownPopupClass = "private-match-dropdown-popup";

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "private-match-gamemode-dropdown", typeof(DropdownField) },
                { "private-match-map-dropdown", typeof(DropdownField) },
                { "private-match-timer-input", typeof(IntegerField) },
                { "private-match-prematch-countdown-toggle", typeof(Button) },
                { "private-match-swap-on-death-toggle", typeof(Button) },
                { "private-match-score-input", typeof(IntegerField) },
                { "private-match-koth-speed-slider", typeof(SliderInt) },
                { "private-match-tagged-input", typeof(IntegerField) },
                { "private-match-start-button", typeof(Button) }
            };
        }

        protected override void OnInitialize() {
            _gamemodeDropdown = QRequired<DropdownField>("private-match-gamemode-dropdown");
            _mapDropdown = QRequired<DropdownField>("private-match-map-dropdown");
            _matchTimerField = QRequired<IntegerField>("private-match-timer-input");
            _preMatchCountdownToggle = QRequired<Button>("private-match-prematch-countdown-toggle");
            _swapWeaponsOnDeathToggle = QRequired<Button>("private-match-swap-on-death-toggle");
            _scoreToWinField = QRequired<IntegerField>("private-match-score-input");
            _kothHillSpeedSlider = QRequired<SliderInt>("private-match-koth-speed-slider");
            _kothHillSpeedValueLabel = QOptional<Label>("private-match-koth-speed-value");
            _taggedPlayersField = QRequired<IntegerField>("private-match-tagged-input");
            _startButton = QRequired<Button>("private-match-start-button");
            _backButton = QOptional<Button>("private-match-back-button");
            _statusLabel = QOptional<Label>("private-match-status-label");
            _validationLabel = QOptional<Label>("private-match-validation-label");
            _scoreToWinRow = QOptional<VisualElement>("private-match-score-row");
            _kothHillSpeedRow = QOptional<VisualElement>("private-match-koth-speed-row");
            _taggedRow = QOptional<VisualElement>("private-match-tagged-row");
            _mapPreviewImage = QOptional<VisualElement>("private-match-map-preview-image");
            _mapPreviewWipLabel = QOptional<Label>("private-match-map-preview-wip");
            _mapPreviewTitleLabel = QOptional<Label>("private-match-map-preview-title");
            _previewFfa = Root?.Q("private-match-preview-ffa");
            _previewTeams = Root?.Q("private-match-preview-teams");
            _ffaList = Root?.Q<ScrollView>("private-match-ffa-list");
            _teamAList = Root?.Q<ScrollView>("private-match-team-a-list");
            _teamBList = Root?.Q<ScrollView>("private-match-team-b-list");

            BindDropdownOpenStateClasses();
            SetupDefaults();
            BindEvents();
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshMapPreview();
            RefreshScoreToWinVisibility();
            RefreshKothHillSpeedVisibility();
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshStartButton();
            RefreshTeamPreview();
        }

        private void BindDropdownOpenStateClasses() {
            var gamemodeCleanup = DropdownOpenStateBinder.Bind(_gamemodeDropdown, PrivateMatchDropdownPopupClass);
            if(gamemodeCleanup != null) {
                RegisterCleanup(gamemodeCleanup);
            }

            var mapCleanup = DropdownOpenStateBinder.Bind(_mapDropdown, PrivateMatchDropdownPopupClass);
            if(mapCleanup != null) {
                RegisterCleanup(mapCleanup);
            }
        }

        protected void OnEnable() {
            EventBus.Unsubscribe<SessionPropertiesRefreshedEvent>(OnSessionPropertiesRefreshed);
            EventBus.Subscribe<SessionPropertiesRefreshedEvent>(OnSessionPropertiesRefreshed);
        }

        protected void OnDisable() {
            EventBus.Unsubscribe<SessionPropertiesRefreshedEvent>(OnSessionPropertiesRefreshed);
        }

        private void OnSessionPropertiesRefreshed(SessionPropertiesRefreshedEvent _) {
            RefreshTeamPreview();
        }

        public void SetSessionHooks(Func<bool> isHostResolver, Action<Vector2, SteamId, bool> showContextMenuForPartyMember) {
            _isHostResolver = isHostResolver;
            _showContextMenuForPartyMember = showContextMenuForPartyMember;
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
            RefreshMapPreview();
            RefreshScoreToWinVisibility();
            RefreshKothHillSpeedVisibility();
            RefreshTaggedRowVisibility();
            RefreshStatusLabel();
            RefreshStartButton();
        }

        private void SetupDefaults() {
            var modeChoices = BuildGamemodeChoices();
            _gamemodeDropdown.choices = modeChoices;
            var initialMode = modeChoices.Count > 0 ? modeChoices[0] : "Deathmatch";

            _draft = new PrivateMatchDraftSettings {
                GamemodeId = initialMode,
                MatchTimerSeconds = Mathf.Max(60, GetDefaultMatchTimer()),
                UsePreMatchCountdown = true,
                SwapWeaponsOnDeath = true,
                ScoreToWin = Mathf.Max(1, GetDefaultScoreToWin(initialMode)),
                KothHillSpeed = Mathf.Clamp(GetDefaultKothHillSpeedPercent(), KothHillSpeedMinPercent, KothHillSpeedMaxPercent),
                TaggedPlayers = Mathf.Max(1, defaultTaggedPlayers),
                MapId = string.Empty
            };

            _suppressEvents = true;
            _gamemodeDropdown.value = _draft.GamemodeId;
            _matchTimerField.value = _draft.MatchTimerSeconds;
            _scoreToWinField.value = _draft.ScoreToWin;
            _kothHillSpeedSlider.value = _draft.KothHillSpeed;
            _taggedPlayersField.value = _draft.TaggedPlayers;
            SetCheckboxValue(_preMatchCountdownToggle, _draft.UsePreMatchCountdown);
            SetCheckboxValue(_swapWeaponsOnDeathToggle, _draft.SwapWeaponsOnDeath);
            _suppressEvents = false;
            ApplyInfiniteFieldDisplay(_matchTimerField, _draft.MatchTimerSeconds == 0);
            ApplyInfiniteFieldDisplay(_scoreToWinField, _draft.ScoreToWin == 0);
            RefreshKothHillSpeedDisplay();
            RefreshScoreToWinVisibility();
            RefreshKothHillSpeedVisibility();
            RefreshTaggedRowVisibility();
        }

        private void BindEvents() {
            RegisterDigitsOnlyInput(_matchTimerField);
            RegisterDigitsOnlyInput(_scoreToWinField);
            RegisterDigitsOnlyInput(_taggedPlayersField);

            _gamemodeDropdown.RegisterValueChangedCallback(OnGamemodeChanged);
            RegisterCleanup(() => _gamemodeDropdown.UnregisterValueChangedCallback(OnGamemodeChanged));

            _mapDropdown.RegisterValueChangedCallback(OnMapChanged);
            RegisterCleanup(() => _mapDropdown.UnregisterValueChangedCallback(OnMapChanged));

            _matchTimerField.RegisterValueChangedCallback(OnTimerChanged);
            RegisterCleanup(() => _matchTimerField.UnregisterValueChangedCallback(OnTimerChanged));
            RegisterSanitizeOnCommit(_matchTimerField, 0, int.MaxValue, v => _draft.MatchTimerSeconds = v);
            RegisterInfiniteFieldDisplay(_matchTimerField);

            if(_preMatchCountdownToggle != null) {
                void TogglePreMatchCountdown() {
                    ToggleCheckbox(_preMatchCountdownToggle);
                    _draft.UsePreMatchCountdown = GetCheckboxValue(_preMatchCountdownToggle);
                    RefreshStatusLabel();
                }

                _preMatchCountdownToggle.clicked += TogglePreMatchCountdown;
                RegisterCleanup(() => _preMatchCountdownToggle.clicked -= TogglePreMatchCountdown);
            }

            if(_swapWeaponsOnDeathToggle != null) {
                void ToggleSwapWeaponsOnDeath() {
                    ToggleCheckbox(_swapWeaponsOnDeathToggle);
                    _draft.SwapWeaponsOnDeath = GetCheckboxValue(_swapWeaponsOnDeathToggle);
                    RefreshStatusLabel();
                }

                _swapWeaponsOnDeathToggle.clicked += ToggleSwapWeaponsOnDeath;
                RegisterCleanup(() => _swapWeaponsOnDeathToggle.clicked -= ToggleSwapWeaponsOnDeath);
            }

            _scoreToWinField.RegisterValueChangedCallback(OnScoreChanged);
            RegisterCleanup(() => _scoreToWinField.UnregisterValueChangedCallback(OnScoreChanged));
            RegisterSanitizeOnCommit(_scoreToWinField, 0, int.MaxValue, v => _draft.ScoreToWin = v);
            RegisterInfiniteFieldDisplay(_scoreToWinField);

            _kothHillSpeedSlider.RegisterValueChangedCallback(OnKothHillSpeedChanged);
            RegisterCleanup(() => _kothHillSpeedSlider.UnregisterValueChangedCallback(OnKothHillSpeedChanged));

            _taggedPlayersField.RegisterValueChangedCallback(OnTaggedPlayersChanged);
            RegisterCleanup(() => _taggedPlayersField.UnregisterValueChangedCallback(OnTaggedPlayersChanged));
            RegisterSanitizeOnCommit(_taggedPlayersField, 1, int.MaxValue, v => _draft.TaggedPlayers = v);

            _startButton.clicked += OnStartClicked;
            RegisterCleanup(() => _startButton.clicked -= OnStartClicked);
            UISound.RegisterButtonHover(_startButton);
            RegisterCleanup(() => UISound.UnregisterButtonHover(_startButton));

            if(_backButton == null) return;
            _backButton.clicked += OnBackClicked;
            RegisterCleanup(() => _backButton.clicked -= OnBackClicked);
            UISound.RegisterButtonHover(_backButton);
            RegisterCleanup(() => UISound.UnregisterButtonHover(_backButton));
        }

        /// <summary>
        /// Blocks non-digit text entry at input time for integer fields used by private match settings.
        /// Keeps navigation/edit keys and common shortcuts intact.
        /// </summary>
        private void RegisterDigitsOnlyInput(IntegerField field) {
            if(field == null) return;

            EventCallback<InputEvent> inputHandler = evt => {
                if(string.IsNullOrEmpty(evt.newData)) return;
                foreach(var t in evt.newData) {
                    if(char.IsDigit(t)) continue;
                    evt.StopImmediatePropagation();
                    evt.StopPropagation();
                    return;
                }
            };

            EventCallback<KeyDownEvent> keyHandler = evt => {
                if(IsCommandShortcut(evt)) return;
                if(IsAllowedEditKey(evt.keyCode)) return;
                if(IsDigitKey(evt.keyCode)) return;
                if(char.IsDigit(evt.character)) return;
                if(evt.character == '\0') return;

                evt.StopImmediatePropagation();
                evt.StopPropagation();
            };

            // IntegerField internals vary by Unity version; register on both the field and resolved text input.
            field.RegisterCallback(inputHandler, TrickleDown.TrickleDown);
            field.RegisterCallback(keyHandler, TrickleDown.TrickleDown);
            RegisterCleanup(() => field.UnregisterCallback(inputHandler, TrickleDown.TrickleDown));
            RegisterCleanup(() => field.UnregisterCallback(keyHandler, TrickleDown.TrickleDown));

            var textInput = field.Q(TextInputBaseField<int>.textInputUssName);
            if(textInput == null) return;

            textInput.RegisterCallback(inputHandler, TrickleDown.TrickleDown);
            textInput.RegisterCallback(keyHandler, TrickleDown.TrickleDown);
            RegisterCleanup(() => textInput.UnregisterCallback(inputHandler, TrickleDown.TrickleDown));
            RegisterCleanup(() => textInput.UnregisterCallback(keyHandler, TrickleDown.TrickleDown));
        }

        private static bool IsCommandShortcut(KeyDownEvent evt) {
            if(!(evt.ctrlKey || evt.commandKey)) return false;
            return evt.keyCode is KeyCode.A or KeyCode.C or KeyCode.V or KeyCode.X or KeyCode.Z or KeyCode.Y;
        }

        private static bool IsAllowedEditKey(KeyCode keyCode) {
            return keyCode is KeyCode.Backspace or KeyCode.Delete 
                or KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.UpArrow or KeyCode.DownArrow or KeyCode.Home 
                or KeyCode.End or KeyCode.Tab or KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Escape;
        }

        private static bool IsDigitKey(KeyCode keyCode) {
            return keyCode is >= KeyCode.Alpha0 and <= KeyCode.Alpha9 or >= KeyCode.Keypad0 and <= KeyCode.Keypad9;
        }

        private static List<string> BuildGamemodeChoices() {
            var choices = new List<string>();
            var settings = MatchSettingsManager.Instance;
            if(settings != null && settings.gamemodeDefinitions is { Count: > 0 }) {
                for(var i = 0; i < settings.gamemodeDefinitions.Count; i++) {
                    var id = settings.gamemodeDefinitions[i].id;
                    if(string.IsNullOrWhiteSpace(id)) continue;
                    choices.Add(id);
                }
            }

            if(choices.Count != 0) return choices;
            choices.Add("Deathmatch");
            choices.Add("Team Deathmatch");
            choices.Add("Hopball");
            choices.Add("KOTH");
            choices.Add("Gun Tag");
            return choices;
        }

        /// <summary>Returns default match timer for the current gamemode.</summary>
        private static int GetDefaultMatchTimer() {
            var settings = MatchSettingsManager.Instance;
            return settings != null ? Mathf.Max(60, settings.defaultMatchDurationSeconds) : 600;
        }

        /// <summary>Returns default score-to-win for the given gamemode.</summary>
        private static int GetDefaultScoreToWin(string gamemodeId) {
            if(string.IsNullOrWhiteSpace(gamemodeId)) return 50;
            if(string.Equals(gamemodeId, "Hopball", StringComparison.OrdinalIgnoreCase)) return 60;
            return string.Equals(gamemodeId, "KOTH", StringComparison.OrdinalIgnoreCase) ? 200 : 50;
        }

        private const int KothHillSpeedMinPercent = 50;
        private const int KothHillSpeedMaxPercent = 300;

        private static int GetDefaultKothHillSpeedPercent() {
            var settings = MatchSettingsManager.Instance;
            return settings != null
                ? Mathf.Clamp(settings.GetKothHillSpeedPercent(), KothHillSpeedMinPercent, KothHillSpeedMaxPercent)
                : 100;
        }

        private void ApplyGamemodeDefaults(string gamemodeId) {
            _draft.MatchTimerSeconds = Mathf.Max(60, GetDefaultMatchTimer());
            _draft.ScoreToWin = Mathf.Max(1, GetDefaultScoreToWin(gamemodeId));
            _draft.KothHillSpeed = Mathf.Clamp(GetDefaultKothHillSpeedPercent(), KothHillSpeedMinPercent, KothHillSpeedMaxPercent);
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

            if(_kothHillSpeedSlider == null) return;
            _suppressEvents = true;
            _kothHillSpeedSlider.value = _draft.KothHillSpeed;
            _suppressEvents = false;
            RefreshKothHillSpeedDisplay();
        }

        private void RefreshMapChoicesForGamemode(string gamemodeId) {
            _filteredMaps.Clear();
            var choices = new List<string>();
            var pool = Resources.Load<MapPoolDefinition>("MatchMapPoolDefinition");
            var poolOk = pool != null;
            if(poolOk && pool.Maps != null) {
                foreach(var map in pool.Maps) {
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
                RefreshStartButton();
                return;
            }

            _mapDropdown.choices = choices;

            // Autocorrect: if current map not in filtered list, select first valid map (WS-D)
            var selectedIndex = 0;
            for(var i = 0; i < _filteredMaps.Count; i++) {
                if(!string.Equals(_filteredMaps[i].MapId, _draft.MapId, StringComparison.OrdinalIgnoreCase)) continue;
                selectedIndex = i;
                break;
            }

            _draft.MapId = _filteredMaps[selectedIndex].MapId;
            _suppressEvents = true;
            _mapDropdown.value = choices[selectedIndex];
            _suppressEvents = false;
            RefreshStartButton();
        }

        private static bool IsGunTag(string gamemodeId) {
            return string.Equals(gamemodeId, "Gun Tag", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKoth(string gamemodeId) {
            return string.Equals(gamemodeId, "KOTH", StringComparison.OrdinalIgnoreCase);
        }

        private static bool UsesScoreWinCondition(string gamemodeId) {
            return IsKoth(gamemodeId)
                   || string.Equals(gamemodeId, "Hopball", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Shows or hides the score-to-win row based on gamemode.</summary>
        private void RefreshScoreToWinVisibility() {
            if(_scoreToWinRow == null) return;
            var show = UsesScoreWinCondition(_draft.GamemodeId);
            if(show) {
                _scoreToWinRow.RemoveFromClassList("hidden");
                _scoreToWinRow.style.display = DisplayStyle.Flex;
            } else {
                _scoreToWinRow.AddToClassList("hidden");
                _scoreToWinRow.style.display = DisplayStyle.None;
            }
        }

        /// <summary>Shows or hides the KOTH hill speed row based on gamemode.</summary>
        private void RefreshKothHillSpeedVisibility() {
            if(_kothHillSpeedRow == null) return;
            var show = IsKoth(_draft.GamemodeId);
            if(show) {
                _kothHillSpeedRow.RemoveFromClassList("hidden");
                _kothHillSpeedRow.style.display = DisplayStyle.Flex;
            } else {
                _kothHillSpeedRow.AddToClassList("hidden");
                _kothHillSpeedRow.style.display = DisplayStyle.None;
            }
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

        private static int GetPartySize() {
            return SessionManager.Instance != null ? SessionManager.Instance.CurrentPartySize : 1;
        }

        private void RefreshStartButton() {
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
                var teamACount = 0;
                var teamBCount = 0;
                foreach(var m in members) {
                    var team = _draftTeamBySteamId.GetValueOrDefault(m.Id.Value, 0);
                    var container = team == 0 ? teamAContainer : teamBContainer;
                    if(container == null) continue;
                    CreatePreviewRow(m, container);
                    if(team == 0) teamACount++;
                    else teamBCount++;
                }

                if(teamAContainer != null) {
                    for(var i = teamACount; i < TeamSlots; i++) {
                        CreateEmptyPreviewRow(teamAContainer);
                    }
                }

                if(teamBContainer == null) return;
                {
                    for(var i = teamBCount; i < TeamSlots; i++) {
                        CreateEmptyPreviewRow(teamBContainer);
                    }
                }
            } else {
                _previewTeams.AddToClassList("hidden");
                _previewTeams.style.display = DisplayStyle.None;
                _previewFfa.RemoveFromClassList("hidden");
                _previewFfa.style.display = DisplayStyle.Flex;
                var ffaContainer = _ffaList != null ? _ffaList.contentContainer : null;
                if(ffaContainer != null) ffaContainer.Clear();
                var filled = 0;
                foreach(var m in members) {
                    if(ffaContainer == null) continue;
                    CreatePreviewRow(m, ffaContainer);
                    filled++;
                }

                if(ffaContainer == null) return;
                for(var i = filled; i < MaxLobbySlots; i++) {
                    CreateEmptyPreviewRow(ffaContainer);
                }
            }
        }

        private static List<Friend> GetCurrentMembersOrdered() {
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

        private void CreatePreviewRow(Friend member, VisualElement container) {
            if(container == null) return;
            var row = new VisualElement();
            row.AddToClassList("private-match-preview-row");
            var left = new VisualElement();
            left.AddToClassList("private-match-preview-left");
            var avatar = new VisualElement();
            avatar.AddToClassList("private-match-preview-avatar");
            var nameLabel = new Label();
            nameLabel.AddToClassList("private-match-preview-name");
            var metaLabel = new Label();
            metaLabel.AddToClassList("private-match-preview-meta");

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
            ApplyPreviewRowAvatarFallback(avatar, iconId, avatarHidden);
            LoadSteamAvatarIntoPreviewRow(avatar, member.Id, iconId, avatarHidden);
            var isLocal = SteamClient.IsValid && member.Id == SteamClient.SteamId;
            if(isLocal) {
                row.AddToClassList("private-match-preview-row-local");
                metaLabel.text = "YOU";
            } else {
                metaLabel.text = "READY";
            }
            left.Add(avatar);
            left.Add(nameLabel);
            row.Add(left);
            row.Add(metaLabel);

            var steamId = member.Id;
            row.RegisterCallback<PointerDownEvent>(evt => {
                if(evt.button != 1) return;
                // Use current draft gamemode at click time. Host can switch any player (including themselves).
                var showSwitchTeam = MatchSettingsManager.IsTeamBasedMode(_draft.GamemodeId)
                    && (_isHostResolver?.Invoke() ?? false);
                if(Debug.isDebugBuild) {
                    DevLog.Log($"[PrivateMatchSetup] Row right-click: steamId={steamId.Value} gamemode={_draft.GamemodeId} showSwitchTeam={showSwitchTeam}");
                }

                _showContextMenuForPartyMember?.Invoke(evt.position, steamId, showSwitchTeam);

                evt.StopPropagation();
            });
            container.Add(row);
        }

        private static void CreateEmptyPreviewRow(VisualElement container) {
            if(container == null) return;
            var row = new VisualElement();
            row.AddToClassList("private-match-preview-row");
            row.AddToClassList("private-match-preview-row-empty");

            var name = new Label("-");
            name.AddToClassList("private-match-preview-name");
            var meta = new Label("-");
            meta.AddToClassList("private-match-preview-meta");

            row.Add(name);
            row.Add(meta);
            container.Add(row);
        }

        private static void ApplyPreviewRowAvatarFallback(VisualElement avatarBox, string iconId, bool hideAvatar) {
            if(avatarBox == null) return;

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
            try {
                if(avatarBox == null || hideAvatar || id.Value == 0) return;
                if(!SteamClient.IsValid || !SteamClient.IsLoggedOn) return;
                if(SteamManager.Instance == null) return;

                try {
                    var avatarTex = await SteamManager.Instance.GetAvatarAsync(id);
                    if(avatarTex == null) return;
                    avatarBox.style.backgroundImage = new StyleBackground(avatarTex);
                } catch {
                    // Keep deterministic fallback icon when Steam avatar lookup fails.
                    ApplyPreviewRowAvatarFallback(avatarBox, iconId, false);
                }
            } catch(Exception e) {
                Debug.LogException(e); // TODO handle exception
            }
        }

        private static string BuildMapChoiceLabel(MapDefinition map) {
            if(map == null) return "UNKNOWN";
            return string.IsNullOrWhiteSpace(map.MapId) ? map.name.ToUpperInvariant() : map.MapId.Trim().ToUpperInvariant();
        }

        private static string BuildMapPreviewTitle(MapDefinition map, string fallbackMapId) {
            if(map != null) {
                return BuildMapChoiceLabel(map);
            }

            return string.IsNullOrWhiteSpace(fallbackMapId) == false ? fallbackMapId.Trim().ToUpperInvariant() : "UNKNOWN MAP";
        }

        private void OnGamemodeChanged(ChangeEvent<string> evt) {
            if(_suppressEvents) return;
            _draft.GamemodeId = string.IsNullOrWhiteSpace(evt.newValue) ? "Deathmatch" : evt.newValue;
            ApplyGamemodeDefaults(_draft.GamemodeId);
            RefreshMapChoicesForGamemode(_draft.GamemodeId);
            RefreshScoreToWinVisibility();
            RefreshKothHillSpeedVisibility();
            RefreshTaggedRowVisibility();
            RefreshMapPreview();
            RefreshStatusLabel();
            RefreshStartButton();
            RefreshTeamPreview();
        }

        private void OnMapChanged(ChangeEvent<string> evt) {
            if(_suppressEvents) return;
            foreach(var t in _filteredMaps) {
                var label = BuildMapChoiceLabel(t);
                if(!string.Equals(label, evt.newValue, StringComparison.OrdinalIgnoreCase)) continue;
                _draft.MapId = t.MapId;
                break;
            }
            RefreshMapPreview();
            RefreshStatusLabel();
            RefreshStartButton();
        }

        private void RefreshMapPreview() {
            if(_mapPreviewImage == null) return;

            var selectedMap = GetSelectedMapDefinition();
            var sprite = selectedMap != null ? selectedMap.PreviewImage : null;
            var hasPreview = sprite != null;

            _mapPreviewImage.style.backgroundImage = hasPreview ? new StyleBackground(sprite) : StyleKeyword.Null;

            if(_mapPreviewWipLabel != null) {
                _mapPreviewWipLabel.style.display = hasPreview ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if(_mapPreviewTitleLabel != null) {
                _mapPreviewTitleLabel.text = BuildMapPreviewTitle(selectedMap, _draft.MapId);
            }
        }

        private MapDefinition GetSelectedMapDefinition() {
            foreach(var candidate in _filteredMaps) {
                if(candidate == null) continue;
                if(string.Equals(candidate.MapId, _draft.MapId, StringComparison.OrdinalIgnoreCase)) {
                    return candidate;
                }
            }

            var pool = Resources.Load<MapPoolDefinition>("MatchMapPoolDefinition");
            if(pool == null || pool.Maps == null) return null;
            foreach(var candidate in pool.Maps) {
                if(candidate == null) continue;
                if(string.Equals(candidate.MapId, _draft.MapId, StringComparison.OrdinalIgnoreCase)) {
                    return candidate;
                }
            }

            return null;
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

            EventCallback<BlurEvent> blurHandler = _ => SanitizeAndApply();
            EventCallback<KeyDownEvent> keyHandler = evt => {
                if(evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
                SanitizeAndApply();
                // IntegerField focus is usually on inner text input, not the field wrapper.
                // Blur both immediately and on scheduled pass to ensure focus is released.
                var textInput = field.Q(TextInputBaseField<int>.textInputUssName);
                textInput?.Blur();
                field.Blur();
                field.schedule.Execute(() => {
                    var delayedInput = field.Q(TextInputBaseField<int>.textInputUssName);
                    delayedInput?.Blur();
                    field.Blur();
                });
                evt.StopImmediatePropagation();
                evt.StopPropagation();
            };
            field.RegisterCallback(blurHandler);
            field.RegisterCallback(keyHandler, TrickleDown.TrickleDown);
            RegisterCleanup(() => field.UnregisterCallback(blurHandler));
            RegisterCleanup(() => field.UnregisterCallback(keyHandler, TrickleDown.TrickleDown));

            var fieldTextInput = field.Q(TextInputBaseField<int>.textInputUssName);
            if(fieldTextInput == null) return;
            fieldTextInput.RegisterCallback(keyHandler, TrickleDown.TrickleDown);
            RegisterCleanup(() => fieldTextInput.UnregisterCallback(keyHandler, TrickleDown.TrickleDown));
            return;

            void SanitizeAndApply() {
                var raw = field.value;
                var clamped = Mathf.Clamp(raw, min, max);
                if(clamped != raw) {
                    _suppressEvents = true;
                    field.value = clamped;
                    _suppressEvents = false;
                }

                setDraft(clamped);
                ApplyInfiniteFieldDisplay(field, clamped == 0);
                RefreshStatusLabel();
                RefreshStartButton();
            }
        }

        private void RegisterInfiniteFieldDisplay(IntegerField field) {
            if(field == null) return;
            EventCallback<FocusInEvent> focusInHandler = _ => {
                // While editing, show numeric value so keyboard input works naturally.
                ApplyInfiniteFieldDisplay(field, false);
            };
            EventCallback<BlurEvent> blurHandler = _ => {
                ApplyInfiniteFieldDisplay(field, field.value == 0);
            };
            field.RegisterCallback(focusInHandler);
            field.RegisterCallback(blurHandler);
            RegisterCleanup(() => field.UnregisterCallback(focusInHandler));
            RegisterCleanup(() => field.UnregisterCallback(blurHandler));
        }

        private static void ApplyInfiniteFieldDisplay(IntegerField field, bool showInfinite) {
            if(field == null) return;
            var textRoot = field.Q(TextInputBaseField<int>.textInputUssName);
            if(textRoot == null) return;
            var textElement = textRoot.Q<TextElement>();
            if(textElement == null) return;
            textElement.text = showInfinite ? "INFINITE" : field.value.ToString();
        }

        private static bool GetCheckboxValue(Button button) {
            return button != null && button.ClassListContains("checked");
        }

        private static void SetCheckboxValue(Button button, bool value) {
            if(button == null) return;
            if(value) {
                button.AddToClassList("checked");
            } else {
                button.RemoveFromClassList("checked");
            }
        }

        private static void ToggleCheckbox(Button button) {
            if(button == null) return;
            SetCheckboxValue(button, !GetCheckboxValue(button));
        }

        private void OnTaggedPlayersChanged(ChangeEvent<int> evt) {
            if(_suppressEvents) return;
            _draft.TaggedPlayers = evt.newValue;
            RefreshStatusLabel();
        }

        private void OnKothHillSpeedChanged(ChangeEvent<int> evt) {
            if(_suppressEvents) return;
            _draft.KothHillSpeed = Mathf.Clamp(evt.newValue, KothHillSpeedMinPercent, KothHillSpeedMaxPercent);
            RefreshKothHillSpeedDisplay();
            RefreshStatusLabel();
        }

        private void RefreshKothHillSpeedDisplay() {
            if(_kothHillSpeedValueLabel == null) return;
            _kothHillSpeedValueLabel.text = $"{_draft.KothHillSpeed / 100f:F2}x";
        }

        private void OnStartClicked() {
            UISound.PlayButtonClick();
            OnStartRequested?.Invoke(_draft);
        }

        private void OnBackClicked() {
            UISound.PlayButtonClick(isBack: true);
            OnBackRequested?.Invoke();
        }

        private void RefreshStatusLabel() {
            if(_statusLabel == null) return;
            var mode = string.IsNullOrWhiteSpace(_draft.GamemodeId) ? "UNKNOWN" : _draft.GamemodeId.ToUpperInvariant();
            var map = string.IsNullOrWhiteSpace(_draft.MapId) ? "UNKNOWN" : _draft.MapId.ToUpperInvariant();
            var timeText = _draft.MatchTimerSeconds <= 0 ? "INFINITE" : $"{_draft.MatchTimerSeconds}s";
            var countdownText = _draft.UsePreMatchCountdown ? "ON" : "OFF";
            var loadoutSwapText = _draft.SwapWeaponsOnDeath ? "ON DEATH" : "INSTANT";
            var scoreText = _draft.ScoreToWin <= 0 ? "INFINITE" : _draft.ScoreToWin.ToString();
            var kothSpeedText = string.Equals(_draft.GamemodeId, "KOTH", StringComparison.OrdinalIgnoreCase)
                ? $"  |  HILL SPEED: {_draft.KothHillSpeed / 100f:F2}x"
                : string.Empty;
            _statusLabel.text =
                $"MODE: {mode}  |  MAP: {map}  |  TIME: {timeText}  |  COUNTDOWN: {countdownText}  |  LOADOUT SWAP: {loadoutSwapText}  |  SCORE: {scoreText}{kothSpeedText}";
        }
    }
}

