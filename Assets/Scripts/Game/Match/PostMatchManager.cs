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
using Network;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

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
        private VisualElement _hudPanel;
        private VisualElement _matchTimerContainer;

        public bool PostMatchFlowStarted { get; private set; }

        private void Awake() {
            var pmm = FindFirstObjectByType<PostMatchManager>();
            if(pmm != null && pmm != this) {
                Destroy(this);
            }

            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if(podiumCamera != null) {
                podiumCamera.gameObject.SetActive(false);
            }

            // Initialize UI if available
            if(uiDocument == null) return;
            _root = uiDocument.rootVisualElement;
            InitializeUI();
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
            _hudPanel = _root.Q<VisualElement>("hud-panel");
            _matchTimerContainer = _root.Q<VisualElement>("match-timer-container");
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            if(Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Called from MatchTimerManager on the server when the timer hits 0.
        /// </summary>
        public void BeginPostMatchFromTimer() {
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

            PostMatchFlowStarted = true;
            StartCoroutine(PostMatchSequence());
        }

        private IEnumerator PostMatchSequence() {
            // 1) Tell all clients to fade to black + hide HUD bits
            RequestFadeToPodiumClientRpc();

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
            var allPlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None)
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

                // If they died before timer ended, bring them back just for podium
                if(player.IsDead || player.netHealth.Value <= 0f) {
                    player.ForceRespawnForPodiumServer();
                }
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

                foreach(var p in allPlayers) {
                    p.ResetVelocityRpc();
                }
            }

            // Hide non-top3 player models (world models only, not cameras)
            foreach(var p in allPlayers) {
                var isOnPodium = topThree.Contains(p);
                p.SetWorldModelVisibleRpc(isOnPodium); // you'll add this helper
            }

            var firstName = topThree.Count > 0 ? topThree[0].playerName.Value.ToString() : string.Empty;
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
            var thirdScore = 0;
            if(topThree.Count > 2) {
                if(isTagMode) {
                    var tagController = topThree[2].GetComponent<PlayerTagController>();
                    thirdScore = tagController != null ? tagController.timeTagged.Value : 0;
                } else {
                    thirdScore = topThree[2].Kills.Value;
                }
            }

            UpdatePodiumUiClientRpc(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
        }

        // --- CLIENT RPCs ---

        [Rpc(SendTo.Everyone)]
        private void RequestFadeToPodiumClientRpc() {
            try {
                // Fade to black using respawn fade overlay (appears above HUD but below pause menu)
                if(SceneTransitionManager.Instance != null) {
                    // Only fade out, we'll manually fade back in later
                    SceneTransitionManager.Instance.StartCoroutine(
                        SceneTransitionManager.Instance.FadeOutRespawnOverlay()
                    );
                }

                // Enter post-match HUD mode (hide crosshair, timer, etc.)
                if(GameMenuManager.Instance != null) {
                    GameMenuManager.Instance.IsPostMatch = true;
                }

                // Find local controller and disable sniper overlay
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
                    HUDManager.Instance.HideHUD();
                }
                HideInGameHudForPostMatch();
                DisableHopballTargets();
            } catch(Exception e) {
                Debug.LogException(e);
            }
        }

        [Rpc(SendTo.Everyone)]
        private void RequestFadeInFromPodiumClientRpc() {
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
            var controllers = GameObject.FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var pc in controllers) {
                pc.SetGameplayCameraActive(false); // you'll add this helper too
            }

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
        }

        private void SetPodiumSlots(
            string firstName, int firstKills,
            string secondName, int secondKills,
            string thirdName, int thirdKills) {
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
            // If for any reason the whole HUD panel == null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Hide individual HUD elements
            if(KillFeedManager.Instance != null)
                KillFeedManager.Instance.HideKillFeed();
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.None;
            // Hide score display (handled by ScoreboardManager)
            if(ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.HideScoreDisplay();
            }

            // Hide grapple UI via GrappleUIManager
            if(GrappleUIManager.Instance != null) {
                GrappleUIManager.Instance.HideGrappleUI();
            }
        }

        public void ShowInGameHudAfterPostMatch() {
            // If for any reason the whole HUD panel == null, bail gracefully.
            if(_hudPanel == null)
                return;

            // Show individual HUD elements
            if(KillFeedManager.Instance != null)
                KillFeedManager.Instance.ShowKillFeed();
            if(_matchTimerContainer != null)
                _matchTimerContainer.style.display = DisplayStyle.Flex;
            // Show score display (handled by ScoreboardManager)
            if(ScoreboardManager.Instance != null) {
                ScoreboardManager.Instance.ShowScoreDisplay();
            }

            // Show grapple UI via GrappleUIManager
            if(GrappleUIManager.Instance != null) {
                GrappleUIManager.Instance.ShowGrappleUI();
            }

            if(_podiumContainer != null) {
                _podiumContainer.style.display = DisplayStyle.None;
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
                if(controller != null && controller.PlayerController != null) {
                    var target = controller.PlayerController.PlayerTarget;
                    if(target != null) target.enabled = false;
                }
            }
        }
    }
}