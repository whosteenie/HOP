using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Player;
using Network;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class PostMatchManager : NetworkBehaviour {
    public static PostMatchManager Instance { get; private set; }

    [Header("Podium Setup")] [SerializeField]
    private CinemachineCamera podiumCamera;

    [SerializeField] private Transform firstPlaceAnchor;
    [SerializeField] private Transform secondPlaceAnchor;
    [SerializeField] private Transform thirdPlaceAnchor;

    [Header("Timing")] [Tooltip("How long to stay on podium view before returning to menu.")] [SerializeField]
    private float podiumDuration = 10f;

    // Keep this roughly in sync with SceneTransitionManager.fadeDuration
    [SerializeField] private float fadeDuration = 0.5f;
    [SerializeField] private float fadeBuffer = 0.1f;

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

        if(PostMatchFlowStarted) {
            Debug.LogWarning("[MatchTimerManager] Post match is already started!");
            return;
        }

        Debug.Log("[PostMatchManager] Starting post-match sequence from timer.");
        PostMatchFlowStarted = true;
        StartCoroutine(PostMatchSequence());
    }

    private IEnumerator PostMatchSequence() {
        Debug.Log(
            $"[PostMatchManager] PostMatchSequence started on server. IsServer={IsServer}, IsSpawned={IsSpawned}");
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
        var matchSettings = MatchSettings.Instance;
        bool isTagMode = matchSettings != null && matchSettings.selectedGameModeId == "Tag";
        
        // Sort by appropriate stat based on gamemode
        List<PlayerController> sorted;
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

        var topThree = new List<PlayerController>();
        if(sorted.Count > 0) topThree.Add(sorted[0]);
        if(sorted.Count > 1) topThree.Add(sorted[1]);
        if(sorted.Count > 2) topThree.Add(sorted[2]);

        foreach(var player in topThree) {
            if(player == null) continue;

            // If they died before timer ended, bring them back just for podium
            if(player.netIsDead.Value || player.netHealth.Value <= 0f) {
                player.ForceRespawnForPodiumServer();
            }
        }

        // Teleport & face podium
        for(int i = 0; i < topThree.Count; i++) {
            var player = topThree[i];
            Transform anchor = null;

            switch(i) {
                case 0:
                    anchor = firstPlaceAnchor;
                    break;
                case 1:
                    anchor = secondPlaceAnchor;
                    break;
                case 2:
                    anchor = thirdPlaceAnchor;
                    break;
            }

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
            bool isOnPodium = topThree.Contains(p);
            p.SetWorldModelVisibleRpc(isOnPodium); // you'll add this helper
        }

        string firstName = topThree.Count > 0 ? topThree[0].playerName.Value.ToString() : string.Empty;
        int firstScore = topThree.Count > 0 ? (isTagMode ? topThree[0].timeTagged.Value : topThree[0].kills.Value) : 0;

        string secondName = topThree.Count > 1 ? topThree[1].playerName.Value.ToString() : string.Empty;
        int secondScore = topThree.Count > 1 ? (isTagMode ? topThree[1].timeTagged.Value : topThree[1].kills.Value) : 0;

        string thirdName = topThree.Count > 2 ? topThree[2].playerName.Value.ToString() : string.Empty;
        int thirdScore = topThree.Count > 2 ? (isTagMode ? topThree[2].timeTagged.Value : topThree[2].kills.Value) : 0;

        UpdatePodiumUiClientRpc(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
    }

    // --- CLIENT RPCs ---

    [Rpc(SendTo.Everyone)]
    private void RequestFadeToPodiumClientRpc() {
        try {
            // Fade to black
            if(SceneTransitionManager.Instance != null) {
                // Only fade out, we'll manually fade back in later
                SceneTransitionManager.Instance.StartCoroutine(
                    SceneTransitionManager.Instance.FadeOut()
                );
            }

            // Enter post-match HUD mode (hide crosshair, timer, etc.)
            GameMenuManager.Instance?.EnterPostMatch();
        } catch(Exception e) {
            Debug.LogException(e);
        }
    }

    [Rpc(SendTo.Everyone)]
    private void RequestFadeInFromPodiumClientRpc() {
        if(SceneTransitionManager.Instance != null) {
            SceneTransitionManager.Instance.StartCoroutine(
                SceneTransitionManager.Instance.FadeIn()
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

        // Optionally, give it the highest priority if you're using multiple vcams
        // podiumCamera.Priority = 100;
    }

    [Rpc(SendTo.Everyone)]
    private void UpdatePodiumUiClientRpc(
        string firstName, int firstScore,
        string secondName, int secondScore,
        string thirdName, int thirdScore
    ) {
        var menu = GameMenuManager.Instance;
        if(menu == null)
            return;

        menu.SetPodiumSlots(firstName, firstScore, secondName, secondScore, thirdName, thirdScore);
    }
}