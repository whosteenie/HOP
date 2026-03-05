using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Player;
using Game.Menu;
using Game.Spawning;
using Game.UI;
using Game.Hopball;
using Game.Player.Hopball;
using Network.Diagnostics;
using Network.Events;
using Network.Singletons;
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
        [SerializeField] private PostMatchXpDisplay xpDisplay;

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

        private PostMatchXpDisplay _xpDisplay;
        private Coroutine _podiumWorldSpaceTrackingRoutine;
        private bool _missingUiReferenceLogged;

        private ulong _firstPlacePlayerId = ulong.MaxValue;
        private ulong _secondPlacePlayerId = ulong.MaxValue;
        private ulong _thirdPlacePlayerId = ulong.MaxValue;

        private const float AssumedCharacterHeight = 1.9f;

        public bool PostMatchFlowStarted { get; private set; }
        private SpawnPoint.Team _winningTeam = SpawnPoint.Team.None;
        private Coroutine _blackoutReadyRoutine;
        private bool _matchEndedEventsBound;
        private bool IsPodiumBlackoutActive { get; set; }
        public static bool IsPodiumBlackoutActiveLocal => Instance != null && Instance.IsPodiumBlackoutActive;

        /// <summary>
        /// True once fade-to-black is fully black. Use this for movement lock so WASD stays active during the fade.
        /// </summary>
        public static bool IsPostMatchMovementLockedLocal { get; private set; }

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
                Debug.LogError("[PostMatchManager] UIDocument is not assigned on PostMatchManager. " +
                               "Assign UIDocument on Game scene object 'PostMatchManager'.");
                return;
            }

            _root = uiDocument.rootVisualElement;

            _xpDisplay = xpDisplay != null ? xpDisplay : GetComponent<PostMatchXpDisplay>();
            if(_xpDisplay == null) {
                Debug.LogError("[PostMatchManager] PostMatchXpDisplay is not assigned. " +
                               "Add PostMatchXpDisplay to Game scene object 'PostMatchManager' and assign UIDocument.");
            } else if(_xpDisplay.uiDocument == null) {
                _xpDisplay.uiDocument = uiDocument;
            }
        }

        private void Start() {
            // UI Toolkit document can be valid in inspector but still not bound to a live root in Start
            // depending on scene/object initialization order. Defer hard binding until first real use.
            TryResolveUiDocumentReference();
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            BindMatchEndedEvent();
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

        private void TryResolveUiDocumentReference() {
            if(GameMenuManager.Instance == null ||
               !GameMenuManager.Instance.TryGetComponent(out UIDocument gameMenuDoc)) return;
            if(uiDocument == null || uiDocument != gameMenuDoc || uiDocument.rootVisualElement == null) {
                uiDocument = gameMenuDoc;
            }
        }

        private bool EnsureUiReferencesBound() {
            TryResolveUiDocumentReference();
            if(uiDocument == null) {
                uiDocument = GetComponent<UIDocument>();
            }

            if(uiDocument == null) {
                if(_missingUiReferenceLogged) return false;
                Debug.LogError("[PostMatchManager] UIDocument is missing; cannot bind post-match UI references.", this);
                _missingUiReferenceLogged = true;
                return false;
            }

            if(_xpDisplay == null) {
                _xpDisplay = xpDisplay != null ? xpDisplay : GetComponent<PostMatchXpDisplay>();
            }
            if(_xpDisplay != null && _xpDisplay.uiDocument == null) {
                _xpDisplay.uiDocument = uiDocument;
            }

            var currentRoot = uiDocument.rootVisualElement;
            if(currentRoot == null) {
                if(_missingUiReferenceLogged) return false;
                Debug.LogError("[PostMatchManager] UIDocument rootVisualElement is null; cannot bind post-match UI references.", this);
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
                Debug.LogError(
                    "[PostMatchManager] Required post-match UI elements are missing from GameMenu.uxml. " +
                    "Expected podium slots/labels and match-timer-container.",
                    this);
                _missingUiReferenceLogged = true;
                return false;
            }

            _missingUiReferenceLogged = false;
            return true;
        }

        public override void OnNetworkDespawn() {
            UnbindMatchEndedEvent();
            ResetPostMatchUiState();
            base.OnNetworkDespawn();
            if(Instance == this)
                Instance = null;
        }

        public override void OnDestroy() {
            base.OnDestroy();
            UnbindMatchEndedEvent();
            if(Instance == this) {
                Instance = null;
            }
        }

        private void BindMatchEndedEvent() {
            if(!IsServer || _matchEndedEventsBound) return;
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
            if(!IsServer) {
                Debug.LogWarning("[MatchTimerManager] Is not server!");
                return;
            }

            // Prevent post-match from starting during pre-match countdown
            if(MatchTimerManager.Instance != null && MatchTimerManager.Instance.IsPreMatch) {
                Debug.LogWarning("[PostMatchManager] Cannot start post-match during pre-match countdown!");
                return;
            }

            if(PostMatchFlowStarted) {
                Debug.LogWarning("[MatchTimerManager] Post match is already started!");
                return;
            }

            ResetPostMatchUiState();
            PostMatchFlowStarted = true;
            StartCoroutine(PostMatchSequence());
        }

        /// <summary>
        /// Called from HopballSpawnManager when a team reaches the win score.
        /// </summary>
        public void BeginPostMatchFromScore(SpawnPoint.Team winningTeam) {
            if(!IsServer) {
                Debug.LogWarning("[PostMatchManager] BeginPostMatchFromScore called on non-server!");
                return;
            }

            if(PostMatchFlowStarted) {
                Debug.LogWarning("[PostMatchManager] Post match is already started!");
                return;
            }

            ResetPostMatchUiState();
            _winningTeam = winningTeam;
            PostMatchFlowStarted = true;
            StartCoroutine(PostMatchSequence());
        }

        private IEnumerator PostMatchSequence() {
            // 1) Tell all clients to fade to black + hide HUD bits
            RequestFadeToPodiumClientRpc();

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
            RequestFadeInFromPodiumClientRpc();

            // 4) Stay on podium for a bit
            yield return new WaitForSeconds(podiumDuration);

            // 5) Fade back to black and return to main menu
            // RequestFadeToMenuClientRpc();

            // yield return new WaitForSeconds(fadeDuration + fadeBuffer);

            // Back to main menu (same flow as QuitToMenu)
            if(SessionManager.Instance != null) {
                SessionManager.Instance.LeaveToMainMenuAsync().Forget();
            }
        }

        /// <summary>
        /// Server-side: pick top 3 players, teleport them to podium anchors,
        /// and face them toward the podium camera. Also hide non-top3 visuals.
        /// </summary>
        private void SetupTopThreeOnServer() {
            var allPlayers = PlayerController.SpawnedPlayers
                .Where(p => p != null && p.NetworkObject != null && p.NetworkObject.IsSpawned)
                .ToList();

            if(allPlayers.Count == 0) return;

            // Check if we're in Tag mode
            var matchSettings = MatchSettingsManager.Instance;
            var isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag";

            // Sort by appropriate stat based on gamemode
            List<PlayerController> sorted;
            if(isTagMode) {
                // Tag mode: sort by time tagged (lowest first), then by tags as tie-breaker
                sorted = allPlayers
                    .OrderBy(p => {
                        var tagCtrl = p.GetComponent<PlayerTagController>();
                        return tagCtrl != null ? tagCtrl.timeTagged.Value : int.MaxValue;
                    })
                    .ThenByDescending(p => {
                        var tagCtrl = p.GetComponent<PlayerTagController>();
                        return tagCtrl != null ? tagCtrl.tags.Value : 0;
                    })
                    .ToList();
            } else {
                // Normal mode: sort by kills descending, then by damage as tie-breaker
                sorted = allPlayers
                    .OrderByDescending(p => p.Kills.Value)
                    .ThenByDescending(p => p.damageDealt.Value)
                    .ToList();
            }

            var topThree = new List<PlayerController>();
            if(sorted.Count > 0) topThree.Add(sorted[0]);
            if(sorted.Count > 1) topThree.Add(sorted[1]);
            if(sorted.Count > 2) topThree.Add(sorted[2]);

            foreach(var player in topThree) {
                if(player == null) continue;
                // Always normalize podium player visuals. This also fixes hopball-holder weapon visibility.
                player.ForceRespawnForPodiumServer();
            }

            // Zero out momentum only after blackout is fully active.
            foreach(var p in allPlayers) {
                if(p == null) continue;
                p.ResetVelocityRpc();
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

                var netObj = player.NetworkObject;
                if(netObj == null || !netObj.IsSpawned) continue;

                // Teleport their transform to podium slot
                netObj.TrySetParent((Transform)null, false); // ensure no odd parents
                player.TeleportToPodiumFromServer(anchor.position, anchor.rotation);
                player.SnapPodiumVisualsClientRpc();
            }

            // Hide non-top3 player models (world models only, not cameras)
            foreach(var p in allPlayers) {
                var isOnPodium = topThree.Contains(p);
                p.SetWorldModelVisibleRpc(isOnPodium); // you'll add this helper
            }

            var firstName = topThree.Count > 0 ? topThree[0].playerName.Value.ToString() : string.Empty;
            var firstId = topThree.Count > 0 ? topThree[0].OwnerClientId : ulong.MaxValue;
            var firstScore = 0;
            if(topThree.Count > 0) {
                if(isTagMode) {
                    var tagController = topThree[0].GetComponent<PlayerTagController>();
                    firstScore = tagController != null ? tagController.timeTagged.Value : 0;
                } else {
                    firstScore = topThree[0].Kills.Value;
                }
            }

            var secondName = topThree.Count > 1 ? topThree[1].playerName.Value.ToString() : string.Empty;
            var secondId = topThree.Count > 1 ? topThree[1].OwnerClientId : ulong.MaxValue;
            var secondScore = 0;
            if(topThree.Count > 1) {
                if(isTagMode) {
                    var tagController = topThree[1].GetComponent<PlayerTagController>();
                    secondScore = tagController != null ? tagController.timeTagged.Value : 0;
                } else {
                    secondScore = topThree[1].Kills.Value;
                }
            }

            var thirdName = topThree.Count > 2 ? topThree[2].playerName.Value.ToString() : string.Empty;
            var thirdId = topThree.Count > 2 ? topThree[2].OwnerClientId : ulong.MaxValue;
            var thirdScore = 0;
            if(topThree.Count > 2) {
                if(isTagMode) {
                    var tagController = topThree[2].GetComponent<PlayerTagController>();
                    thirdScore = tagController != null ? tagController.timeTagged.Value : 0;
                } else {
                    thirdScore = topThree[2].Kills.Value;
                }
            }

            UpdatePodiumUiClientRpc(firstName, firstScore, firstId, secondName, secondScore, secondId, thirdName,
                thirdScore, thirdId);
        }

        // --- CLIENT RPCs ---
        [Rpc(SendTo.Everyone)]
        private void RequestFadeToPodiumClientRpc() {
            try {
                EnsureUiReferencesBound();
                ResetPostMatchUiState();
                IsPodiumBlackoutActive = false;
                IsPostMatchMovementLockedLocal = false;
                if(_blackoutReadyRoutine != null) {
                    StopCoroutine(_blackoutReadyRoutine);
                    _blackoutReadyRoutine = null;
                }

                // Fade to black using respawn fade overlay (appears above HUD but below pause menu)
                if(SceneTransitionManager.Instance != null) {
                    // Only fade out, we'll manually fade back in later
                    SceneTransitionManager.Instance.StartCoroutine(
                        SceneTransitionManager.Instance.FadeOutRespawnOverlay()
                    );
                }
                _blackoutReadyRoutine = StartCoroutine(MarkPodiumBlackoutReadyAfterFade());

                // Enter post-match HUD mode (hide crosshair, timer, etc.)
                if(GameMenuManager.Instance != null) {
                    GameMenuManager.Instance.IsPostMatch = true;
                }

                // Find local controller and disable sniper overlay (do NOT lock movement yet - wait for fade to complete)
                if(NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null) {
                    var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
                    if(localPlayer != null) {
                        var localController = localPlayer.GetComponent<PlayerController>();
                        if(localController != null && localController.PlayerInput != null) {
                            localController.PlayerInput.ForceDisableSniperOverlay(false);
                        }
                    }
                }

                if(HUDManager.Instance != null) {
                    EventBus.Publish(new HideHUDEvent());
                }
                HideInGameHudForPostMatch();
                DisableHopballTargets();
            } catch(Exception e) {
                DebugHelpers.PublishCriticalError($"PostMatchManager.RequestFadeToPodiumClientRpc failed: {e.Message}", 
                    "PostMatchManager.RequestFadeToPodiumClientRpc", e);
                Debug.LogException(e);
            }
        }
        [Rpc(SendTo.Everyone)]
        private void AnnounceMatchResultClientRpc(SpawnPoint.Team winningTeam) {
            if (Progression.ProgressionManager.Instance == null) return;

            // Match Completion XP
            Progression.ProgressionManager.Instance.AddXp(100);

            // Win Bonus
            // We need to check our local team.
            // Assumption: PlayerTeamManager sets the local player's team in a way we can access, or we check NetworkManager.LocalClient
            var localTeam = SpawnPoint.Team.None;
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null && 
                NetworkManager.Singleton.LocalClient.PlayerObject != null) {
                var teamManager = NetworkManager.Singleton.LocalClient.PlayerObject.GetComponent<PlayerTeamManager>();
                if (teamManager != null) {
                    localTeam = teamManager.netTeam.Value;
                }
            }

            var matchSettings = MatchSettingsManager.Instance;
            var isTrackedGamemode = matchSettings != null && 
                                    matchSettings.selectedGameModeId is "Team Deathmatch" or "Hopball" or "KOTH";

            if (isTrackedGamemode && localTeam != SpawnPoint.Team.None) {
                if (localTeam == winningTeam) {
                     Progression.ProgressionManager.Instance.AddXp(500); // Win Bonus
                     Progression.ProgressionManager.Instance.RecordWin();
                } else if (winningTeam != SpawnPoint.Team.None) {
                     // Only record loss if there was a winner (not a draw) and we didn't win
                     Progression.ProgressionManager.Instance.RecordLoss();
                }
                
                // Track "Matches Played" for team modes, but NOT placement for now.
                Progression.ProgressionManager.Instance.RecordMatchComplete(matchSettings.selectedGameModeId, 0); // 0 = no placement context

            } else {
                // FFA Mode (Gun Tag, Deathmatch)
                if (ScoreboardManager.Instance != null && 
                    ScoreboardManager.Instance.GetLocalPlayerPlacement(out var rank, out _)) {
                    
                    // Award Win if Rank 1?
                    if (rank == 1) {
                        Progression.ProgressionManager.Instance.AddXp(500);
                        Progression.ProgressionManager.Instance.RecordWin();
                    } else {
                        Progression.ProgressionManager.Instance.RecordLoss();
                    }
                    
                    if (matchSettings != null) {
                        Progression.ProgressionManager.Instance.RecordMatchComplete(matchSettings.selectedGameModeId, rank);
                    }
                }
            }

            // Record Average Speed for this match

            // Record Average Speed for this match
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.LocalClient != null && 
                NetworkManager.Singleton.LocalClient.PlayerObject != null) {
                var statsCtrl = NetworkManager.Singleton.LocalClient.PlayerObject.GetComponent<PlayerStatsController>();
                if (statsCtrl != null) {
                    Progression.ProgressionManager.Instance.RecordMatchAverageSpeed(statsCtrl.averageVelocity.Value);
                }
            }

            Progression.ProgressionManager.Instance.EndMatch();
        }
        [Rpc(SendTo.Everyone)]
        private void RequestFadeInFromPodiumClientRpc() {
            IsPodiumBlackoutActive = false;
            if(_blackoutReadyRoutine != null) {
                StopCoroutine(_blackoutReadyRoutine);
                _blackoutReadyRoutine = null;
            }

            if(SceneTransitionManager.Instance != null) {
                SceneTransitionManager.Instance.StartCoroutine(
                    SceneTransitionManager.Instance.FadeInRespawnOverlay()
                );
            }
        }
        [Rpc(SendTo.Everyone)]
        private void ActivatePodiumCameraClientRpc() {
            if(podiumCamera == null) return;

            // Disable player-specific cameras (owner-local rigs, etc.)
            var controllers = PlayerController.SpawnedPlayers;
            foreach(var pc in controllers) {
                if(pc == null) continue;
                pc.SetGameplayCameraActive(false); // you'll add this helper too
                pc.SetPostMatchControlLock(true, lockLook: false, resetVelocity: false);
            }

            // Enable podium camera
            podiumCamera.gameObject.SetActive(true);

            // Optionally, give it the highest priority if you're using multiple cams
            // podiumCamera.Priority = 100;
        }
        [Rpc(SendTo.Everyone)]
        private void UpdatePodiumUiClientRpc(
            string firstName, int firstScore,
            ulong firstPlayerId,
            string secondName, int secondScore,
            ulong secondPlayerId,
            string thirdName, int thirdScore,
            ulong thirdPlayerId
        ) {
            _firstPlacePlayerId = firstPlayerId;
            _secondPlacePlayerId = secondPlayerId;
            _thirdPlacePlayerId = thirdPlayerId;
            SetPodiumSlots(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
            StartPodiumWorldSpaceTracking();
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

            // Show XP Bar for local player
            if(_xpDisplay == null || Progression.ProgressionManager.Instance == null) return;
            var pm = Progression.ProgressionManager.Instance;
            var nextLevelXp = pm.GetXpRequiredForLevel(pm.StartMatchLevel); // Max XP for the START level
                
            // Note: If we leveled up multiple times, the animation might be a bit weird with just start/end,
            // but PostMatchXPDisplay handles basic level up logic.
                
            _xpDisplay.ShowXp(
                pm.StartMatchLevel,
                pm.StartMatchCurrentXp,
                pm.Data.level,
                pm.Data.currentXp,
                pm.CurrentMatchXp,
                nextLevelXp
            );
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

        /// <summary>
        /// Hides only the in-game HUD elements, but leaves pause/scoreboard usable.
        /// </summary>
        private void HideInGameHudForPostMatch() {
            EnsureUiReferencesBound();

            // Hide individual HUD elements
            if(KillFeedManager.Instance != null)
                EventBus.Publish(new HideKillFeedEvent());
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.None;
            EventBus.Publish(new HideScoreDisplayEvent());

            EventBus.Publish(new HideGrappleUIEvent());
        }

        public void ShowInGameHudAfterPostMatch() {
            EnsureUiReferencesBound();
            ResetPostMatchUiState();
            var controllers = PlayerController.SpawnedPlayers;
            foreach(var controller in controllers) {
                if(controller == null) continue;
                controller.SetPostMatchControlLock(false);
            }

            // Show individual HUD elements
            if(KillFeedManager.Instance != null)
                EventBus.Publish(new ShowKillFeedEvent());
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            EventBus.Publish(new ShowScoreDisplayEvent());

            EventBus.Publish(new ShowGrappleUIEvent());
        }

        private void StartPodiumWorldSpaceTracking() {
            if(!EnsureUiReferencesBound()) return;
            StopPodiumWorldSpaceTracking();
            if(_podiumContainer == null) return;

            PrepareContainerForWorldSpacePositioning(_podiumContainer);
            PrepareSlotForWorldSpacePositioning(_podiumFirstSlot);
            PrepareSlotForWorldSpacePositioning(_podiumSecondSlot);
            PrepareSlotForWorldSpacePositioning(_podiumThirdSlot);

            _podiumWorldSpaceTrackingRoutine = StartCoroutine(PodiumWorldSpaceTrackingCoroutine());
        }

        private void StopPodiumWorldSpaceTracking() {
            if(_podiumWorldSpaceTrackingRoutine == null) return;
            StopCoroutine(_podiumWorldSpaceTrackingRoutine);
            _podiumWorldSpaceTrackingRoutine = null;
        }

        private IEnumerator PodiumWorldSpaceTrackingCoroutine() {
            while(_podiumContainer != null && _podiumContainer.resolvedStyle.display != DisplayStyle.None) {
                UpdatePodiumWorldSpacePositions();
                yield return null;
            }

            _podiumWorldSpaceTrackingRoutine = null;
        }

        private void UpdatePodiumWorldSpacePositions() {
            if(_root?.panel == null) return;

            var worldCamera = ResolveWorldCamera();
            if(worldCamera == null) return;

            UpdatePodiumSlotWorldPosition(_podiumFirstSlot, _firstPlacePlayerId, firstPlaceAnchor, worldCamera);
            UpdatePodiumSlotWorldPosition(_podiumSecondSlot, _secondPlacePlayerId, secondPlaceAnchor, worldCamera);
            UpdatePodiumSlotWorldPosition(_podiumThirdSlot, _thirdPlacePlayerId, thirdPlaceAnchor, worldCamera);
        }

        private void UpdatePodiumSlotWorldPosition(VisualElement slot, ulong playerId, Transform slotAnchor,
            Camera worldCamera) {
            if(slot == null) return;

            Vector3 targetWorldPosition;
            if(slotAnchor != null) {
                targetWorldPosition = slotAnchor.position + Vector3.up * podiumCardAnchorYOffset;
            } else if(TryGetPodiumPlayer(playerId, out var player) && player != null) {
                targetWorldPosition = GetPlayerFeetWorldPosition(player);
            } else {
                slot.style.display = DisplayStyle.None;
                return;
            }

            var feetScreen = worldCamera.WorldToScreenPoint(targetWorldPosition);
            if(feetScreen.z <= 0f) {
                slot.style.display = DisplayStyle.None;
                return;
            }

            var panelFeet = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, targetWorldPosition, worldCamera);
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

        private static void PrepareContainerForWorldSpacePositioning(VisualElement container) {
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

        private static void PrepareSlotForWorldSpacePositioning(VisualElement slot) {
            if(slot == null) return;
            slot.style.position = Position.Absolute;
            slot.style.left = StyleKeyword.Null;
            slot.style.top = StyleKeyword.Null;
            slot.style.bottom = StyleKeyword.Null;
            slot.style.right = StyleKeyword.Null;
            slot.style.translate = new Translate(0f, 0f);
        }

        private static Camera ResolveWorldCamera() {
            if(Camera.main != null) return Camera.main;

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

        private static Vector3 GetPlayerFeetWorldPosition(PlayerController player) {
            if(player == null) return Vector3.zero;

            var cc = player.CharacterController;
            if(cc == null) return player.transform.position;
            var bounds = cc.bounds;
            return bounds.size.y > 0.001f ? new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) : player.transform.position;
        }

        private static bool TryGetPodiumPlayer(ulong playerId, out PlayerController player) {
            player = null;
            if(playerId == ulong.MaxValue) return false;
            if(NetworkManager.Singleton == null) return false;
            var spawnManager = NetworkManager.Singleton.SpawnManager;
            if(spawnManager == null) return false;

            // Works for host and clients; ConnectedClients is not reliable on all peers.
            foreach(var kvp in spawnManager.SpawnedObjects) {
                var netObj = kvp.Value;
                if(netObj == null || !netObj.IsSpawned) continue;
                if(netObj.OwnerClientId != playerId) continue;
                player = netObj.GetComponent<PlayerController>();
                if(player != null) return true;
            }

            return false;
        }

        private void ResetPostMatchUiState() {
            EnsureUiReferencesBound();
            StopPodiumWorldSpaceTracking();
            IsPodiumBlackoutActive = false;
            IsPostMatchMovementLockedLocal = false;
            if(_blackoutReadyRoutine != null) {
                StopCoroutine(_blackoutReadyRoutine);
                _blackoutReadyRoutine = null;
            }

            _firstPlacePlayerId = ulong.MaxValue;
            _secondPlacePlayerId = ulong.MaxValue;
            _thirdPlacePlayerId = ulong.MaxValue;

            if(_podiumContainer != null) {
                _podiumContainer.style.display = DisplayStyle.None;
                _podiumContainer.style.translate = new Translate(0f, 0f);
            }

            SetPodiumSlot(_podiumFirstSlot, _podiumFirstName, _podiumFirstKills, string.Empty, 0);
            SetPodiumSlot(_podiumSecondSlot, _podiumSecondName, _podiumSecondKills, string.Empty, 0);
            SetPodiumSlot(_podiumThirdSlot, _podiumThirdName, _podiumThirdKills, string.Empty, 0);

            if(_xpDisplay != null) {
                _xpDisplay.Hide();
            }
        }
        private static void DisableHopballTargets() {
            // Disable Hopball target
            if(HopballController.Instance != null) {
                var targets = HopballController.Instance.GetComponentsInChildren<OSI.Target>(true);
                foreach(var t in targets) {
                    if(t != null) t.enabled = false;
                }
            }

            // Disable all player targets (whoever is holding it)
            foreach(var controller in PlayerHopballController.Instances) {
                if(controller == null || controller.PlayerController == null) continue;
                var target = controller.PlayerController.PlayerTarget;
                if(target != null) target.enabled = false;
            }
        }

        private IEnumerator MarkPodiumBlackoutReadyAfterFade() {
            yield return new WaitForSeconds(fadeDuration + fadeBuffer);
            IsPodiumBlackoutActive = true;
            IsPostMatchMovementLockedLocal = true;
            _blackoutReadyRoutine = null;

            // Lock movement now that fade is fully black (same pattern as momentum zero in SetupTopThreeOnServer)
            if(NetworkManager.Singleton == null || NetworkManager.Singleton.LocalClient == null) yield break;
            var localPlayer = NetworkManager.Singleton.LocalClient.PlayerObject;
            
            if(localPlayer == null) yield break;
            var localController = localPlayer.GetComponent<PlayerController>();
            
            if(localController == null) yield break;
            if(localController.WeaponManager != null) {
                localController.WeaponManager.PrepareCurrentWeaponForPostMatchPodium();
            }

            localController.SetPostMatchControlLock(true, lockLook: false, resetVelocity: false);
        }
    }
}
