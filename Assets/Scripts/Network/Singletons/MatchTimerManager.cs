using System.Collections;
using System.Linq;
using Game.Player;
using Unity.Netcode;
using UnityEngine;

namespace Network.Singletons {
    public class MatchTimerManager : NetworkBehaviour {
        public static MatchTimerManager Instance { get; private set; }

        [Header("Match Settings")]
        [SerializeField] private int matchDurationSeconds = 600; // 10 minutes by default

        private readonly NetworkVariable<int> _timeRemainingSeconds = new(value: 0);
        private readonly NetworkVariable<int> _preMatchCountdownSeconds = new(value: 0);
        private readonly NetworkVariable<bool> _isPreMatch = new(value: true);

        public int TimeRemainingSeconds => _timeRemainingSeconds.Value;
        public bool IsPreMatch => _isPreMatch.Value;

        private Coroutine _timerRoutine;
        private bool _hasTriggeredPostMatch;
        private bool _hasDesignatedInitialIt;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;

            if(MatchSettingsManager.Instance != null) {
                matchDurationSeconds = MatchSettingsManager.Instance.GetMatchDurationSeconds();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Subscribe for UI updates on all clients
            _timeRemainingSeconds.OnValueChanged += OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged += OnPreMatchCountdownChanged;
            _isPreMatch.OnValueChanged += OnPreMatchStateChanged;

            if(IsServer) {
                // Initialize pre-match countdown on server
                var matchSettings = MatchSettingsManager.Instance;
                var preMatchSeconds = matchSettings != null ? matchSettings.GetPreMatchCountdownSeconds() : 5;
                _preMatchCountdownSeconds.Value = Mathf.Max(0, preMatchSeconds);
                _isPreMatch.Value = true;

                // Ensure we don't double-start
                if(_timerRoutine != null)
                    StopCoroutine(_timerRoutine);

                _timerRoutine = StartCoroutine(PreMatchCountdownCoroutine());
            }

            // Push current value to UI immediately when a client joins
            OnPreMatchCountdownChanged(0, _preMatchCountdownSeconds.Value);
        }

        public override void OnNetworkDespawn() {
            base.OnNetworkDespawn();
            if(Instance == this)
                Instance = null;
        }

        private new void OnDestroy() {
            _timeRemainingSeconds.OnValueChanged -= OnTimeRemainingChanged;
            _preMatchCountdownSeconds.OnValueChanged -= OnPreMatchCountdownChanged;
            _isPreMatch.OnValueChanged -= OnPreMatchStateChanged;

            if(Instance == this)
                Instance = null;
        }

        /// <summary>
        /// Pre-match countdown coroutine. Counts down from configured seconds, then starts the match timer.
        /// </summary>
        private IEnumerator PreMatchCountdownCoroutine() {
            var wait = new WaitForSeconds(1f);

            // Pre-match countdown
            while(IsServer && _preMatchCountdownSeconds.Value > 0) {
                yield return wait;
                _preMatchCountdownSeconds.Value--;
            }

            // Pre-match countdown finished - start the actual match
            if(!IsServer) yield break;
            
            _isPreMatch.Value = false;
            var matchSettings = MatchSettingsManager.Instance;
            matchDurationSeconds = matchSettings?.GetMatchDurationSeconds() ?? 600;
            _timeRemainingSeconds.Value = Mathf.Max(0, matchDurationSeconds);

            // Check if we're in Tag mode and designate initial "it" after 5 seconds
            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag") {
                StartCoroutine(DesignateInitialItAfterDelay());
            }

            // Check if we're in Hopball mode and spawn hopball after 5 seconds
            if(matchSettings != null && matchSettings.selectedGameModeId == "Hopball") {
                // HopballSpawnManager will handle spawning
            }

            // Start the actual match timer
            _timerRoutine = StartCoroutine(TimerCoroutine());
        }

        private IEnumerator TimerCoroutine() {
            var wait = new WaitForSeconds(1f);

            while(IsServer && !_isPreMatch.Value && _timeRemainingSeconds.Value > 0) {
                yield return wait;
                _timeRemainingSeconds.Value--;
            }

            // Only trigger post-match if we're not in pre-match (safety check)
            if(!IsServer || _isPreMatch.Value || _hasTriggeredPostMatch) yield break;
            _hasTriggeredPostMatch = true;
            if(PostMatchManager.Instance == null) {
                Debug.LogWarning("[MatchTimerManager] PostMatchManager.Instance == null on server!");
            } else {
                PostMatchManager.Instance.BeginPostMatchFromTimer();
            }
        }

        private void OnTimeRemainingChanged(int previous, int current) {
            // Only update UI if we're not in pre-match
            if(!_isPreMatch.Value && GameMenuManager.Instance != null) {
                // SetMatchTime will handle tick sound playback based on displayed time
                ScoreboardManager.Instance?.SetMatchTime(current);
            }
        }

        private void OnPreMatchCountdownChanged(int previous, int current) {
            // Display pre-match countdown in UI
            if(_isPreMatch.Value && GameMenuManager.Instance != null) {
                // SetMatchTime will handle tick sound playback based on displayed time
                ScoreboardManager.Instance?.SetMatchTime(current);
            }
        }

        private void OnPreMatchStateChanged(bool previous, bool current) {
            // When pre-match ends, ensure UI shows match timer
            if(!current && GameMenuManager.Instance != null) {
                ScoreboardManager.Instance?.SetMatchTime(_timeRemainingSeconds.Value);
            }
        }

        /// <summary>
        /// Optional: configure match duration from code BEFORE this spawns/starts on server.
        /// </summary>
        public void ConfigureMatchDuration(int seconds) {
            if(!IsServer) return;

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

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings == null || matchSettings.selectedGameModeId != "Gun Tag") yield break;

            // Check if anyone is already tagged
            var allPlayers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None)
                .Where(p => p?.NetworkObject != null && p.NetworkObject.IsSpawned)
                .ToList();

            if(allPlayers.Count == 0) yield break;

            // Check if anyone is already tagged
            var anyoneTagged = allPlayers.Any(p => {
                var tagCtrl = p.GetComponent<PlayerTagController>();
                return tagCtrl != null && tagCtrl.isTagged.Value;
            });

            if(anyoneTagged) yield break;
            {
                // Randomly select a player to be "it"
                var randomPlayer = allPlayers[Random.Range(0, allPlayers.Count)];
                var tagCtrl = randomPlayer.GetComponent<PlayerTagController>();

                if(tagCtrl != null) {
                    // Tag the player
                    tagCtrl.isTagged.Value = true;
                    tagCtrl.tagged.Value++;

                    // Play tagged sound for the player who was designated as "it"
                    tagCtrl.PlayTaggedSoundClientRpc();
                }

                // Broadcast to kill feed with HOP as the tagger (similar to OOB kills)
                tagCtrl?.BroadcastTagTransferFromHopClientRpc(randomPlayer.OwnerClientId);

                _hasDesignatedInitialIt = true;
            }
        }
    }
}