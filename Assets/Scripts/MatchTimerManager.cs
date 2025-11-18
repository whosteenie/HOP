using System.Collections;
using System.Linq;
using Game.Player;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

public class MatchTimerManager : NetworkBehaviour {
    [Header("Match Settings")]
    [SerializeField]
    private int matchDurationSeconds = 600; // 10 minutes by default

    private readonly NetworkVariable<int> _timeRemainingSeconds = new(value: 0);

    public int TimeRemainingSeconds => _timeRemainingSeconds.Value;

    private Coroutine _timerRoutine;
    private bool _hasTriggeredPostMatch;
    private bool _hasDesignatedInitialIt;

    private void Awake() {
        matchDurationSeconds = MatchSettings.Instance.GetMatchDurationSeconds();
    }

    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();

        // Subscribe for UI updates on all clients
        _timeRemainingSeconds.OnValueChanged += OnTimeRemainingChanged;

        if (IsServer) {
            // Initialize & start timer on server
            _timeRemainingSeconds.Value = Mathf.Max(0, matchDurationSeconds);

            // Ensure we don't double-start
            if (_timerRoutine != null)
                StopCoroutine(_timerRoutine);

            _timerRoutine = StartCoroutine(TimerCoroutine());
            
            // Check if we're in Tag mode and designate initial "it" after 5 seconds
            var matchSettings = MatchSettings.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Tag") {
                StartCoroutine(DesignateInitialItAfterDelay());
            }
        }

        // Push current value to UI immediately when a client joins
        OnTimeRemainingChanged(0, _timeRemainingSeconds.Value);
    }

    private new void OnDestroy() {
        _timeRemainingSeconds.OnValueChanged -= OnTimeRemainingChanged;
    }

    private IEnumerator TimerCoroutine() {
        var wait = new WaitForSeconds(1f);

        while (IsServer && _timeRemainingSeconds.Value > 0) {
            yield return wait;
            _timeRemainingSeconds.Value--;
        }
        
        if (IsServer && !_hasTriggeredPostMatch) {
            _hasTriggeredPostMatch = true;
            Debug.Log("[MatchTimerManager] Timer reached zero, triggering post-match flow");
            if (PostMatchManager.Instance == null) {
                Debug.LogWarning("[MatchTimerManager] PostMatchManager.Instance is null on server!");
            } else {
                PostMatchManager.Instance.BeginPostMatchFromTimer();
            }
        }
    }

    private void OnTimeRemainingChanged(int previous, int current) {
        if (GameMenuManager.Instance != null) {
            GameMenuManager.Instance.SetMatchTime(current);
        }
    }

    /// <summary>
    /// Optional: configure match duration from code BEFORE this spawns/starts on server.
    /// </summary>
    public void ConfigureMatchDuration(int seconds) {
        if (!IsServer) return;

        matchDurationSeconds = Mathf.Max(0, seconds);

        // If you want to restart timer mid-match, you could also reset
        // _timeRemainingSeconds here and restart the coroutine.
    }
    
    /// <summary>
    /// Designates a random player as "it" after 5 seconds if no one is tagged yet (Tag mode only).
    /// </summary>
    private IEnumerator DesignateInitialItAfterDelay() {
        yield return new WaitForSeconds(5f);
        
        if(_hasDesignatedInitialIt || !IsServer) yield break;
        
        var matchSettings = MatchSettings.Instance;
        if(matchSettings == null || matchSettings.selectedGameModeId != "Tag") yield break;
        
        // Check if anyone is already tagged
        var allPlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None)
            .Where(p => p != null && p.NetworkObject != null && p.NetworkObject.IsSpawned)
            .ToList();
        
        if(allPlayers.Count == 0) yield break;
        
        // Check if anyone is already tagged
        bool anyoneTagged = allPlayers.Any(p => p.isTagged.Value);
        
        if(!anyoneTagged) {
            // Randomly select a player to be "it"
            var randomPlayer = allPlayers[Random.Range(0, allPlayers.Count)];
            
            // Tag the player
            randomPlayer.isTagged.Value = true;
            randomPlayer.tagged.Value++;
            
            // Play tagged sound for the player who was designated as "it"
            randomPlayer.PlayTaggedSoundClientRpc();
            
            // Broadcast to kill feed with HOP as the tagger (similar to OOB kills)
            randomPlayer.BroadcastTagTransferFromHOPClientRpc(randomPlayer.OwnerClientId);
            
            _hasDesignatedInitialIt = true;
            Debug.Log($"[MatchTimerManager] Designated {randomPlayer.playerName.Value} as initial 'it' after 5 seconds");
        }
    }
}