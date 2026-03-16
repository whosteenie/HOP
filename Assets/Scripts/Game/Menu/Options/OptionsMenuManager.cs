using System;
using System.Collections.Generic;
using Game.Settings;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    /// <summary>
    /// Thin coordinator for the shared options menu. Delegates to tab handlers.
    /// Can be used by both MainMenuManager and GameMenuManager.
    /// </summary>
    public class OptionsMenuManager : UIElementBase, IOptionsTabContext {
        [Header("References")]
        [SerializeField] private AudioMixer audioMixer;

        [Header("Callbacks")]
        [SerializeField] private bool useCallbacks = true;

        public Action<bool> OnButtonClickedCallback;
        public Action<MouseEnterEvent> MouseEnterCallback;
        public Action<MouseOverEvent> MouseHoverCallback;
        public Action OnBackFromOptionsCallback;

        #region Shared UI

        private Button _tabVideo;
        private Button _tabAudio;
        private Button _tabGame;
        private Button _tabControls;
        private VisualElement _videoContent;
        private VisualElement _audioContent;
        private VisualElement _gameContent;
        private VisualElement _controlsContent;
        private ScrollView _optionsContentScroll;
        private Label _optionsDescriptionTitle;
        private Label _optionsDescriptionBody;
        private VisualElement _optionsDescriptionPanel;
        private VisualElement _unsavedChangesModal;
        private Button _unsavedChangesYes;
        private Button _unsavedChangesNo;
        private Button _unsavedChangesCancel;
        private Button _applyButton;
        private Button _backButton;

        private static readonly Color OptionsTabHoverTextColor = new(240f / 255f, 240f / 255f, 240f / 255f, 1f);
        private static readonly Color OptionsTabHoverBorderColor = new(200f / 255f, 60f / 255f, 60f / 255f, 0.5f);

        private readonly Dictionary<string, (string Title, string Body)> _settingDescriptions = new() {
            ["window-mode-container"] = ("WINDOW MODE", "Choose how the game window is presented: windowed, borderless, or fullscreen."),
            ["aspect-ratio-container"] = ("ASPECT RATIO", "Sets the screen ratio. Match this to your monitor for the cleanest image framing."),
            ["resolution-container"] = ("RESOLUTION", "Higher resolution improves clarity but increases GPU load."),
            ["msaa-container"] = ("ANTI-ALIASING (MSAA)", "Smooths jagged edges. Higher values improve quality at a performance cost."),
            ["shadow-distance-container"] = ("SHADOW DISTANCE", "Controls how far dynamic shadows are rendered from the camera."),
            ["shadow-resolution-container"] = ("SHADOW RESOLUTION", "Increases shadow sharpness. Higher settings cost more GPU memory and performance."),
            ["bloom-container"] = ("BLOOM", "Adds glow around bright highlights. Disable for a cleaner image and lower post-processing cost."),
            ["motion-blur-container"] = ("MOTION BLUR", "Simulates camera/object motion streaking. Disable for a sharper image and reduced post-processing cost."),
            ["film-grain-container"] = ("FILM GRAIN", "Adds subtle film-like noise for texture. Disable for a cleaner image."),
            ["vignette-container"] = ("VIGNETTE", "Darkens screen edges to focus attention toward the center."),
            ["vsync-container"] = ("VSYNC", "Reduces tearing by syncing frame output with monitor refresh rate."),
            ["target-fps-container"] = ("TARGET FPS", "Caps framerate to reduce power use and stabilize frame pacing."),
            ["master-container"] = ("MASTER VOLUME", "Global volume level for all game audio."),
            ["music-container"] = ("MUSIC VOLUME", "Controls soundtrack and ambient music loudness."),
            ["sfx-container"] = ("SFX VOLUME", "Controls gameplay sound effects such as weapons, impacts, and movement."),
            ["voice-volume-container"] = ("VOICE CHAT VOLUME", "Adjusts how loud incoming voice chat sounds."),
            ["voice-input-volume-container"] = ("MICROPHONE VOLUME", "Adjusts outgoing microphone gain for voice chat."),
            ["voice-device-container"] = ("MICROPHONE", "Select which input device is used for voice chat."),
            ["player-trails-container"] = ("PLAYER SPEED TRAILS", "Toggles speed trail visual effects on players."),
            ["main-menu-background-container"] = ("MAIN MENU BACKGROUND", "Select which mannequin background scene is shown in the main menu. Random picks one each menu entry."),
            ["streamer-mode-container"] = ("STREAMER MODE", "Hides your display name in supported UI surfaces."),
            ["hold-mantle-container"] = ("HOLD TO MANTLE", "When enabled, mantling can trigger while jump is held."),
            ["auto-wall-run-container"] = ("AUTO WALL RUN", "Automatically starts a wall run when valid wall-run conditions are met."),
            ["grapple-indicator-container"] = ("GRAPPLE INDICATOR TYPE", "Choose where grapple readiness/aim feedback is shown."),
            ["crosshair-style-container"] = ("CROSSHAIR STYLE", "Switch between a classic cross and a center dot."),
            ["crosshair-color-container"] = ("CROSSHAIR COLOR", "Sets the HUD crosshair color and grapple indicator accent color."),
            ["profanity-filter-container"] = ("CHAT PROFANITY FILTER", "Locally filters text chat according to your preference."),
            ["analytics-enabled-container"] = ("ANALYTICS", "Turn off local diagnostics/analytics logging for the best performance, especially on lower-end machines."),
            ["voice-mode-container"] = ("VOICE INPUT MODE", "Select voice activation mode: push-to-talk or open mic."),
            ["sensitivity-container"] = ("MOUSE SENSITIVITY", "Controls horizontal and vertical look sensitivity."),
            ["invert-y-container"] = ("INVERT Y AXIS", "Inverts vertical look input for mouse movement.")
        };

        #endregion

        #region Tab Handlers

        private OptionsAudioTabHandler _audioHandler;
        private OptionsVideoTabHandler _videoHandler;
        private OptionsGameTabHandler _gameHandler;
        private OptionsControlsTabHandler _controlsHandler;

        private readonly List<IOptionsTabHandler> _tabHandlers = new();

        #endregion

        #region IOptionsTabContext

        T IOptionsTabContext.QOptional<T>(string tabName) => QOptional<T>(tabName);
        void IOptionsTabContext.RegisterCleanup(Action a) => RegisterCleanup(a);
        VisualElement IOptionsTabContext.Root => Root;

        #endregion

        #region Unity Lifecycle

        protected override void Awake() {
            base.Awake();
            if(uiDocument == null) uiDocument = GetComponent<UIDocument>();
        }

        public new void Initialize() {
            base.Initialize();
        }

        protected override void OnInitialize() {
            _audioHandler = new OptionsAudioTabHandler(audioMixer);
            _videoHandler = new OptionsVideoTabHandler();
            _gameHandler = new OptionsGameTabHandler(ResolveBackgroundRandomizer);
            _controlsHandler = new OptionsControlsTabHandler();

            _tabHandlers.Clear();
            _tabHandlers.Add(_audioHandler);
            _tabHandlers.Add(_videoHandler);
            _tabHandlers.Add(_gameHandler);
            _tabHandlers.Add(_controlsHandler);

            FindSharedElements();
            foreach(var handler in _tabHandlers) {
                handler.FindElements(this);
            }

            BindDropdownOpenStateClasses();
            SetupDropdownTextFormatting();
            foreach(var handler in _tabHandlers) {
                handler.SetupCallbacks(this);
            }
            SetupOptionsTabs();
            _controlsHandler.SetupKeybinds(this);
            SetupManagerCallbacks();
            SetupSettingDescriptions();
        }

        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type> {
                { "apply-button", typeof(Button) },
                { "back-button", typeof(Button) }
            };
        }

        #endregion

        #region Setup

        private void FindSharedElements() {
            _tabVideo = QOptional<Button>("tab-video");
            _tabAudio = QOptional<Button>("tab-audio");
            _tabGame = QOptional<Button>("tab-game");
            _tabControls = QOptional<Button>("tab-controls");
            _optionsContentScroll = QOptional<ScrollView>("options-content-scroll");
            _videoContent = QOptional<VisualElement>("video-content");
            _audioContent = QOptional<VisualElement>("audio-content");
            _gameContent = QOptional<VisualElement>("game-content");
            _controlsContent = QOptional<VisualElement>("controls-content");
            _optionsDescriptionPanel = QOptional<VisualElement>("options-description-panel");
            _optionsDescriptionTitle = QOptional<Label>("options-description-title");
            _optionsDescriptionBody = QOptional<Label>("options-description-body");
            _unsavedChangesModal = QOptional<VisualElement>("unsaved-changes-modal");
            _unsavedChangesYes = QOptional<Button>("unsaved-changes-yes");
            _unsavedChangesNo = QOptional<Button>("unsaved-changes-no");
            _unsavedChangesCancel = QOptional<Button>("unsaved-changes-cancel");
            _applyButton = QRequired<Button>("apply-button");
            _backButton = QRequired<Button>("back-button");
        }

        private void BindDropdownOpenStateClasses() {
            if(Root == null) return;
            foreach(var dropdown in Root.Query<DropdownField>().ToList()) {
                var cleanup = DropdownOpenStateBinder.Bind(dropdown);
                if(cleanup != null) RegisterCleanup(cleanup);
            }
        }

        private void SetupDropdownTextFormatting() {
            if(Root == null) return;
            foreach(var dropdown in Root.Query<DropdownField>().ToList()) {
                if(dropdown == null) continue;
                dropdown.formatSelectedValueCallback = value => string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToUpperInvariant();
                dropdown.formatListItemCallback = value => string.IsNullOrWhiteSpace(value) ? string.Empty : value.ToUpperInvariant();
            }
        }

        private void SetupOptionsTabs() {
            if(_optionsContentScroll != null) {
                _optionsContentScroll.verticalScrollerVisibility = ScrollerVisibility.AlwaysVisible;
                _optionsContentScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            }
            RegisterTabButton(_tabVideo, "video");
            RegisterTabButton(_tabAudio, "audio");
            RegisterTabButton(_tabGame, "game");
            RegisterTabButton(_tabControls, "controls");
            SetupTabHoverCallbacks(_tabVideo);
            SetupTabHoverCallbacks(_tabAudio);
            SetupTabHoverCallbacks(_tabGame);
            SetupTabHoverCallbacks(_tabControls);
            SwitchOptionsTab("video");
        }

        private void RegisterTabButton(Button tab, string tabName) {
            if(tab == null) return;
            var t = tabName;
            EventCallback<ClickEvent> handler = _ => {
                OnButtonClicked();
                SwitchOptionsTab(t);
            };
            tab.RegisterCallback(handler);
            RegisterCleanup(() => tab.UnregisterCallback(handler));
        }

        private void SetupSettingDescriptions() {
            if(Root == null || _optionsDescriptionPanel == null || _optionsDescriptionTitle == null || _optionsDescriptionBody == null) return;
            var rows = Root.Query<VisualElement>(className: "setting-row").ToList();
            foreach(var row in rows) {
                if(row == null) continue;
                EventCallback<PointerEnterEvent> pointerEnter = _ => SetDescriptionForRow(row);
                row.RegisterCallback(pointerEnter);
                RegisterCleanup(() => row.UnregisterCallback(pointerEnter));
                foreach(var control in row.Query<VisualElement>().ToList()) {
                    EventCallback<FocusInEvent> focusIn = _ => SetDescriptionForRow(row);
                    control.RegisterCallback(focusIn);
                    RegisterCleanup(() => control.UnregisterCallback(focusIn));
                }
            }
            SetDefaultDescriptionForTab("video");
        }

        private void SetDefaultDescriptionForTab(string tabName) {
            var tabContent = tabName.ToLowerInvariant() switch {
                "video" => _videoContent,
                "audio" => _audioContent,
                "game" => _gameContent,
                "controls" => _controlsContent,
                _ => null
            };
            if(tabContent == null) return;
            var rows = tabContent.Query<VisualElement>(className: "setting-row").ToList();
            var firstRow = rows.Count > 0 ? rows[0] : null;
            if(firstRow == null) {
                SetDescription("SETTING DETAILS", "Hover or select a setting to see what it changes.");
                return;
            }
            SetDescriptionForRow(firstRow);
        }

        private void SetDescriptionForRow(VisualElement row) {
            if(_optionsDescriptionTitle == null || _optionsDescriptionBody == null || row == null) return;
            var key = row.name ?? string.Empty;
            if(_settingDescriptions.TryGetValue(key, out var description)) {
                SetDescription(description.Title, description.Body);
                return;
            }
            var label = row.Q<Label>(className: "setting-label");
            var labelText = label != null && !string.IsNullOrWhiteSpace(label.text) ? label.text : "SETTING";
            if(key.StartsWith("keybind-", StringComparison.OrdinalIgnoreCase)) {
                SetDescription($"KEYBIND: {labelText}",
                    "Assign primary and secondary keys for this action. Secondary binds are useful for alternates like mouse wheel or extra keys.");
                return;
            }
            SetDescription(labelText, "Adjust this setting to tune gameplay, visuals, audio, or controls.");
        }

        private void SetDescription(string title, string body) {
            if(_optionsDescriptionTitle != null)
                _optionsDescriptionTitle.text = string.IsNullOrWhiteSpace(title) ? "SETTING DETAILS" : title;
            if(_optionsDescriptionBody != null)
                _optionsDescriptionBody.text = string.IsNullOrWhiteSpace(body) ? "Hover or select a setting to see what it changes." : body;
        }

        private void SetupTabHoverCallbacks(Button tab) {
            if(tab == null) return;
            EventCallback<MouseEnterEvent> enterHandler = evt => {
                if(tab.ClassListContains("options-tab-active")) return;
                MouseEnterCallback?.Invoke(evt);
            };
            tab.RegisterCallback(enterHandler);
            RegisterCleanup(() => tab.UnregisterCallback(enterHandler));
            EventCallback<MouseOverEvent> overHandler = evt => {
                if(MouseHoverCallback != null && !tab.ClassListContains("options-tab-active")) MouseHoverCallback(evt);
                if(!tab.ClassListContains("options-tab-active") && tab.ClassListContains("options-tab-hover")) tab.MarkDirtyRepaint();
            };
            tab.RegisterCallback(overHandler);
            RegisterCleanup(() => tab.UnregisterCallback(overHandler));
            EventCallback<PointerEnterEvent> pointerEnterHandler = _ => {
                if(tab.ClassListContains("options-tab-active")) return;
                if(!tab.ClassListContains("options-tab-hover")) tab.AddToClassList("options-tab-hover");
                tab.style.color = new StyleColor(OptionsTabHoverTextColor);
                tab.style.borderBottomColor = new StyleColor(OptionsTabHoverBorderColor);
                tab.MarkDirtyRepaint();
                tab.schedule.Execute(() => {
                    if(!tab.ClassListContains("options-tab-hover") || tab.ClassListContains("options-tab-active")) return;
                    tab.MarkDirtyRepaint();
                    tab.parent?.MarkDirtyRepaint();
                }).StartingIn(0);
            };
            tab.RegisterCallback(pointerEnterHandler);
            RegisterCleanup(() => tab.UnregisterCallback(pointerEnterHandler));
            EventCallback<PointerLeaveEvent> pointerLeaveHandler = _ => {
                tab.RemoveFromClassList("options-tab-hover");
                tab.style.color = StyleKeyword.Null;
                tab.style.borderBottomColor = StyleKeyword.Null;
                tab.MarkDirtyRepaint();
            };
            tab.RegisterCallback(pointerLeaveHandler);
            RegisterCleanup(() => tab.UnregisterCallback(pointerLeaveHandler));
        }

        private void SwitchOptionsTab(string tabName) {
            var tabs = new[] { _tabVideo, _tabAudio, _tabGame, _tabControls };
            var contents = new[] { _videoContent, _audioContent, _gameContent, _controlsContent };
            for(var i = 0; i < tabs.Length; i++) {
                if(tabs[i] != null) {
                    tabs[i].RemoveFromClassList("options-tab-active");
                    tabs[i].RemoveFromClassList("options-tab-hover");
                    tabs[i].style.color = StyleKeyword.Null;
                    tabs[i].style.borderBottomColor = StyleKeyword.Null;
                }
                contents[i]?.AddToClassList("hidden");
            }
            var index = tabName.ToLowerInvariant() switch { "video" => 0, "audio" => 1, "game" => 2, "controls" => 3, _ => 0 };
            tabs[index]?.AddToClassList("options-tab-active");
            contents[index]?.RemoveFromClassList("hidden");
            foreach(var tab in tabs) tab?.MarkDirtyRepaint();
            SetDefaultDescriptionForTab(tabName);
        }

        private MainMenuBackgroundRandomizer ResolveBackgroundRandomizer() {
            var r = GetComponentInParent<MainMenuBackgroundRandomizer>();
            return r != null ? r : MainMenuBackgroundRandomizer.Instance;
        }

        #endregion

        #region Callbacks (Apply, Back, Unsaved)

        private void SetupManagerCallbacks() {
            EventCallback<ClickEvent> applyHandler = _ => {
                OnButtonClicked();
                ApplySettings();
            };
            _applyButton.RegisterCallback(applyHandler);
            RegisterCleanup(() => _applyButton.UnregisterCallback(applyHandler));
            if(useCallbacks) RegisterHoverCallback(_applyButton);

            EventCallback<ClickEvent> backHandler = _ => {
                OnButtonClicked(true);
                OnBackFromOptions();
            };
            _backButton.RegisterCallback(backHandler);
            RegisterCleanup(() => _backButton.UnregisterCallback(backHandler));
            if(useCallbacks) RegisterHoverCallback(_backButton);

            RegisterUnsavedChangesButton(_unsavedChangesYes, OnUnsavedChangesYes);
            RegisterUnsavedChangesButton(_unsavedChangesNo, OnUnsavedChangesNo);
            RegisterUnsavedChangesButton(_unsavedChangesCancel, OnUnsavedChangesCancel);
        }

        private void RegisterUnsavedChangesButton(Button button, Action onClick) {
            if(button == null) return;
            EventCallback<ClickEvent> handler = evt => {
                evt.StopPropagation();
                evt.StopImmediatePropagation();
                onClick();
            };
            button.RegisterCallback(handler);
            RegisterCleanup(() => button.UnregisterCallback(handler));
            if(useCallbacks) RegisterHoverCallback(button);
        }

        private void RegisterHoverCallback(Button button) {
            EventCallback<MouseEnterEvent> enterHandler = evt => MouseEnterCallback?.Invoke(evt);
            button.RegisterCallback(enterHandler);
            RegisterCleanup(() => button.UnregisterCallback(enterHandler));
            if(MouseHoverCallback == null) return;
            EventCallback<MouseOverEvent> hoverHandler = evt => MouseHoverCallback(evt);
            button.RegisterCallback(hoverHandler);
            RegisterCleanup(() => button.UnregisterCallback(hoverHandler));
        }

        #endregion

        #region Settings Management

        public void LoadSettings() {
            var data = GameSettings.Data;
            foreach(var handler in _tabHandlers) {
                handler.Load(data);
                handler.StoreOriginal();
            }
            ApplySettingsInternal();
            foreach(var handler in _tabHandlers) {
                handler.RefreshDisplay();
            }
            _controlsHandler.LoadKeybindDisplayStrings();
        }

        private void ApplySettings() {
            var data = GameSettings.Data;
            var mainMenuBackgroundSelectionChanged = _gameHandler.HasBackgroundChange();
            foreach(var handler in _tabHandlers) {
                handler.Save(data);
            }
            if(KeybindManager.Instance != null) KeybindManager.Instance.SaveBindings();
            GameSettings.Save();
            ApplySettingsInternal();
            if(mainMenuBackgroundSelectionChanged) _gameHandler.ApplyBackgroundPreviewFromCurrent(onlyIfNotRandom: true);
            foreach(var handler in _tabHandlers) {
                handler.StoreOriginal();
            }
            _controlsHandler.LoadKeybindDisplayStrings();
        }

        private void ApplySettingsInternal() {
            foreach(var handler in _tabHandlers) {
                handler.ApplyToRuntime();
            }
        }

        private bool HasUnsavedChanges() {
            foreach(var handler in _tabHandlers) {
                if(handler.HasUnsavedChanges()) return true;
            }

            return false;
        }

        #endregion

        #region Back / Unsaved Flow

        private void OnBackFromOptions() {
            OptionsControlsTabHandler.CancelKeybindRebinding();
            _controlsHandler.LoadKeybindDisplayStrings();
            var hasUnsaved = HasUnsavedChanges();
            OptionsControlsTabHandler.CancelKeybindBindings();
            if(hasUnsaved) ShowUnsavedChangesDialog();
            else OnBackFromOptionsCallback?.Invoke();
        }

        private void ShowUnsavedChangesDialog() {
            if(_unsavedChangesModal == null) return;
            _unsavedChangesModal.RemoveFromClassList("hidden");
            _unsavedChangesModal.BringToFront();
        }

        private void HideUnsavedChangesDialog() => _unsavedChangesModal?.AddToClassList("hidden");

        private void OnUnsavedChangesYes() {
            OnButtonClicked();
            ApplySettings();
            HideUnsavedChangesDialog();
            NavigateBackFromOptions();
        }

        private void OnUnsavedChangesNo() {
            OnButtonClicked(true);
            OptionsControlsTabHandler.CancelKeybindBindings();
            LoadSettings();
            _gameHandler.ApplyBackgroundPreviewFromCurrent();
            HideUnsavedChangesDialog();
            NavigateBackFromOptions();
        }

        private void OnUnsavedChangesCancel() {
            OnButtonClicked(true);
            HideUnsavedChangesDialog();
        }

        private void NavigateBackFromOptions() {
            if(OnBackFromOptionsCallback == null) return;
            if(Root == null) {
                OnBackFromOptionsCallback.Invoke();
                return;
            }
            Root.schedule.Execute(() => OnBackFromOptionsCallback?.Invoke());
        }

        #endregion

        #region Helpers

        private void OnButtonClicked(bool isBack = false) => OnButtonClickedCallback?.Invoke(isBack);

        #endregion

        #region Public

        public void OnOptionsPanelShown() {
            _audioHandler.RefreshVoiceDeviceChoicesForPanel(Root);
            _gameHandler.RefreshBackgroundChoicesForPanel(preserveCurrentSelection: true);
            var optionsPanel = Root?.Q<VisualElement>("options-panel");
            optionsPanel?.schedule.Execute(() => {
                _tabVideo?.SetEnabled(true);
                _tabAudio?.SetEnabled(true);
                _tabGame?.SetEnabled(true);
                _tabControls?.SetEnabled(true);
                optionsPanel.schedule.Execute(() => {
                    _tabVideo?.MarkDirtyRepaint();
                    _tabAudio?.MarkDirtyRepaint();
                    _tabGame?.MarkDirtyRepaint();
                    _tabControls?.MarkDirtyRepaint();
                });
            });
        }

        #endregion
    }
}
