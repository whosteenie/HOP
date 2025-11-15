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

    [Header("Podium Setup")]
    [SerializeField] private CinemachineCamera podiumCamera;
    [SerializeField] private Transform firstPlaceAnchor;
    [SerializeField] private Transform secondPlaceAnchor;
    [SerializeField] private Transform thirdPlaceAnchor;

    [Header("Timing")]
    [Tooltip("How long to stay on podium view before returning to menu.")]
    [SerializeField] private float podiumDuration = 10f;

    // Keep this roughly in sync with SceneTransitionManager.fadeDuration
    [SerializeField] private float fadeDuration = 0.5f;
    [SerializeField] private float fadeBuffer = 0.1f;

    public bool PostMatchFlowStarted { get; private set; }

    private void Awake() {
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
        Debug.Log($"[PostMatchManager] PostMatchSequence started on server. IsServer={IsServer}, IsSpawned={IsSpawned}");
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

        // Sort by kills descending, then maybe by damage as tie-breaker later
        var sorted = allPlayers
            .OrderByDescending(p => p.kills.Value)
            .ThenByDescending(p => p.damageDealt.Value)
            .ToList();

        var topThree = new List<PlayerController>();
        if(sorted.Count > 0) topThree.Add(sorted[0]);
        if(sorted.Count > 1) topThree.Add(sorted[1]);
        if(sorted.Count > 2) topThree.Add(sorted[2]);

        // Teleport & face podium
        for(int i = 0; i < topThree.Count; i++) {
            var player = topThree[i];
            Transform anchor = null;

            switch(i) {
                case 0: anchor = firstPlaceAnchor; break;
                case 1: anchor = secondPlaceAnchor; break;
                case 2: anchor = thirdPlaceAnchor; break;
            }

            if(anchor == null || player == null) continue;

            var netObj = player.NetworkObject;
            if(netObj == null || !netObj.IsSpawned) continue;

            // Teleport their transform to podium slot
            netObj.TrySetParent((Transform)null, false); // ensure no odd parents
            player.TeleportToPodiumFromServer(anchor.position, anchor.rotation);
            
            foreach (var p in allPlayers) {
                p.ResetVelocityRpc();
            }


            // Make sure they face the camera exactly
            // if(podiumCamera != null) {
            //     var lookTarget = podiumCamera.transform.position;
            //     var flatPos = player.transform.position;
            //     lookTarget.y = flatPos.y; // keep upright
            //     var lookRot = Quaternion.LookRotation((lookTarget - flatPos).normalized, Vector3.up);
            //     player.transform.rotation = lookRot;
            // }
        }

        // Hide non-top3 player models (world models only, not cameras)
        foreach(var p in allPlayers) {
            bool isOnPodium = topThree.Contains(p);
            p.SetWorldModelVisibleRpc(isOnPodium);   // you'll add this helper
        }
                
        string firstName  = topThree.Count > 0 ? topThree[0].playerName.Value.ToString() : string.Empty;
        int    firstKills = topThree.Count > 0 ? topThree[0].kills.Value : 0;

        string secondName  = topThree.Count > 1 ? topThree[1].playerName.Value.ToString() : string.Empty;
        int    secondKills = topThree.Count > 1 ? topThree[1].kills.Value : 0;

        string thirdName  = topThree.Count > 2 ? topThree[2].playerName.Value.ToString() : string.Empty;
        int    thirdKills = topThree.Count > 2 ? topThree[2].kills.Value : 0;

        UpdatePodiumUiClientRpc(firstName, firstKills, secondName, secondKills, thirdName, thirdKills);
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
    private void RequestFadeToMenuClientRpc() {
        if(SceneTransitionManager.Instance != null) {
            SceneTransitionManager.Instance.StartCoroutine(
                SceneTransitionManager.Instance.FadeOut()
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
        string firstName, int firstKills,
        string secondName, int secondKills,
        string thirdName, int thirdKills
    ) {
        var menu = GameMenuManager.Instance;
        if (menu == null)
            return;

        menu.SetPodiumSlots(firstName, firstKills, secondName, secondKills, thirdName, thirdKills);
    }
}