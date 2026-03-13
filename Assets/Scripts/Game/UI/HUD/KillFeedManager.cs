using System.Collections;
using System.Collections.Generic;
using Game.Match;
using Game.Player.Core;
using Network.Events;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.HUD {
    /// <summary>
    /// Manages the kill feed UI, including kill entries and tag transfer entries.
    /// </summary>
    public class KillFeedManager : MonoBehaviour {
        public static KillFeedManager Instance { get; private set; }

        [Header("Kill Feed Settings")]
        [SerializeField] private Sprite killIconSprite;
        [SerializeField] private Sprite taggedIconSprite; // Icon for tag transfers in Tag mode
        [SerializeField] private float killFeedDisplayTime = 5f;
        [SerializeField] private int maxKillFeedEntries = 5;

        [Header("UI Templates")]
        [SerializeField] private VisualTreeAsset killFeedEntryTemplate;

        private VisualElement _killFeedContainer;
        private readonly List<VisualElement> _activeKillEntries = new();
        private readonly Dictionary<VisualElement, Coroutine> _fadeCoroutines = new();
        private bool _killFeedTemplateErrorLogged;

        // Cached references for performance
        private MatchSettingsManager _cachedMatchSettings;
        private bool _cachedIsTeamBased;
        private bool _cachedIsTagMode;
        private bool _gameModeCacheValid;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable() {
            // Subscribe to UI events
            EventBus.Subscribe<AddKillFeedEntryEvent>(OnAddKillFeedEntry);
            EventBus.Subscribe<ShowKillFeedEvent>(OnShowKillFeed);
            EventBus.Subscribe<HideKillFeedEvent>(OnHideKillFeed);
        }

        private void OnDisable() {
            // Unsubscribe from UI events
            EventBus.Unsubscribe<AddKillFeedEntryEvent>(OnAddKillFeedEntry);
            EventBus.Unsubscribe<ShowKillFeedEvent>(OnShowKillFeed);
            EventBus.Unsubscribe<HideKillFeedEvent>(OnHideKillFeed);
        }

        #region Event Handlers

        private void OnAddKillFeedEntry(AddKillFeedEntryEvent evt) {
            AddEntryToFeed(evt.Killer, evt.Victim, evt.IsLocalKiller, evt.KillerId, evt.VictimId, evt.WasKill);
        }

        private void OnShowKillFeed(ShowKillFeedEvent evt) {
            ShowKillFeed();
        }

        private void OnHideKillFeed(HideKillFeedEvent evt) {
            HideKillFeed();
        }

        #endregion

        /// <summary>
        /// Initializes the kill feed manager with the kill feed container from the UI.
        /// </summary>
        public void Initialize(VisualElement killFeedContainer) {
            _killFeedContainer = killFeedContainer;
        }

        /// <summary>
        /// Adds an entry to the kill feed. Automatically uses the appropriate icon based on game mode.
        /// For kills in normal modes, uses kill icon. For tag transfers in Gun Tag mode, uses tag icon.
        /// Event handler - called via EventBus
        /// </summary>
        private void AddEntryToFeed(string actorName, string targetName, bool isLocalActor, ulong actorClientId,
            ulong targetClientId, bool wasKill) {
            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null || !_gameModeCacheValid) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            // Check if we're in Tag mode (cache the result)
            if(!_gameModeCacheValid) {
                _cachedIsTagMode = _cachedMatchSettings != null && _cachedMatchSettings.selectedGameModeId == "Gun Tag";
                _cachedIsTeamBased = _cachedMatchSettings != null &&
                                     MatchSettingsManager.IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
                _gameModeCacheValid = true;
            }

            // Gun Tag uses tag icon only for tag-transfer entries. Real kills (including OOB) always use kill icon.
            var useTagIcon = !wasKill && _cachedIsTagMode && taggedIconSprite != null;
            var icon = useTagIcon ? taggedIconSprite : killIconSprite;

            AddEntryToFeedInternal(actorName, targetName, isLocalActor, actorClientId, targetClientId, icon);
        }

        /// <summary>
        /// Internal method to add an entry to the kill feed. Handles both kills and tag transfers.
        /// </summary>
        private void AddEntryToFeedInternal(string actorName, string targetName, bool isLocalActor, ulong actorClientId,
            ulong targetClientId, Sprite iconSprite) {
            if(_killFeedContainer == null) return;

            // Check if we're at capacity
            if(_activeKillEntries.Count >= maxKillFeedEntries) {
                // Force remove the oldest entry (last in the list)
                var oldestEntry = _activeKillEntries[^1];
                RemoveKillEntry(oldestEntry, immediate: true);
            }

            var entry = CreateFeedEntry(actorName, targetName, isLocalActor, actorClientId, targetClientId, iconSprite);
            if(entry == null) return;

            // Add to top of feed
            _killFeedContainer.Add(entry);
            _activeKillEntries.Add(entry); // Insert at beginning so oldest is at end

            // Start fade-out timer
            var fadeCoroutine = StartCoroutine(FadeOutKillEntry(entry));
            _fadeCoroutines[entry] = fadeCoroutine;
        }

        /// <summary>
        /// Sets the visibility of the kill feed.
        /// </summary>
        /// <param name="visible">True to show, false to hide</param>
        private void SetKillFeedVisible(bool visible) {
            if(_killFeedContainer != null) {
                _killFeedContainer.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>
        /// Hides the kill feed (e.g., during post-match).
        /// Event handler - called via EventBus
        /// </summary>
        private void HideKillFeed() {
            SetKillFeedVisible(false);
        }

        /// <summary>
        /// Shows the kill feed (e.g., after post-match).
        /// Event handler - called via EventBus
        /// </summary>
        private void ShowKillFeed() {
            SetKillFeedVisible(true);
        }

        /// <summary>
        /// Creates a feed entry (kill or tag transfer) with the specified icon.
        /// Uses the required UXML template and fails fast if the template is missing or invalid.
        /// </summary>
        private VisualElement CreateFeedEntry(string actorName, string targetName, bool isLocalActor,
            ulong actorClientId, ulong targetClientId, Sprite iconSprite) {
            if(killFeedEntryTemplate == null) {
                if(_killFeedTemplateErrorLogged) return null;
                Debug.LogError("[KillFeedManager] killFeedEntryTemplate is required. Assign KillFeedEntry.uxml in the inspector.");
                _killFeedTemplateErrorLogged = true;
                return null;
            }

            var entry = killFeedEntryTemplate.CloneTree();
            var entryRoot = entry.Q<VisualElement>("kill-entry-root") ?? entry;
            var killerLabel = entry.Q<Label>("killer-label");
            var victimLabel = entry.Q<Label>("victim-label");
            var iconElement = entry.Q<VisualElement>("icon");

            if(killerLabel == null || victimLabel == null || iconElement == null) {
                if(_killFeedTemplateErrorLogged) return null;
                Debug.LogError("[KillFeedManager] KillFeedEntry template is missing required elements: killer-label, victim-label, or icon.");
                _killFeedTemplateErrorLogged = true;
                return null;
            }

            if(isLocalActor) {
                entryRoot.AddToClassList("kill-entry-local");
            }

            // Get team colors for actor and target (tag mode is FFA, so GetTeamColorForPlayer will return white)
            var actorColor = GetTeamColorForPlayer(actorClientId);
            var targetColor = GetTeamColorForPlayer(targetClientId);

            // Actor name (killer/tagger)
            killerLabel.text = actorName;
            if(isLocalActor) {
                killerLabel.AddToClassList("killer-name-local");
            }
            killerLabel.style.color = new StyleColor(actorColor);

            // Icon (kill or tag)
            if(iconSprite != null) {
                iconElement.style.backgroundImage = new StyleBackground(iconSprite);
            }

            // Target name (victim/tagged)
            victimLabel.text = targetName;
            victimLabel.style.color = new StyleColor(targetColor);

            return entry;
        }

        /// <summary>
        /// Gets the appropriate team color for a player based on their team and the local player's team.
        /// Returns a readable RGB color (not HDR) for text display.
        /// </summary>
        private Color GetTeamColorForPlayer(ulong clientId) {
            // Special case: HOP/fall damage (ulong.MaxValue) - use white
            if(clientId == ulong.MaxValue) {
                return Color.white;
            }

            // Refresh MatchSettingsManager cache if needed
            if(_cachedMatchSettings == null || !_gameModeCacheValid) {
                _cachedMatchSettings = MatchSettingsManager.Instance;
                _gameModeCacheValid = false;
            }

            // Check if this is a team-based mode
            if(!_gameModeCacheValid) {
                _cachedIsTeamBased = _cachedMatchSettings != null &&
                                     MatchSettingsManager.IsTeamBasedMode(_cachedMatchSettings.selectedGameModeId);
                _gameModeCacheValid = true;
            }

            if(!_cachedIsTeamBased) {
                // FFA mode: use default white color
                return Color.white;
            }

            // Get local player's team
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return Color.white;
            if(networkManager.LocalClient == null) return Color.white;
            var localPlayer = networkManager.LocalClient.PlayerObject;
            if(localPlayer == null) return Color.white;

            var localController = localPlayer.GetComponent<PlayerController>();
            PlayerTeamManager localTeamMgr = null;
            if(localController != null) {
                localTeamMgr = localController.TeamManager;
            }
            if(localTeamMgr == null) return Color.white;

            var localTeam = localTeamMgr.netTeam.Value;

            // Find the player by client ID
            if(!NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client)) {
                return Color.white;
            }

            var playerObj = client.PlayerObject;
            if(playerObj == null) return Color.white;

            var playerController = playerObj.GetComponent<PlayerController>();
            PlayerTeamManager playerTeamMgr = null;
            if(playerController != null) {
                playerTeamMgr = playerController.TeamManager;
            }
            if(playerTeamMgr == null) return Color.white;

            var playerTeam = playerTeamMgr.netTeam.Value;

            // Determine if this player is teammate or enemy
            var isTeammate = playerTeam == localTeam;

            // Convert HDR colors to readable RGB for text (tone down brightness)
            return isTeammate ?
                // Teammate: cyan-blue
                // HDR outline: (0, 1.5, 2.5) -> readable text: (0, 0.7, 1.0)
                new Color(0f, 0.7f, 1f, 1f) : // Bright cyan-blue
                // Enemy: red
                // HDR outline: (2.5, 0.5, 0.5) -> readable text: (1.0, 0.3, 0.3)
                new Color(1f, 0.3f, 0.3f, 1f); // Bright red
        }

        private IEnumerator FadeOutKillEntry(VisualElement entry) {
            // Wait for display time
            yield return new WaitForSeconds(killFeedDisplayTime);

            // Remove the entry
            RemoveKillEntry(entry, immediate: false);
        }

        /// <summary>
        /// Removes a kill entry from the feed.
        /// </summary>
        /// <param name="entry">The entry to remove</param>
        /// <param name="immediate">If true, removes instantly. If false, fades out first.</param>
        private void RemoveKillEntry(VisualElement entry, bool immediate) {
            if(entry == null || !_activeKillEntries.Contains(entry)) return;

            // Cancel existing fade coroutine if any
            if(_fadeCoroutines.TryGetValue(entry, out var coroutine)) {
                if(coroutine != null) {
                    StopCoroutine(coroutine);
                }

                _fadeCoroutines.Remove(entry);
            }

            if(immediate) {
                // Immediate removal (when at capacity)
                entry.AddToClassList("kill-entry-fade");

                // Remove from lists immediately
                _activeKillEntries.Remove(entry);

                // Wait one frame for fade class to apply, then remove from DOM
                StartCoroutine(RemoveAfterFrame(entry));
            } else {
                // Normal fade out
                StartCoroutine(FadeAndRemove(entry));
            }
        }

        private IEnumerator FadeAndRemove(VisualElement entry) {
            // Fade out
            entry.AddToClassList("kill-entry-fade");

            // Wait for fade animation
            yield return new WaitForSeconds(0.3f);

            // Remove from feed
            if(!_killFeedContainer.Contains(entry)) yield break;
            _killFeedContainer.Remove(entry);
            _activeKillEntries.Remove(entry);
        }

        private IEnumerator RemoveAfterFrame(VisualElement entry) {
            // Wait briefly for fade animation to start
            yield return new WaitForSeconds(0.15f);

            // Remove from DOM
            if(_killFeedContainer.Contains(entry)) {
                _killFeedContainer.Remove(entry);
            }
        }
    }
}
