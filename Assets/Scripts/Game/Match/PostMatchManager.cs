using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Diagnostics;
using Events;
using Network.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using SessionManager = Network.Session.SessionManager;

namespace Game.Match {
    public class PostMatchManager : NetworkBehaviour {
        public static PostMatchManager Instance { get; private set; }

        [Header("Podium Setup")]
        [SerializeField] private CinemachineCamera podiumCamera;

        [SerializeField] private Transform firstPlaceAnchor;
        [SerializeField] private Transform secondPlaceAnchor;
        [SerializeField] private Transform thirdPlaceAnchor;

        [Header("UI")]
        [SerializeField] private UIDocument uiDocument;

        [Header("Timing")]
        [Tooltip("How long to stay on podium view before returning to menu.")] [SerializeField]
        private float podiumDuration = 10f;

        // Keep this roughly in sync with SceneTransitionManager.fadeDuration
        [SerializeField] private float fadeDuration = 0.5f;
        [SerializeField] private float fadeBuffer = 0.1f;
        [SerializeField] private float worldSpaceCardOffsetScale = 0.18f;
        [SerializeField] private float worldSpaceCardOffsetMinPx = 12f;
        [SerializeField] private float worldSpaceCardOffsetMaxPx = 56f;
        [SerializeField] private float podiumCardFixedHeight = 88f;
        [SerializeField] private float podiumCardAnchorYOffset;

        // Podium UI
        private VisualElement _root;
        private VisualElement _podiumContainer;
        private VisualElement _podiumFirstSlot;
        private VisualElement _podiumSecondSlot;
        private VisualElement _podiumThirdSlot;

        private Label _podiumFirstName;
        private Label _podiumSecondName;
        private Label _podiumThirdName;

        private Label _podiumFirstKills;
        private Label _podiumSecondKills;
        private Label _podiumThirdKills;

        // HUD elements for hiding/showing
        private VisualElement _matchTimerContainer;

        private Coroutine _podiumWorldSpaceTrackingRoutine;
        private bool _missingUiReferenceLogged;

        private const float AssumedCharacterHeight = 1.9f;

        public bool PostMatchFlowStarted { get; private set; }
        private SpawnPoint.Team _winningTeam = SpawnPoint.Team.None;
        private bool _matchEndedEventsBound;
        private bool _sessionOwnerCallbacksRegistered;
        private bool HasPostMatchAuthority => NetworkAuthority.HasGlobalAuthority(this);
        private bool IsPodiumBlackoutActive { get; set; }
        public static bool IsPodiumBlackoutActiveLocal => Instance != null && Instance.IsPodiumBlackoutActive;
        private Coroutine _localReturnToMenuRoutine;
        private Camera _mainCamera;

        /// <summary>
        /// True once fade-to-black is fully black. Use this for movement lock so WASD stays active during the fade.
        /// </summary>
        public static bool IsPostMatchMovementLockedLocal { get; private set; }

        private void Start() {
            _mainCamera = Camera.main;
        }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if(podiumCamera != null) {
                podiumCamera.gameObject.SetActive(false);
            }

            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }

            if(uiDocument == null) {
                DevLog.LogError("[PostMatchManager] UIDocument is not assigned on PostMatchManager. " +
                               "Assign UIDocument on Game scene object 'PostMatchManager'.");
                return;
            }

            _root = uiDocument.rootVisualElement;

        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            RegisterSessionOwnerCallbacks();
            BindMatchEndedEvent();
            EventBus.Subscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Subscribe<GameplayUiDocumentReadyEvent>(OnGameplayUiDocumentReady);
        }

        private void InitializeUI() {
            if(_root == null) return;

            // Podium UI
            _podiumContainer = _root.Q<VisualElement>("podium-nameplates-container");
            _podiumFirstSlot = _root.Q<VisualElement>("podium-first-slot");
            _podiumSecondSlot = _root.Q<VisualElement>("podium-second-slot");
            _podiumThirdSlot = _root.Q<VisualElement>("podium-third-slot");

            _podiumFirstName = _root.Q<Label>("podium-first-name");
            _podiumSecondName = _root.Q<Label>("podium-second-name");
            _podiumThirdName = _root.Q<Label>("podium-third-name");

            _podiumFirstKills = _root.Q<Label>("podium-first-kills");
            _podiumSecondKills = _root.Q<Label>("podium-second-kills");
            _podiumThirdKills = _root.Q<Label>("podium-third-kills");

            // HUD elements
            _matchTimerContainer = _root.Q<VisualElement>("match-timer-container");
        }

        private bool EnsureUiReferencesBound() {
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }

            if(uiDocument == null) {
                if(_missingUiReferenceLogged) return false;
                DevLog.LogError("[PostMatchManager] UIDocument is missing; cannot bind post-match UI references.", this);
                _missingUiReferenceLogged = true;
                return false;
            }

            var currentRoot = uiDocument.rootVisualElement;
            if(currentRoot == null) {
                if(_missingUiReferenceLogged) return false;
                DevLog.LogError(
                    "[PostMatchManager] UIDocument rootVisualElement is null; cannot bind post-match UI references.",
                    this);
                _missingUiReferenceLogged = true;
                return false;
            }

            if(!ReferenceEquals(_root, currentRoot) || _podiumContainer == null || _matchTimerContainer == null) {
                _root = currentRoot;
                InitializeUI();
            }

            var hasRequiredPodiumElements =
                _podiumContainer != null &&
                _podiumFirstSlot != null &&
                _podiumSecondSlot != null &&
                _podiumThirdSlot != null &&
                _podiumFirstName != null &&
                _podiumSecondName != null &&
                _podiumThirdName != null &&
                _podiumFirstKills != null &&
                _podiumSecondKills != null &&
                _podiumThirdKills != null;

            if(!hasRequiredPodiumElements || _matchTimerContainer == null) {
                if(_missingUiReferenceLogged) return false;
                DevLog.LogError(
                    "[PostMatchManager] Required post-match UI elements are missing from GameMenu.uxml. " +
                    "Expected podium slots/labels and match-timer-container.",
                    this);
                _missingUiReferenceLogged = true;
                return false;
            }

            _missingUiReferenceLogged = false;
            return true;
        }

        private void OnGameplayUiDocumentReady(GameplayUiDocumentReadyEvent evt) {
            if(evt?.Document == null) return;
            if(uiDocument == evt.Document && uiDocument.rootVisualElement != null) return;
            uiDocument = evt.Document;
        }

        public override void OnNetworkDespawn() {
            UnbindMatchEndedEvent();
            UnregisterSessionOwnerCallbacks();
            EventBus.Unsubscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Unsubscribe<GameplayUiDocumentReadyEvent>(OnGameplayUiDocumentReady);
            ResetPostMatchUiState();
            base.OnNetworkDespawn();
            if(Instance == this)
                Instance = null;
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnbindMatchEndedEvent();
            UnregisterSessionOwnerCallbacks();
            EventBus.Unsubscribe<PostMatchBlackoutReadyEvent>(OnPostMatchBlackoutReady);
            EventBus.Unsubscribe<GameplayUiDocumentReadyEvent>(OnGameplayUiDocumentReady);
            if(Instance == this) {
                Instance = null;
            }
        }

        private void RegisterSessionOwnerCallbacks() {
            if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = true;
        }

        private void UnregisterSessionOwnerCallbacks() {
            if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
            NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
            _sessionOwnerCallbacksRegistered = false;
        }

        private void OnSessionOwnerPromoted(ulong _) {
            if(!HasPostMatchAuthority) {
                UnbindMatchEndedEvent();
                return;
            }

            NetworkAuthority.TryConfigureSessionOwnerObject(this);
            BindMatchEndedEvent();
        }

        private void BindMatchEndedEvent() {
            if(!HasPostMatchAuthority || _matchEndedEventsBound) return;
            EventBus.Subscribe<MatchEndedEvent>(OnMatchEnded);
            _matchEndedEventsBound = true;
        }

        private void UnbindMatchEndedEvent() {
            if(!_matchEndedEventsBound) return;
            EventBus.Unsubscribe<MatchEndedEvent>(OnMatchEnded);
            _matchEndedEventsBound = false;
        }

        private void OnMatchEnded(MatchEndedEvent _) {
            BeginPostMatchFromTimer();
        }

        /// <summary>
        /// Called from MatchTimerManager on the server when the timer hits 0.
        /// </summary>
        private void BeginPostMatchFromTimer() {
            if(!HasPostMatchAuthority) {
                DevLog.LogWarning("[PostMatchManager] BeginPostMatchFromTimer called without match authority.");
                return;
            }

            // Prevent post-match from starting during pre-match countdown
            if(MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch) {
                DevLog.LogWarning("[PostMatchManager] Cannot start post-match during pre-match countdown!");
                return;
            }

            if(PostMatchFlowStarted) {
                DevLog.LogWarning("[MatchTimerManager] Post match is already started!");
                return;
            }

            ResetPostMatchUiState();
            PostMatchFlowStarted = true;
            EventBus.Publish(new PostMatchStartedEvent());
            StartCoroutine(PostMatchSequence());
        }

        /// <summary>
        /// Called from HopballSpawnManager when a team reaches the win score.
        /// </summary>
        public void BeginPostMatchFromScore(SpawnPoint.Team winningTeam) {
            if(!HasPostMatchAuthority) {
                DevLog.LogWarning("[PostMatchManager] BeginPostMatchFromScore called without match authority.");
                return;
            }

            if(PostMatchFlowStarted) {
                DevLog.LogWarning("[PostMatchManager] Post match is already started!");
                return;
            }

            ResetPostMatchUiState();
            _winningTeam = winningTeam;
            PostMatchFlowStarted = true;
            EventBus.Publish(new PostMatchStartedEvent());
            StartCoroutine(PostMatchSequence());
        }

        private IEnumerator PostMatchSequence() {
            // 1) Tell all clients to fade to black + hide HUD bits
            FadeToPodiumClientRpc();

            // 1b) Announce results and award XP
            // We need to fetch the winner if we came from score.
            // Note: Currently PostMatchSequence doesn't know the winner if called via Coroutine from StartCoroutine(PostMatchSequence()).
            // Implementation Gaps: We need to store the winning team in a member variable before starting sequence.
            AnnounceMatchResultClientRpc(_winningTeam);

            yield return new WaitForSeconds(fadeDuration + fadeBuffer);

            // 2) On server: compute top 3 and teleport them
            SetupTopThreeOnServer();

            // 3) Switch everyone to the podium camera & fade back in
            ActivatePodiumCameraClientRpc();

            yield return null; // small frame delay before fade in
            FadeInFromPodiumClientRpc();

            // 4) Stay on podium for a bit
            yield return new WaitForSeconds(podiumDuration);
        }

        /// <summary>
        /// Server-side: pick top 3 players, teleport them to podium anchors,
        /// and face them toward the podium camera. Also hide non-top3 visuals.
        /// </summary>
        private void SetupTopThreeOnServer() {
            var allPlayers = ResolveActivePlayerStates().ToList();

            if(allPlayers.Count == 0) return;

            // Check if we're in Tag mode
            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            // Sort by appropriate stat based on gamemode
            List<MatchPlayerStateProxy> sorted;
            if(isTagMode) {
                // Tag mode: sort by time tagged (lowest first), then by tags as tie-breaker
                sorted = allPlayers
                    .OrderBy(p => p.timeTagged.Value)
                    .ThenByDescending(p => p.tags.Value)
                    .ToList();
            } else {
                // Normal mode: sort by kills descending, then by damage as tie-breaker
                sorted = allPlayers
                    .OrderByDescending(p => p.kills.Value)
                    .ThenByDescending(p => p.damageDealt.Value)
                    .ToList();
            }

            var topThree = new List<MatchPlayerStateProxy>();
            if(sorted.Count > 0) topThree.Add(sorted[0]);
            if(sorted.Count > 1) topThree.Add(sorted[1]);
            if(sorted.Count > 2) topThree.Add(sorted[2]);

            foreach(var player in topThree) {
                if(player == null) continue;
                EventBus.Publish(new PostMatchPodiumPrepareRequestedEvent(player.representedClientId.Value));
            }

            // Zero out momentum only after blackout is fully active.
            foreach(var p in allPlayers) {
                if(p == null) continue;
                EventBus.Publish(new PostMatchResetVelocityRequestedEvent(p.representedClientId.Value));
            }

            // Teleport & face podium
            for(var i = 0; i < topThree.Count; i++) {
                var player = topThree[i];

                var anchor = i switch {
                    0 => firstPlaceAnchor,
                    1 => secondPlaceAnchor,
                    2 => thirdPlaceAnchor,
                    _ => null
                };

                if(anchor == null || player == null) continue;

                TryDetachPlayerObjectFromParent(player.representedClientId.Value);
                EventBus.Publish(new PostMatchTeleportRequestedEvent(player.representedClientId.Value, anchor.position,
                    anchor.rotation));
                EventBus.Publish(new PostMatchSnapVisualsRequestedEvent(player.representedClientId.Value));
            }

            // Hide non-top3 player models (world models only, not cameras)
            foreach(var p in allPlayers) {
                var isOnPodium = topThree.Contains(p);
                EventBus.Publish(new PostMatchWorldModelVisibilityRequestedEvent(p.representedClientId.Value,
                    isOnPodium));
            }

            var firstName = topThree.Count > 0 ? topThree[0].playerName.Value.ToString() : string.Empty;
            var firstScore = 0;
            if(topThree.Count > 0) {
                firstScore = isTagMode ? topThree[0].timeTagged.Value : topThree[0].kills.Value;
            }

            var secondName = topThree.Count > 1 ? topThree[1].playerName.Value.ToString() : string.Empty;
            var secondScore = 0;
            if(topThree.Count > 1) {
                secondScore = isTagMode ? topThree[1].timeTagged.Value : topThree[1].kills.Value;
            }

            var thirdName = topThree.Count > 2 ? topThree[2].playerName.Value.ToString() : string.Empty;
            var thirdScore = 0;
            if(topThree.Count > 2) {
                thirdScore = isTagMode ? topThree[2].timeTagged.Value : topThree[2].kills.Value;
            }

            UpdatePodiumUiClientRpc(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
        }

        // --- CLIENT RPCs ---
        /// <summary>Fades to black and prepares HUD for podium (all clients).</summary>
        [Rpc(SendTo.Everyone)]
        private void FadeToPodiumClientRpc() {
            try {
                EnsureUiReferencesBound();
                ResetPostMatchUiState();
                IsPodiumBlackoutActive = false;
                IsPostMatchMovementLockedLocal = false;
                EventBus.Publish(new RequestPostMatchBlackoutTransitionEvent());

                // Enter post-match HUD mode (hide crosshair, timer, etc.)
                EventBus.Publish(new SetPostMatchMenuStateEvent(true));

                // Find local controller and disable sniper overlay (do NOT lock movement yet - wait for fade to complete)
                if(NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) return;
                EventBus.Publish(new PostMatchSniperOverlayDisableRequestedEvent(NetworkManager.Singleton.LocalClientId,
                    false));

            } catch(Exception e) {
                DebugHelpers.PublishCriticalError($"PostMatchManager.FadeToPodiumClientRpc failed: {e.Message}",
                    "PostMatchManager.FadeToPodiumClientRpc", e);
                Debug.LogException(e);
            }
        }

        [Rpc(SendTo.Everyone)]
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private void AnnounceMatchResultClientRpc(SpawnPoint.Team winningTeam) {
            if(NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) return;

            var localClientId = NetworkManager.Singleton.LocalClientId;
            const int matchCompletionXp = 100;
            var bonusXp = 0;
            var didWin = false;
            var didLose = false;
            var placement = 0;
            var recordMatchCompletion = false;
            var averageSpeed = 0f;

            var localTeam = SpawnPoint.Team.None;
            var localState = MatchPlayerStateProxy.GetForPlayer(localClientId);
            if(localState != null) {
                localTeam = (SpawnPoint.Team)localState.teamId.Value;
            }

            var matchSettings = MatchSettingsManager.Instance;
            var isTrackedGamemode = matchSettings != null &&
                                    matchSettings.selectedGameModeId is "Team Deathmatch" or "Hopball" or "KOTH";

            if(isTrackedGamemode && localTeam != SpawnPoint.Team.None) {
                if(localTeam == winningTeam) {
                    bonusXp = 500;
                    didWin = true;
                } else if(winningTeam != SpawnPoint.Team.None) {
                    didLose = true;
                }

                recordMatchCompletion = matchSettings != null;
            } else {
                if(TryGetLocalFfaPlacement(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag",
                       out var rank)) {
                    placement = rank;
                    if(rank == 1) {
                        bonusXp = 500;
                        didWin = true;
                    } else {
                        didLose = true;
                    }

                    recordMatchCompletion = matchSettings != null;
                }
            }

            if(localState != null) {
                averageSpeed = localState.averageVelocity.Value;
            }

            EventBus.Publish(new MatchProgressionResolvedEvent(localClientId, matchCompletionXp, bonusXp, didWin,
                didLose, matchSettings != null ? matchSettings.selectedGameModeId : null, placement,
                recordMatchCompletion, averageSpeed));
        }

        /// <summary>Fades back in from black after podium (all clients).</summary>
        [Rpc(SendTo.Everyone)]
        private void FadeInFromPodiumClientRpc() {
            IsPodiumBlackoutActive = false;
            EventBus.Publish(new RequestPostMatchFadeInEvent());

            StartReturnToMenuCountdown();
        }

        [Rpc(SendTo.Everyone)]
        private void ActivatePodiumCameraClientRpc() {
            if(podiumCamera == null) return;

            // Enable podium camera
            podiumCamera.gameObject.SetActive(true);

            // Optionally, give it the highest priority if you're using multiple cams
            // podiumCamera.Priority = 100;
        }

        [Rpc(SendTo.Everyone)]
        private void UpdatePodiumUiClientRpc(
            string firstName, int firstScore,
            string secondName, int secondScore,
            string thirdName, int thirdScore
        ) {
            SetPodiumSlots(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
            StartPodiumWorldTracking();
        }

        private void SetPodiumSlots(
            string firstName, int firstKills,
            string secondName, int secondKills,
            string thirdName, int thirdKills) {
            if(!EnsureUiReferencesBound()) return;
            if(_podiumContainer == null)
                return;

            // Show the container as soon as we have data
            _podiumContainer.style.display = DisplayStyle.Flex;

            // Allow pointer events to pass through the container so pause menu is clickable
            // Only the actual podium slots should capture pointer events
            _podiumContainer.pickingMode = PickingMode.Ignore;

            SetPodiumSlot(_podiumFirstSlot, _podiumFirstName, _podiumFirstKills, firstName, firstKills);
            SetPodiumSlot(_podiumSecondSlot, _podiumSecondName, _podiumSecondKills, secondName, secondKills);
            SetPodiumSlot(_podiumThirdSlot, _podiumThirdName, _podiumThirdKills, thirdName, thirdKills);

            EventBus.Publish(new RequestShowPostMatchXpEvent());
        }

        private static void SetPodiumSlot(
            VisualElement slotRoot,
            Label nameLabel,
            Label killsLabel,
            string playerName,
            int kills) {
            if(slotRoot == null || nameLabel == null || killsLabel == null)
                return;

            var hasPlayer = !string.IsNullOrEmpty(playerName);

            slotRoot.style.display = hasPlayer ? DisplayStyle.Flex : DisplayStyle.None;
            nameLabel.text = hasPlayer ? playerName : "---";
            killsLabel.text = hasPlayer ? kills.ToString() : "0";
        }

        private static bool TryGetLocalFfaPlacement(bool isTagMode, out int placement) {
            placement = 0;

            var localClient = NetworkManager.Singleton.LocalClient;
            if(localClient?.PlayerObject == null) return false;

            var sortedPlayers = ResolveActivePlayerStates()
                .OrderBy(p => isTagMode ? GetTagSortScore(p) : -p.kills.Value)
                .ThenBy(p => p.representedClientId.Value)
                .ToList();

            var totalPlayers = sortedPlayers.Count;
            if(totalPlayers == 0) return false;

            for(var i = 0; i < sortedPlayers.Count; i++) {
                if(sortedPlayers[i].representedClientId.Value != localClient.ClientId) continue;
                placement = i + 1;
                return true;
            }

            return false;
        }

        private static int GetTagSortScore(MatchPlayerStateProxy player) {
            return player != null ? player.timeTagged.Value : int.MaxValue;
        }

        /// <summary>
        /// Hides only the in-game HUD elements, but leaves pause/scoreboard usable.
        /// </summary>
        private void HideInGameHudForPostMatch() {
            EnsureUiReferencesBound();

            // Hide individual HUD elements
            EventBus.Publish(new HideKillFeedEvent());
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.None;
            EventBus.Publish(new HideScoreDisplayEvent());

            EventBus.Publish(new HideGrappleUIEvent());
        }

        public void ShowInGameHudAfterPostMatch() {
            EnsureUiReferencesBound();
            ResetPostMatchUiState();
            foreach(var state in ResolveActivePlayerStates()) {
                if(state == null) continue;
                EventBus.Publish(new PostMatchControlLockRequestedEvent(state.representedClientId.Value, false, true,
                    true));
            }

            // Show individual HUD elements
            EventBus.Publish(new ShowKillFeedEvent());
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            EventBus.Publish(new ShowScoreDisplayEvent());

            EventBus.Publish(new ShowGrappleUIEvent());
        }

        /// <summary>Starts tracking podium slots in world space.</summary>
        private void StartPodiumWorldTracking() {
            if(!EnsureUiReferencesBound()) return;
            StopPodiumWorldTracking();
            if(_podiumContainer == null) return;

            PrepareContainerForWorldSpace(_podiumContainer);
            PrepareSlotForWorldSpace(_podiumFirstSlot);
            PrepareSlotForWorldSpace(_podiumSecondSlot);
            PrepareSlotForWorldSpace(_podiumThirdSlot);

            _podiumWorldSpaceTrackingRoutine = StartCoroutine(PodiumWorldTrackingCoroutine());
        }

        private void StopPodiumWorldTracking() {
            if(_podiumWorldSpaceTrackingRoutine == null) return;
            StopCoroutine(_podiumWorldSpaceTrackingRoutine);
            _podiumWorldSpaceTrackingRoutine = null;
        }

        private IEnumerator PodiumWorldTrackingCoroutine() {
            while(_podiumContainer != null && _podiumContainer.resolvedStyle.display != DisplayStyle.None) {
                UpdatePodiumWorldPositions();
                yield return null;
            }

            _podiumWorldSpaceTrackingRoutine = null;
        }

        private void UpdatePodiumWorldPositions() {
            if(_root?.panel == null) return;

            var worldCamera = ResolveWorldCamera();
            if(worldCamera == null) return;

            UpdatePodiumSlotPosition(_podiumFirstSlot, firstPlaceAnchor, worldCamera);
            UpdatePodiumSlotPosition(_podiumSecondSlot, secondPlaceAnchor, worldCamera);
            UpdatePodiumSlotPosition(_podiumThirdSlot, thirdPlaceAnchor, worldCamera);
        }

        private void UpdatePodiumSlotPosition(VisualElement slot, Transform slotAnchor, Camera worldCamera) {
            if(slot == null) return;

            Vector3 targetWorldPosition;
            if(slotAnchor != null) {
                targetWorldPosition = slotAnchor.position + Vector3.up * podiumCardAnchorYOffset;
            } else {
                slot.style.display = DisplayStyle.None;
                return;
            }

            var feetScreen = worldCamera.WorldToScreenPoint(targetWorldPosition);
            if(feetScreen.z <= 0f) {
                slot.style.display = DisplayStyle.None;
                return;
            }

            var panelFeet =
                RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, targetWorldPosition, worldCamera);
            var slotWidth = GetResolvedLength(slot.resolvedStyle.width, 200f);
            var slotHeight = Mathf.Max(1f, podiumCardFixedHeight);
            var downOffsetPx = ComputeBelowFeetOffsetPixels(slotAnchor, worldCamera);

            slot.style.display = DisplayStyle.Flex;
            slot.style.left = panelFeet.x - slotWidth * 0.5f;
            slot.style.top = panelFeet.y + downOffsetPx;
            slot.style.bottom = StyleKeyword.Null;
            slot.style.right = StyleKeyword.Null;
            slot.style.translate = new Translate(0f, 0f);
            slot.style.height = slotHeight;
            slot.style.minHeight = slotHeight;
            slot.style.maxHeight = slotHeight;
        }

        private float ComputeBelowFeetOffsetPixels(Transform slotAnchor, Camera worldCamera) {
            if(worldCamera == null) return worldSpaceCardOffsetMinPx;
            if(slotAnchor == null) return worldSpaceCardOffsetMinPx;

            var distance = Vector3.Distance(worldCamera.transform.position, slotAnchor.position);
            if(distance <= 0.001f) return worldSpaceCardOffsetMinPx;

            float pixelsPerMeter;
            if(worldCamera.orthographic) {
                var worldHeight = worldCamera.orthographicSize * 2f;
                pixelsPerMeter = Screen.height / Mathf.Max(0.001f, worldHeight);
            } else {
                var fovRad = worldCamera.fieldOfView * Mathf.Deg2Rad;
                var frustumHeightAtDistance = 2f * distance * Mathf.Tan(fovRad * 0.5f);
                pixelsPerMeter = Screen.height / Mathf.Max(0.001f, frustumHeightAtDistance);
            }

            var characterHeightPx = AssumedCharacterHeight * pixelsPerMeter;
            var desiredOffset = characterHeightPx * worldSpaceCardOffsetScale;
            return Mathf.Clamp(desiredOffset, worldSpaceCardOffsetMinPx, worldSpaceCardOffsetMaxPx);
        }

        private static float GetResolvedLength(float value, float fallback) {
            return float.IsNaN(value) || value <= 0f ? fallback : value;
        }

        private static void PrepareContainerForWorldSpace(VisualElement container) {
            if(container == null) return;
            container.style.position = Position.Absolute;
            container.style.left = 0f;
            container.style.top = 0f;
            container.style.right = 0f;
            container.style.bottom = 0f;
            container.style.width = Length.Percent(100f);
            container.style.height = Length.Percent(100f);
            container.style.overflow = Overflow.Visible;
            container.style.translate = new Translate(0f, 0f);
        }

        private static void PrepareSlotForWorldSpace(VisualElement slot) {
            if(slot == null) return;
            slot.style.position = Position.Absolute;
            slot.style.left = StyleKeyword.Null;
            slot.style.top = StyleKeyword.Null;
            slot.style.bottom = StyleKeyword.Null;
            slot.style.right = StyleKeyword.Null;
            slot.style.translate = new Translate(0f, 0f);
        }

        private Camera ResolveWorldCamera() {
            if(_mainCamera != null) return _mainCamera;

            var cameras = Camera.allCameras;
            Camera best = null;
            foreach(var cam in cameras) {
                if(cam == null || cam.enabled == false || cam.gameObject.activeInHierarchy == false) continue;
                if(best == null || cam.depth > best.depth) {
                    best = cam;
                }
            }

            return best;
        }

        private void ResetPostMatchUiState() {
            EnsureUiReferencesBound();
            StopPodiumWorldTracking();
            IsPodiumBlackoutActive = false;
            IsPostMatchMovementLockedLocal = false;
            if(_localReturnToMenuRoutine != null) {
                StopCoroutine(_localReturnToMenuRoutine);
                _localReturnToMenuRoutine = null;
            }

            if(_podiumContainer != null) {
                _podiumContainer.style.display = DisplayStyle.None;
                _podiumContainer.style.translate = new Translate(0f, 0f);
            }

            SetPodiumSlot(_podiumFirstSlot, _podiumFirstName, _podiumFirstKills, string.Empty, 0);
            SetPodiumSlot(_podiumSecondSlot, _podiumSecondName, _podiumSecondKills, string.Empty, 0);
            SetPodiumSlot(_podiumThirdSlot, _podiumThirdName, _podiumThirdKills, string.Empty, 0);

            EventBus.Publish(new HidePostMatchXpEvent());
        }

        /// <summary>Starts the countdown after which the local client returns to menu.</summary>
        private void StartReturnToMenuCountdown() {
            if(_localReturnToMenuRoutine != null) {
                StopCoroutine(_localReturnToMenuRoutine);
            }

            _localReturnToMenuRoutine = StartCoroutine(LocalReturnToMenuCoroutine());
        }

        private IEnumerator LocalReturnToMenuCoroutine() {
            yield return new WaitForSecondsRealtime(podiumDuration);
            _localReturnToMenuRoutine = null;

            if(SessionManager.Instance != null) {
                SessionManager.Instance.LeaveToMainMenuAsync().Forget();
            }
        }

        private void OnPostMatchBlackoutReady(PostMatchBlackoutReadyEvent _) {
            IsPodiumBlackoutActive = true;
            IsPostMatchMovementLockedLocal = true;

            EventBus.Publish(new HideHUDEvent());
            HideInGameHudForPostMatch();

            foreach(var state in ResolveActivePlayerStates()) {
                if(state == null) continue;
                EventBus.Publish(new PostMatchGameplayCameraStateRequestedEvent(state.representedClientId.Value, false));
            }

            // Lock movement now that fade is fully black (same pattern as momentum zero in SetupTopThreeOnServer)
            if(NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) return;
            EventBus.Publish(new PostMatchControlLockRequestedEvent(NetworkManager.Singleton.LocalClientId, true, false,
                false));
        }

        private static IEnumerable<MatchPlayerStateProxy> ResolveActivePlayerStates() {
            return MatchPlayerStateProxy.GetAllStates()
                .Where(state => state != null && state.NetworkObject != null && state.NetworkObject.IsSpawned);
        }

        private static void TryDetachPlayerObjectFromParent(ulong ownerClientId) {
            if(NetworkManager.Singleton == null) return;
            var spawnManager = NetworkManager.Singleton.SpawnManager;
            if(spawnManager == null) return;

            foreach(var netObj in spawnManager.SpawnedObjects.Values) {
                if(netObj == null || !netObj.IsSpawned || netObj.OwnerClientId != ownerClientId) continue;
                netObj.TrySetParent((Transform)null, false);
                return;
            }
        }
    }
}
