using System;
using System.Collections;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

public class MatchTimerManager : NetworkBehaviour {
    [Header("Match Settings")]
    [SerializeField]
    private int matchDurationSeconds = 600; // 10 minutes by default

    private readonly NetworkVariable<int> _timeRemainingSeconds =
        new NetworkVariable<int>(
            value: 0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

    public int TimeRemainingSeconds => _timeRemainingSeconds.Value;

    private Coroutine _timerRoutine;
    private bool _hasTriggeredPostMatch = false;

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
        }

        // Push current value to UI immediately when a client joins
        OnTimeRemainingChanged(0, _timeRemainingSeconds.Value);
    }

    private void OnDestroy() {
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
}