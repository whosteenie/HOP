using Game.Match;
using Game.Player.Core;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;
using SessionManager = Network.Session.SessionManager;

namespace Game.UI.Screens {
    /// <summary>
    /// Manages the scoreboard UI, including FFA and TDM scoreboards, player rows, and match timer.
    /// Coordinates subsystems: registry, player data, header, top bar, row factory, table updater.
    /// </summary>
    public class ScoreboardManager : MonoBehaviour {
        public static ScoreboardManager Instance { get; private set; }

        [Header("Player Icons")]
        [SerializeField] private Sprite[] playerIconSprites;

        [Header("UI Templates")]
        [SerializeField] private VisualTreeAsset scoreboardRowTemplate;

        // UI refs (resolved in Initialize)
        private VisualElement _root;
        private VisualElement _scoreboardPanel;
        private VisualElement _playerRows;
        private VisualElement _scoreboardContainer;
        private VisualElement _tdmScoreboardContainer;
        private VisualElement _enemyTeamRows;
        private VisualElement _yourTeamRows;
        private Label _enemyScoreValue;
        private Label _yourScoreValue;
        private Label _matchTimerLabel;
        private VisualElement _leftScoreContainer;
        private VisualElement _rightScoreContainer;
        private Label _leftScoreValue;
        private Label _rightScoreValue;

        private float _lastScoreUpdateTime;
        private const float ScoreUpdateInterval = 0.1f;

        private PlayerController _localController;
        private MatchSettingsManager _cachedMatchSettings;
        private string _cachedSceneName;
        private bool _hoverDisabledForMouseLook;

        // Subsystems
        private ScoreboardPlayerData _playerData;
        private ScoreboardPlayerRegistry _registry;
        private ScoreboardHeader _header;
        private ScoreboardTopBar _topBar;
        private ScoreboardRowFactory _rowFactory;
        private ScoreboardTableUpdater _tableUpdater;

        public bool IsScoreboardVisible { get; private set; }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnEnable() {
            EventBus.Subscribe<SetMatchTimeEvent>(OnSetMatchTime);
            EventBus.Subscribe<ShowScoreboardEvent>(OnShowScoreboard);
            EventBus.Subscribe<HideScoreboardEvent>(OnHideScoreboard);
            EventBus.Subscribe<ScoreboardRefreshRequestedEvent>(OnScoreboardRefreshRequested);
            EventBus.Subscribe<ScoreboardGamemodeChangedEvent>(OnScoreboardGamemodeChanged);
            EventBus.Subscribe<HideScoreDisplayEvent>(OnHideScoreDisplay);
            EventBus.Subscribe<ShowScoreDisplayEvent>(OnShowScoreDisplay);
            EventBus.Subscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Subscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
            EventBus.Subscribe<PlayerDiedEvent>(OnPlayerDied);
            MatchPlayerStateProxy.StateRegistered += OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered += OnPlayerStateUnregistered;
        }

        private void OnDisable() {
            EventBus.Unsubscribe<SetMatchTimeEvent>(OnSetMatchTime);
            EventBus.Unsubscribe<ShowScoreboardEvent>(OnShowScoreboard);
            EventBus.Unsubscribe<HideScoreboardEvent>(OnHideScoreboard);
            EventBus.Unsubscribe<ScoreboardRefreshRequestedEvent>(OnScoreboardRefreshRequested);
            EventBus.Unsubscribe<ScoreboardGamemodeChangedEvent>(OnScoreboardGamemodeChanged);
            EventBus.Unsubscribe<HideScoreDisplayEvent>(OnHideScoreDisplay);
            EventBus.Unsubscribe<ShowScoreDisplayEvent>(OnShowScoreDisplay);
            EventBus.Unsubscribe<PlayerNetworkSpawnedEvent>(OnPlayerNetworkSpawned);
            EventBus.Unsubscribe<PlayerNetworkDespawnedEvent>(OnPlayerNetworkDespawned);
            EventBus.Unsubscribe<PlayerDiedEvent>(OnPlayerDied);
            MatchPlayerStateProxy.StateRegistered -= OnPlayerStateRegistered;
            MatchPlayerStateProxy.StateUnregistered -= OnPlayerStateUnregistered;
            if(NetworkManager.Singleton != null)
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ClearCachedPlayerData();
            _registry?.ClearProfileStateSubscriptions();
        }

        private void OnSetMatchTime(SetMatchTimeEvent evt) => _topBar?.SetMatchTime(evt.Seconds);
        private void OnPlayerDied(PlayerDiedEvent evt) { UpdateScoreboard(); UpdateScoreDisplay(); }
        private void OnShowScoreboard(ShowScoreboardEvent evt) => ShowScoreboard();
        private void OnHideScoreboard(HideScoreboardEvent evt) => HideScoreboard();
        private void OnScoreboardRefreshRequested(ScoreboardRefreshRequestedEvent evt) => UpdateScoreboard();
        private void OnScoreboardGamemodeChanged(ScoreboardGamemodeChangedEvent evt) => RefreshGamemode();
        private void OnHideScoreDisplay(HideScoreDisplayEvent evt) => _topBar?.HideScoreDisplay();
        private void OnShowScoreDisplay(ShowScoreDisplayEvent evt) => _topBar?.ShowScoreDisplay();
        private void OnPlayerNetworkSpawned(PlayerNetworkSpawnedEvent evt) => _registry?.Register(evt.Player);
        private void OnPlayerNetworkDespawned(PlayerNetworkDespawnedEvent evt) => _registry?.Unregister(evt.Player);
        private void OnPlayerStateRegistered(ulong id, MatchPlayerStateProxy proxy) => _registry?.OnStateRegistered(id, proxy);
        private void OnPlayerStateUnregistered(ulong id, MatchPlayerStateProxy proxy) => _registry?.OnStateUnregistered(id, proxy);

        public void Initialize(VisualElement root) {
            _root = root;
            UpdateCachedSceneName();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;

            _scoreboardPanel = _root.Q<VisualElement>("scoreboard-panel");
            _playerRows = _root.Q<VisualElement>("player-rows");
            _scoreboardContainer = _root.Q<VisualElement>("scoreboard-container");
            _tdmScoreboardContainer = _root.Q<VisualElement>("tdm-scoreboard-container");
            _enemyTeamRows = _root.Q<VisualElement>("enemy-team-rows");
            _yourTeamRows = _root.Q<VisualElement>("your-team-rows");
            _enemyScoreValue = _root.Q<Label>("enemy-score-value");
            _yourScoreValue = _root.Q<Label>("your-score-value");
            _matchTimerLabel = _root.Q<Label>("match-timer-label");
            _leftScoreContainer = _root.Q<VisualElement>("left-score-container");
            _rightScoreContainer = _root.Q<VisualElement>("right-score-container");
            _leftScoreValue = _root.Q<Label>("left-score-value");
            _rightScoreValue = _root.Q<Label>("right-score-value");

            _cachedMatchSettings = MatchSettingsManager.Instance;

            _playerData = new ScoreboardPlayerData();
            _tableUpdater = new ScoreboardTableUpdater();
            _rowFactory = new ScoreboardRowFactory(scoreboardRowTemplate, _playerData, playerIconSprites, this);
            _header = new ScoreboardHeader(_root, this);
            _topBar = new ScoreboardTopBar(_matchTimerLabel, _leftScoreContainer, _rightScoreContainer, _leftScoreValue, _rightScoreValue);
            _registry = new ScoreboardPlayerRegistry(() => {
                _tableUpdater.ClearCaches();
                UpdateScoreboard();
            });

            if(NetworkManager.Singleton != null) {
                NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
                NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            }

            foreach(var player in PlayerController.SpawnedPlayers)
                _registry.Register(player);
            _topBar.ApplyInitialTimerState();
        }

        private void Update() {
            if(_localController == null && SessionManager.IsGameplaySceneName(_cachedSceneName))
                FindLocalController();
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName) || !(Time.time - _lastScoreUpdateTime >= ScoreUpdateInterval)) return;
            UpdateScoreDisplay();
            _lastScoreUpdateTime = Time.time;
            if(!IsScoreboardVisible) return;
            RefreshHoverStateForCursorMode();
            _rowFactory?.UpdateSpeakingIndicators(_registry?.GetAllPlayers());
        }

        private void FindLocalController() {
            var all = _registry?.GetAllPlayers();
            if(all == null) return;
            foreach(var c in all) {
                if(c == null || !c.IsOwner) continue;
                _localController = c;
                break;
            }
        }

        private void UpdateCachedSceneName() {
            var scene = SceneManager.GetActiveScene();
            if(scene.IsValid()) _cachedSceneName = scene.name;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => UpdateCachedSceneName();

        private void ClearCachedPlayerData() {
            _playerData?.Clear();
            _tableUpdater?.ClearCaches();
            _rowFactory?.ClearSpeakingIndicators();
        }

        private void OnClientDisconnected(ulong clientId) {
            var all = _registry?.GetAllPlayers();
            if(all != null) _playerData?.RemovePlayersByClientId(clientId, all);
        }

        private bool IsTagMode() {
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;
            return _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
        }

        private bool IsTeamBased() {
            if(_cachedMatchSettings == null) _cachedMatchSettings = MatchSettingsManager.Instance;
            return _cachedMatchSettings != null && MatchSettingsManager.IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
        }

        private void RefreshGamemode() {
            _cachedMatchSettings = null;
            _header?.InvalidateHeaderCache();
            UpdateScoreboardTitle();
        }

        private void UpdateScoreboardTitle() {
            _cachedMatchSettings = MatchSettingsManager.Instance;
            _header?.UpdateTitles(_cachedMatchSettings, _cachedSceneName);
        }

        private void ShowScoreboard() {
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName)) return;
            IsScoreboardVisible = true;
            var rootContainer = _root.Q<VisualElement>("root-container");
            if(rootContainer != null) rootContainer.style.display = DisplayStyle.Flex;
            UpdateScoreboardTitle();
            _scoreboardPanel.style.display = DisplayStyle.Flex;
            _scoreboardPanel.RemoveFromClassList("hidden");
            RefreshHoverStateForCursorMode(force: true);
            _header?.UpdateHeaderColumns(IsTagMode());
            UpdateScoreboard();
        }

        private void HideScoreboard() {
            if(!SessionManager.IsGameplaySceneName(_cachedSceneName)) return;
            IsScoreboardVisible = false;
            _scoreboardPanel.style.display = StyleKeyword.Null;
            _scoreboardPanel.AddToClassList("hidden");
            _scoreboardPanel.EnableInClassList("scoreboard-hover-disabled", false);
            _hoverDisabledForMouseLook = false;
            if(Cursor.lockState == CursorLockMode.None) {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                if(PlayerController.LocalPlayer != null) PlayerController.LocalPlayer.LockLook = false;
            }
            if(InGameContextMenuManager.Instance != null) InGameContextMenuManager.Instance.Hide();
        }

        private void RefreshHoverStateForCursorMode(bool force = false) {
            if(_scoreboardPanel == null) return;
            var shouldDisable = Cursor.lockState == CursorLockMode.Locked || !Cursor.visible;
            if(!force && shouldDisable == _hoverDisabledForMouseLook) return;
            _hoverDisabledForMouseLook = shouldDisable;
            _scoreboardPanel.EnableInClassList("scoreboard-hover-disabled", shouldDisable);
        }

        private void UpdateScoreboard() {
            if(_root == null) return;
            var allControllers = _registry?.GetAllPlayers();
            if(allControllers == null) return;
            if(IsTeamBased()) {
                var updated = ScoreboardTableUpdater.UpdateTdm(allControllers, _enemyTeamRows, _yourTeamRows,
                    _scoreboardContainer, _tdmScoreboardContainer, _enemyScoreValue, _yourScoreValue,
                    _cachedMatchSettings, _rowFactory, _playerData, _root, this);
                if(!updated)
                    _tableUpdater.UpdateFfa(allControllers, _playerRows, _scoreboardContainer, _tdmScoreboardContainer, IsTagMode(), _rowFactory, _playerData, _root, this);
            } else {
                _tableUpdater.UpdateFfa(allControllers, _playerRows, _scoreboardContainer, _tdmScoreboardContainer, IsTagMode(), _rowFactory, _playerData, _root, this);
            }
        }

        private void UpdateScoreDisplay() {
            _topBar?.UpdateScoreDisplay(_registry, _playerData, _cachedMatchSettings ? _cachedMatchSettings : MatchSettingsManager.Instance, _localController);
        }

        public bool GetLocalPlayerPlacement(out int placement, out int totalPlayers) {
            placement = 0;
            totalPlayers = 0;
            var allControllers = _registry?.GetAllPlayers();
            if(allControllers == null) return false;
            totalPlayers = allControllers.Count;
            if(totalPlayers == 0) return false;
            var isTagMode = IsTagMode();
            var sortedPlayers = _playerData.BuildSortedPlayerList(allControllers, isTagMode);
            var localClientId = NetworkManager.Singleton.LocalClientId;
            for(var i = 0; i < sortedPlayers.Count; i++) {
                if(sortedPlayers[i].OwnerClientId != localClientId) continue;
                placement = i + 1;
                return true;
            }
            return false;
        }
    }
}
