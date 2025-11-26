using System;
using System.Collections.Generic;
using Game.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class GameMenuManager : MonoBehaviour {
        #region Serialized Fields

        [SerializeField] private UIDocument uiDocument;
        // AudioMixer is now handled by OptionsMenuManager

        [Header("Kill Feed")]
        [SerializeField] private KillFeedManager killFeedManager;

        [Header("Scoreboard")]
        [SerializeField] private ScoreboardManager scoreboardManager;

        [Header("Options")]
        [SerializeField] private OptionsMenuManager optionsMenuManager;

        [Header("Sniper Overlay")]
        [SerializeField] private SniperOverlayManager _sniperOverlayManager;

        #endregion

        #region UI Elements - Kill Feed

        private VisualElement _killFeedContainer;

        #endregion

        #region Private Fields

        private VisualElement _pauseMenuPanel;
        private VisualElement _optionsPanel;
        private PlayerController _localController;
        private Button _resumeButton;
        private Button _optionsButton;
        private Button _quitButton;

        // Pause menu join code
        private Label _pauseJoinCodeLabel;
        private Button _pauseCopyCodeButton;

        private VisualElement _root;

        // HUD / Match UI
        private VisualElement _hudPanel;
        private VisualElement _matchTimerContainer;

        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        #endregion

        #region Properties

        public bool IsPaused { get; private set; }

        public bool IsScoreboardVisible => scoreboardManager != null && scoreboardManager.IsScoreboardVisible;

        public bool IsPostMatch { get; set; }

        public bool IsPreMatch => MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch;

        #endregion

        public static GameMenuManager Instance { get; private set; }

        #region Unity Lifecycle

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable() {
            if(uiDocument == null) {
                Debug.LogError("[GameMenuManager] UIDocument is not assigned!");
                return;
            }

            _root = uiDocument.rootVisualElement;

            // Cache scene name to avoid allocations
            UpdateCachedSceneName();

            // Subscribe to scene changes to update cache
            SceneManager.sceneLoaded += OnSceneLoaded;

            FindUIElements();
            RegisterUIEvents();

            SetupOptionsMenuManager();
            SetupKillFeedManager();
            SetupScoreboardManager();
            SetupSniperOverlayManager();

            // Subscribe to join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable += OnRelayCodeAvailable;
            }
        }

        private void OnDisable() {
            // Unsubscribe from join code updates
            if(SessionManager.Instance != null) {
                SessionManager.Instance.RelayCodeAvailable -= OnRelayCodeAvailable;
            }

            // Unsubscribe from scene changes
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            UpdateCachedSceneName();
        }

        private void OnRelayCodeAvailable(string joinCode) {
            // Update join code display if pause menu is visible
            if(!IsPaused || _pauseJoinCodeLabel == null) return;
            _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
            _pauseCopyCodeButton?.SetEnabled(true);
        }

        private void FindLocalController() {
            var allControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var controller in allControllers) {
                if(!controller.IsOwner) continue;
                _localController = controller.GetComponent<PlayerController>();
                break;
            }
        }

        private void FindUIElements() {
            // Get panels
            _pauseMenuPanel = _root.Q<VisualElement>("pause-menu-panel");
            _optionsPanel = _root.Q<VisualElement>("options-panel");

            _resumeButton = _root.Q<Button>("resume-button");
            _optionsButton = _root.Q<Button>("options-button");
            _quitButton = _root.Q<Button>("quit-button");

            // Pause menu join code
            _pauseJoinCodeLabel = _root.Q<Label>("pause-join-code-label");
            _pauseCopyCodeButton = _root.Q<Button>("pause-copy-code-button");

            // Kill Feed
            _killFeedContainer = _root.Q<VisualElement>("kill-feed-container");

            // HUD root + containers
            _hudPanel = _root.Q<VisualElement>("hud-panel");
            _matchTimerContainer = _root.Q<VisualElement>("match-timer-container");
        }

        private void RegisterUIEvents() {
            // Setup main menu buttons
            _resumeButton.clicked += () => {
                OnButtonClicked();
                ResumeGame();
            };
            _resumeButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _optionsButton.clicked += () => {
                OnButtonClicked();
                ShowOptions();
            };
            _optionsButton.RegisterCallback<MouseOverEvent>(MouseHover);

            _quitButton.clicked += () => {
                OnButtonClicked(true);
                QuitToMenu();
            };
            _quitButton.RegisterCallback<MouseOverEvent>(MouseHover);

            // Setup pause menu copy button
            if(_pauseCopyCodeButton == null) return;
            _pauseCopyCodeButton.clicked += CopyPauseJoinCodeToClipboard;
            _pauseCopyCodeButton.RegisterCallback<MouseEnterEvent>(MouseHover);
        }

        private static void OnButtonClicked(bool isBack = false) {
            if(SoundFXManager.Instance == null) return;
            var soundKey = !isBack ? SfxKey.ButtonClick : SfxKey.BackButton;
            SoundFXManager.Instance.PlayUISound(soundKey);
        }

        private static void MouseEnter(MouseEnterEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        private static void MouseHover(MouseOverEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        private static void MouseHover(MouseEnterEvent evt) {
            if(SoundFXManager.Instance != null) {
                SoundFXManager.Instance.PlayUISound(SfxKey.ButtonHover);
            }
        }

        public void TogglePause() {
            // Only allow pausing in Game scene
            if(_cachedSceneName == null || !_cachedSceneName.Contains("Game")) return;

            if(IsPaused) {
                if(!_optionsPanel.ClassListContains("hidden")) {
                    HideOptions();
                } else {
                    ResumeGame();
                }
            } else {
                PauseGame();
            }
        }

        #endregion

        #region Setup Methods

        private void SetupOptionsMenuManager() {
            if(optionsMenuManager == null) {
                Debug.LogError("[GameMenuManager] OptionsMenuManager not assigned!");
                return;
            }

            // Set up callbacks
            optionsMenuManager.OnButtonClickedCallback = OnButtonClicked;
            optionsMenuManager.MouseEnterCallback = MouseEnter;
            optionsMenuManager.MouseHoverCallback = MouseHover;
            optionsMenuManager.OnBackFromOptionsCallback = HideOptions;

            // Initialize the options menu manager
            optionsMenuManager.Initialize();
        }

        private void SetupKillFeedManager() {
            if(killFeedManager == null) {
                Debug.LogError("[GameMenuManager] KillFeedManager not assigned!");
                return;
            }

            // Initialize kill feed manager with the container
            killFeedManager.Initialize(_killFeedContainer);
        }

        private void SetupScoreboardManager() {
            if(scoreboardManager == null) {
                Debug.LogError("[GameMenuManager] ScoreboardManager not assigned!");
                return;
            }

            // Initialize scoreboard manager with the root
            scoreboardManager.Initialize(_root);
        }

        private void SetupSniperOverlayManager() {
            if(_sniperOverlayManager == null) {
                Debug.LogError("[GameMenuManager] SniperOverlayManager not assigned!");
                return;
            }

            // Initialize sniper overlay manager with the root
            _sniperOverlayManager.Initialize(_root);
        }

        #endregion

        #region Menu Navigation

        private void PauseGame() {
            IsPaused = true;
            _pauseMenuPanel.RemoveFromClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            // Update join code display when pausing
            UpdatePauseJoinCodeDisplay();

            if(_localController) {
                _localController.moveInput = Vector2.zero;
            }
        }

        private void UpdatePauseJoinCodeDisplay() {
            if(_pauseJoinCodeLabel == null) return;

            // Try to get join code from SessionManager
            var sessionManager = SessionManager.Instance;
            if(sessionManager?.ActiveSession != null) {
                // Try to get relay code from session properties first
                string joinCode = null;
                if(sessionManager.ActiveSession.Properties.TryGetValue("relayCode", out var prop) &&
                   !string.IsNullOrEmpty(prop.Value)) {
                    joinCode = prop.Value;
                } else if(!string.IsNullOrEmpty(sessionManager.ActiveSession.Code)) {
                    // Fallback to UGS session code
                    joinCode = sessionManager.ActiveSession.Code;
                }

                if(!string.IsNullOrEmpty(joinCode)) {
                    _pauseJoinCodeLabel.text = $"Join Code: {joinCode}";
                    _pauseCopyCodeButton?.SetEnabled(true);
                } else {
                    _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                    _pauseCopyCodeButton?.SetEnabled(false);
                }
            } else {
                _pauseJoinCodeLabel.text = "Join Code: - - - - - -";
                _pauseCopyCodeButton?.SetEnabled(false);
            }
        }

        private void CopyPauseJoinCodeToClipboard() {
            OnButtonClicked();
            if(_pauseJoinCodeLabel == null) return;

            // Extract code from "Join Code: ABC123" → "ABC123"
            var fullText = _pauseJoinCodeLabel.text;
            var code = fullText.Replace("Join Code: ", "").Trim();

            if(string.IsNullOrEmpty(code) || code == "- - - - - -") {
                return; // No code to copy
            }

            GUIUtility.systemCopyBuffer = code;
            Debug.Log($"[GameMenuManager] Copied join code to clipboard: {code}");
        }

        private void ResumeGame() {
            IsPaused = false;
            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.AddToClassList("hidden");
            UnityEngine.Cursor.lockState = CursorLockMode.Locked;
            UnityEngine.Cursor.visible = false;
        }

        private void ShowOptions() {
            if(_cachedSceneName != "Game") return;
            if(optionsMenuManager != null) {
                optionsMenuManager.LoadSettings();
                optionsMenuManager.OnOptionsPanelShown();
            }

            _pauseMenuPanel.AddToClassList("hidden");
            _optionsPanel.RemoveFromClassList("hidden");
        }

        private void HideOptions() {
            if(_cachedSceneName != "Game") return;
            _optionsPanel.AddToClassList("hidden");
            _pauseMenuPanel.RemoveFromClassList("hidden");
        }

        private async void QuitToMenu() {
            try {
                await SessionManager.Instance.LeaveToMainMenuAsync();

                var root = uiDocument.rootVisualElement;
                var rootContainer = root.Q<VisualElement>("root-container");
                rootContainer.style.display = DisplayStyle.None;
                _pauseMenuPanel.AddToClassList("hidden");
                _optionsPanel.AddToClassList("hidden");
                IsPaused = false;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        #endregion

        #region Match Flow / Post-Match

        #endregion
    }
}