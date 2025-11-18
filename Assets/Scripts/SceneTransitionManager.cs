using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class SceneTransitionManager : MonoBehaviour {
        [SerializeField] private UIDocument transitionDocument;
        [SerializeField] private float fadeDuration = 0.5f;
        [SerializeField] private float musicFadeDuration = 1.5f; // Slightly longer for smooth music fade
        [SerializeField] private float respawnHoldDuration = 0.5f; // How long to hold on black screen during respawn
        
        private VisualElement _transitionOverlay;
        private bool _isTransitioning;
        private bool _isRespawnFading; // Track if respawn fade is in progress
        private bool _hasRespawnFadeInStarted; // Track when fade in starts (for restoring control)
        private bool _hasRespawnFadeInCompleted; // Track when fade in completes (for camera switch)
        private bool _hasRespawnFadeOutCompleted; // Track when fade to black completes
        private bool _serverSignaledFadeIn; // Server-authoritative signal to start fade in

        public static SceneTransitionManager Instance { get; private set; }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() {
            // Try to refresh, but don't worry if UI isn't loaded yet (we'll retry when needed)
            RefreshOverlayReference();
            
            // Also listen for scene loads to refresh references
            SceneManager.sceneLoaded += OnSceneLoaded;
        }
        
        private void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
        
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            // Refresh references when a new scene loads (MainMenu or Game)
            RefreshOverlayReference();
        }
        
        /// <summary>
        /// Refreshes the overlay reference, checking both transition document and GameMenuManager's document.
        /// This ensures the overlay is found even if GameMenuManager initializes after SceneTransitionManager.
        /// </summary>
        private void RefreshOverlayReference() {
            // Try to find overlay in transition document first (for MainMenu transitions)
            if(transitionDocument != null) {
                var root = transitionDocument.rootVisualElement;
                _transitionOverlay = root.Q<VisualElement>("transition-overlay");
            }
            
            // If not found and in Game scene, try to find it in GameMenuManager's document
            // This allows the overlay to render behind pause menu/scoreboard (since it's first in hierarchy)
            if(_transitionOverlay == null && SceneManager.GetActiveScene().name.Contains("Game")) {
                var gameMenuManager = GameMenuManager.Instance;
                if(gameMenuManager != null) {
                    // Try to get UIDocument component
                    var gameMenuDoc = gameMenuManager.GetComponent<UIDocument>();
                    if(gameMenuDoc != null) {
                        var gameRoot = gameMenuDoc.rootVisualElement;
                        _transitionOverlay = gameRoot.Q<VisualElement>("transition-overlay");
                    }
                }
            }
        }

        /// <summary>
        /// Fade to black, execute action, then fade back in
        /// </summary>
        public IEnumerator FadeTransition(System.Action duringFade) {
            if(_isTransitioning) yield break;
            
            _isTransitioning = true;

            // Fade out menu music if it exists
            StartCoroutine(FadeOutMenuMusic());

            // Fade to black
            yield return StartCoroutine(FadeOut());

            // Execute action (scene load, etc.)
            duringFade?.Invoke();

            // Wait a frame for scene to load
            yield return null;
            
            // Refresh overlay reference after scene change
            RefreshOverlayReference();

            // Fade back in
            yield return StartCoroutine(FadeIn());

            _isTransitioning = false;
        }
        
        public UniTask FadeOutAsync(float? customDuration = null) => FadeOut(customDuration).ToUniTask();
        public UniTask FadeInAsync(float? customDuration = null, string fadeColor = null)  => FadeIn(customDuration, fadeColor).ToUniTask();

        /// <summary>
        /// Fade to black only
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        public IEnumerator FadeOut(float? customDuration = null) {
            // Refresh overlay reference in case GameMenuManager wasn't ready when OnEnable was called
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }
            if(_transitionOverlay == null) yield break;

            var duration = customDuration ?? fadeDuration;

            // Always use black for fade out
            _transitionOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));

            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");

            yield return new WaitForSeconds(duration);
        }

        /// <summary>
        /// Fade from black to clear
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        /// <param name="fadeColor">Optional custom fade color. If null, uses black. Format: "rgb(r, g, b)" or hex "#rrggbb"</param>
        public IEnumerator FadeIn(float? customDuration = null, string fadeColor = null) {
            if(_transitionOverlay == null) yield break;

            var duration = customDuration ?? fadeDuration;

            // Set fade color if provided (otherwise uses default black from CSS)
            if(!string.IsNullOrEmpty(fadeColor)) {
                _transitionOverlay.style.backgroundColor = new StyleColor(ParseColor(fadeColor));
            } else {
                // Reset to black (default)
                _transitionOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));
            }

            yield return new WaitForSeconds(0.1f); // Small delay for scene to settle

            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");

            yield return new WaitForSeconds(duration);
            
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
        }
        
        /// <summary>
        /// Parses a color string (hex or rgb) to Unity Color
        /// </summary>
        private Color ParseColor(string colorString) {
            colorString = colorString.Trim();
            
            // Handle hex format (#rrggbb or #rrggbbaa)
            if(colorString.StartsWith("#")) {
                colorString = colorString.Substring(1);
                if(colorString.Length == 6) {
                    // RGB hex
                    var r = System.Convert.ToInt32(colorString.Substring(0, 2), 16) / 255f;
                    var g = System.Convert.ToInt32(colorString.Substring(2, 2), 16) / 255f;
                    var b = System.Convert.ToInt32(colorString.Substring(4, 2), 16) / 255f;
                    return new Color(r, g, b, 1f);
                }
            }
            
            // Handle rgb(r, g, b) format
            if(colorString.StartsWith("rgb(") && colorString.EndsWith(")")) {
                var values = colorString.Substring(4, colorString.Length - 5).Split(',');
                if(values.Length >= 3) {
                    var r = float.Parse(values[0].Trim()) / 255f;
                    var g = float.Parse(values[1].Trim()) / 255f;
                    var b = float.Parse(values[2].Trim()) / 255f;
                    return new Color(r, g, b, 1f);
                }
            }
            
            // Default to black if parsing fails
            Debug.LogWarning($"[SceneTransitionManager] Failed to parse color: {colorString}, using black");
            return Color.black;
        }

        /// <summary>
        /// Respawn transition: fade to black (using default duration), hold on black screen, then fade back in.
        /// Uses default fade duration for consistency with main menu transitions.
        /// </summary>
        /// <param name="customHoldDuration">Optional custom hold duration. If null, uses respawnHoldDuration.</param>
        public IEnumerator FadeRespawnTransition(float? customHoldDuration = null) {
            // Refresh overlay reference in case GameMenuManager wasn't ready when OnEnable was called
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }
            if(_transitionOverlay == null) yield break;

            _isRespawnFading = true;
            _hasRespawnFadeInStarted = false;
            _hasRespawnFadeInCompleted = false;
            _hasRespawnFadeOutCompleted = false;
            _serverSignaledFadeIn = false;

            var holdDur = customHoldDuration ?? respawnHoldDuration;

            // Fade to black (using default fade duration) - always use black for respawn
            _transitionOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));
            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");

            yield return new WaitForSeconds(fadeDuration);

            // Small buffer to ensure CSS transition is fully complete and overlay is fully opaque
            yield return new WaitForSeconds(0.05f);

            // Signal that fade to black has completed (for teleporting/ragdoll disable)
            _hasRespawnFadeOutCompleted = true;

            // Hold on black screen - wait for server to signal fade in start (server-authoritative)
            // This ensures all clients are synced regardless of network latency
            while(!_serverSignaledFadeIn) {
                yield return null;
            }

            // Signal that fade in is starting (for restoring control)
            _hasRespawnFadeInStarted = true;

            // Fade back in (using default fade duration) - always use black for respawn
            _transitionOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));
            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");

            yield return new WaitForSeconds(fadeDuration);
            
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;

            // Signal that fade in has completed (for camera switch)
            _hasRespawnFadeInCompleted = true;

            _isRespawnFading = false;
        }

        /// <summary>
        /// Check if respawn fade is currently in progress
        /// </summary>
        public bool IsRespawnFading => _isRespawnFading;

        /// <summary>
        /// Check if respawn fade in has started (use this to restore control)
        /// </summary>
        public bool HasRespawnFadeInStarted => _hasRespawnFadeInStarted;

        /// <summary>
        /// Check if respawn fade in has completed (use this to switch cameras)
        /// </summary>
        public bool HasRespawnFadeInCompleted => _hasRespawnFadeInCompleted;

        /// <summary>
        /// Check if respawn fade to black has completed (use this to teleport/disable ragdoll)
        /// </summary>
        public bool HasRespawnFadeOutCompleted => _hasRespawnFadeOutCompleted;

        /// <summary>
        /// Server-authoritative: Signal that fade in should start (called by server after hold duration)
        /// </summary>
        public void SignalFadeInStart() {
            _serverSignaledFadeIn = true;
        }

        /// <summary>
        /// Fade out menu music if MenuMusicPlayer exists
        /// </summary>
        private IEnumerator FadeOutMenuMusic() {
            var menuMusicPlayer = FindFirstObjectByType<MenuMusicPlayer>();
            if(menuMusicPlayer == null) yield break;

            var musicSource = menuMusicPlayer.GetComponent<AudioSource>();
            if(musicSource == null || !musicSource.isPlaying) yield break;

            float startVolume = musicSource.volume;
            float elapsed = 0f;

            while(elapsed < musicFadeDuration) {
                elapsed += Time.deltaTime;
                musicSource.volume = Mathf.Lerp(startVolume, 0f, elapsed / musicFadeDuration);
                yield return null;
            }

            musicSource.volume = 0f;
            musicSource.Stop();
        }

        /// <summary>
        /// Quick fade for instant transitions
        /// </summary>
        public void FadeOutImmediate() {
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }
            if(_transitionOverlay == null) return;
            
            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            
            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new TimeValue(0) });
            _transitionOverlay.style.transitionDuration = instantList;

            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");
            
            // Also fade out music instantly
            var menuMusicPlayer = FindFirstObjectByType<MenuMusicPlayer>();
            if(menuMusicPlayer != null) {
                var musicSource = menuMusicPlayer.GetComponent<AudioSource>();
                if(musicSource != null && musicSource.isPlaying) {
                    musicSource.Stop();
                    musicSource.volume = 0f;
                }
            }
            
            // Restore normal transition duration after one frame
            StartCoroutine(RestoreTransitionDuration());
        }

        public void FadeInImmediate() {
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }
            if(_transitionOverlay == null) return;
            
            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new TimeValue(0) });
            _transitionOverlay.style.transitionDuration = instantList;
            
            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
            
            StartCoroutine(RestoreTransitionDuration());
        }

        private IEnumerator RestoreTransitionDuration() {
            yield return null;
            if(_transitionOverlay != null) {
                _transitionOverlay.style.transitionDuration = StyleKeyword.Null;
            }
        }
    }
}