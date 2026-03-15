using System;
using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Audio;
using Game.Menu;
using Game.UI.Misc;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class SceneTransitionManager : MonoBehaviour {
        private enum OverlayVisualState {
            Hidden,
            FadingOut,
            Opaque,
            FadingIn
        }

        [SerializeField] private UIDocument transitionDocument;
        [SerializeField] private float fadeDuration = 0.5f;
        [SerializeField] private float musicFadeDuration = 2.25f; // Intentionally longer than visual fade to avoid abrupt cutoffs during load.
        [SerializeField] private float transitionCompletionGraceSeconds = 0.15f;
        [SerializeField] private float respawnFadeInSignalTimeoutSeconds = 8f;

        private VisualElement _transitionOverlay;
        private VisualElement _respawnFadeOverlay; // Separate overlay for respawn fades (from GameMenu)
        private VisualElement _loadingBall; // Loading ball animation element
        private LoadingBallAnimation _loadingBallAnimation; // Animation controller
        private bool _isTransitioning;
        private bool _serverSignaledFadeIn; // Server-authoritative signal to start fade in
        private UniTaskCompletionSource<bool> _respawnFadeInSignal;
        private OverlayVisualState _transitionOverlayState = OverlayVisualState.Hidden;
        private OverlayVisualState _respawnOverlayState = OverlayVisualState.Hidden;
        private MenuMusicPlayer _menuMusicPlayer;

        // Cache scene name to avoid string allocations
        private string _cachedSceneName;

        public static SceneTransitionManager Instance { get; private set; }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            // Get or create LoadingBallAnimation component
            _loadingBallAnimation = GetComponent<LoadingBallAnimation>();
            if(_loadingBallAnimation == null) {
                _loadingBallAnimation = gameObject.AddComponent<LoadingBallAnimation>();
            }
        }

        private void OnEnable() {
            // Cache scene name to avoid allocations
            UpdateCachedSceneName();

            // Try to refresh, but don't worry if UI isn't loaded yet (we'll retry when needed)
            RefreshOverlayReference();

            // Also listen for scene loads to refresh references
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void UpdateCachedSceneName() {
            var activeScene = SceneManager.GetActiveScene();
            if(activeScene.IsValid()) {
                _cachedSceneName = activeScene.name;
            }
        }

        private void OnDisable() {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
            // Update cached scene name
            UpdateCachedSceneName();

            // Refresh references when a new scene loads (MainMenu or Game)
            RefreshOverlayReference();
            RefreshRespawnFadeOverlay(); // Also refresh respawn fade overlay
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

                // Find loading ball within the transition overlay
                if(_transitionOverlay != null) {
                    _loadingBall = _transitionOverlay.Q<VisualElement>("loading-ball");
                }
            }

            if(_transitionOverlay != null) {
                _transitionOverlayState = _transitionOverlay.ClassListContains("visible")
                    ? OverlayVisualState.Opaque
                    : OverlayVisualState.Hidden;
            }

            // Refresh respawn fade overlay from GameMenu (for respawn transitions)
            RefreshRespawnFadeOverlay();
        }

        /// <summary>
        /// Refreshes the respawn fade overlay reference from GameMenuManager's document.
        /// This overlay appears above HUD but below pause menu.
        /// </summary>
        private void RefreshRespawnFadeOverlay() {
            if(!Session.SessionManager.IsGameplaySceneName(_cachedSceneName)) return;
            
            var gameMenuManager = GameMenuManager.Instance;
            if(gameMenuManager == null) return;

            // Try to get UIDocument component
            var gameMenuDoc = gameMenuManager.GetComponent<UIDocument>();
            if(gameMenuDoc == null) return;
                    
            var gameRoot = gameMenuDoc.rootVisualElement;
            _respawnFadeOverlay = gameRoot.Q<VisualElement>("respawn-fade-overlay");
            if(_respawnFadeOverlay != null) {
                _respawnOverlayState = _respawnFadeOverlay.ClassListContains("visible")
                    ? OverlayVisualState.Opaque
                    : OverlayVisualState.Hidden;
            }
        }

        /// <summary>
        /// Fade to black, execute action, then fade back in
        /// </summary>
        public IEnumerator FadeTransition(Action duringFade) {
            if(_isTransitioning) yield break;

            _isTransitioning = true;

            // Fade to black
            yield return StartCoroutine(FadeOut());

            // Execute action (scene load, etc.)
            if(duringFade != null) {
                duringFade.Invoke();
            }

            // Wait a frame for scene to load
            yield return null;

            // Refresh overlay reference after scene change
            RefreshOverlayReference();

            // Fade back in
            yield return StartCoroutine(FadeIn());

            _isTransitioning = false;
        }

        public UniTask FadeOutAsync(float? customDuration = null) => FadeOut(customDuration).ToUniTask();

        public UniTask FadeInAsync(float? customDuration = null, string fadeColor = null) =>
            FadeIn(customDuration, fadeColor).ToUniTask();

        /// <summary>
        /// Fade to black only
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        private IEnumerator FadeOut(float? customDuration = null) {
            // Refresh overlay reference in case GameMenuManager wasn't ready when OnEnable was called
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }

            if(_transitionOverlay == null) yield break;
            if(_transitionOverlayState is OverlayVisualState.FadingOut or OverlayVisualState.Opaque) yield break;

            var duration = customDuration != null ? customDuration.Value : fadeDuration;
            SetTransitionDuration(_transitionOverlay, duration);
            StartMenuMusicFadeOut(duration);

            // Always use black for fade out
            _transitionOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));

            _transitionOverlay.style.display = DisplayStyle.Flex;
            _transitionOverlay.pickingMode = PickingMode.Position;
            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");
            _transitionOverlayState = OverlayVisualState.FadingOut;

            // Start loading ball animation when transition overlay becomes visible
            if(_loadingBall != null && _loadingBallAnimation != null) {
                _loadingBallAnimation.StartAnimation(_loadingBall);
            }

            yield return WaitForOpacityAsync(_transitionOverlay, duration).ToCoroutine();
            _transitionOverlayState = OverlayVisualState.Opaque;
        }

        /// <summary>
        /// Fade from black to clear
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        /// <param name="fadeColor">Optional custom fade color. If null, uses black. Format: "rgb(r, g, b)" or hex "#rrggbb"</param>
        private IEnumerator FadeIn(float? customDuration = null, string fadeColor = null) {
            if(_transitionOverlay == null) yield break;
            if(_transitionOverlayState is OverlayVisualState.FadingIn or OverlayVisualState.Hidden) {
                _transitionOverlay.pickingMode = PickingMode.Ignore;
                _transitionOverlay.style.display = DisplayStyle.None;
                if(_loadingBallAnimation != null) {
                    _loadingBallAnimation.StopAnimation();
                }

                _transitionOverlayState = OverlayVisualState.Hidden;
                yield break;
            }

            var duration = customDuration != null ? customDuration.Value : fadeDuration;
            SetTransitionDuration(_transitionOverlay, duration);

            // Set fade color if provided (otherwise uses default black from CSS)
            _transitionOverlay.style.backgroundColor = !string.IsNullOrEmpty(fadeColor)
                ? new StyleColor(ParseColor(fadeColor))
                :
                // Reset to black (default)
                new StyleColor(new Color(0, 0, 0, 1));

            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");
            _transitionOverlayState = OverlayVisualState.FadingIn;

            yield return WaitForOpacityAsync(_transitionOverlay, duration).ToCoroutine();

            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
            _transitionOverlayState = OverlayVisualState.Hidden;

            // Stop loading ball animation when transition overlay is hidden
            if(_loadingBallAnimation != null) {
                _loadingBallAnimation.StopAnimation();
            }
        }

        /// <summary>
        /// Fade to black using respawn fade overlay (appears above HUD but below pause menu).
        /// Used for game->podium transitions.
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        public IEnumerator FadeOutRespawnOverlay(float? customDuration = null) {
            // Refresh respawn fade overlay reference in case GameMenuManager wasn't ready when OnEnable was called
            if(_respawnFadeOverlay == null) {
                RefreshRespawnFadeOverlay();
            }

            if(_respawnFadeOverlay == null) yield break;
            if(_respawnOverlayState is OverlayVisualState.FadingOut or OverlayVisualState.Opaque) yield break;

            var duration = customDuration != null ? customDuration.Value : fadeDuration;
            SetTransitionDuration(_respawnFadeOverlay, duration);

            // Always use black for respawn overlay
            _respawnFadeOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));

            _respawnFadeOverlay.style.display = DisplayStyle.Flex;
            _respawnFadeOverlay.pickingMode = PickingMode.Position;
            _respawnFadeOverlay.RemoveFromClassList("hidden");
            _respawnFadeOverlay.AddToClassList("visible");
            _respawnOverlayState = OverlayVisualState.FadingOut;

            yield return WaitForOpacityAsync(_respawnFadeOverlay, duration).ToCoroutine();
            _respawnOverlayState = OverlayVisualState.Opaque;
        }

        /// <summary>
        /// Fade from black to clear using respawn fade overlay (appears above HUD but below pause menu).
        /// Used for game->podium transitions.
        /// </summary>
        /// <param name="customDuration">Optional custom duration. If null, uses default fadeDuration.</param>
        public IEnumerator FadeInRespawnOverlay(float? customDuration = null) {
            if(_respawnFadeOverlay == null) {
                RefreshRespawnFadeOverlay();
            }

            if(_respawnFadeOverlay == null) yield break;
            if(_respawnOverlayState is OverlayVisualState.FadingIn or OverlayVisualState.Hidden) {
                _respawnFadeOverlay.pickingMode = PickingMode.Ignore;
                _respawnFadeOverlay.style.display = DisplayStyle.None;
                _respawnOverlayState = OverlayVisualState.Hidden;
                yield break;
            }

            var duration = customDuration != null ? customDuration.Value : fadeDuration;
            SetTransitionDuration(_respawnFadeOverlay, duration);

            // Always use black for respawn overlay
            _respawnFadeOverlay.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 1));

            _respawnFadeOverlay.RemoveFromClassList("visible");
            _respawnFadeOverlay.AddToClassList("hidden");
            _respawnOverlayState = OverlayVisualState.FadingIn;

            yield return WaitForOpacityAsync(_respawnFadeOverlay, duration).ToCoroutine();

            _respawnFadeOverlay.pickingMode = PickingMode.Ignore;
            _respawnFadeOverlay.style.display = DisplayStyle.None;
            _respawnOverlayState = OverlayVisualState.Hidden;
        }

        /// <summary>
        /// Parses a color string (hex or rgb) to Unity Color
        /// </summary>
        private static Color ParseColor(string colorString) {
            colorString = colorString.Trim();

            // Handle hex format (#rrggbb or #rrggbbaa)
            if(colorString.StartsWith("#")) {
                colorString = colorString[1..];
                if(colorString.Length == 6) {
                    // RGB hex
                    var r = Convert.ToInt32(colorString[..2], 16) / 255f;
                    var g = Convert.ToInt32(colorString.Substring(2, 2), 16) / 255f;
                    var b = Convert.ToInt32(colorString.Substring(4, 2), 16) / 255f;
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
        public IEnumerator FadeRespawnTransition() {
            // Refresh overlay reference in case GameMenuManager wasn't ready when OnEnable was called
            if(_respawnFadeOverlay == null) {
                RefreshRespawnFadeOverlay();
            }

            if(_respawnFadeOverlay == null) yield break;

            _serverSignaledFadeIn = false;
            _respawnFadeInSignal = new UniTaskCompletionSource<bool>();

            try {
                // Fade to black (using default fade duration) - always use black for respawn
                // Use respawn fade overlay (from GameMenu) instead of scene transition overlay
                yield return FadeOutRespawnOverlay();

                // Signal that fade to black has completed (for teleporting/ragdoll disable)

                // Hold on black screen - wait for server to signal fade in start (server-authoritative)
                // This ensures all clients are synced regardless of network latency
                var fadeInSignaled = _serverSignaledFadeIn;
                if(!_serverSignaledFadeIn) {
                    async UniTask WaitForSignalAsync() {
                        fadeInSignaled = await WaitForRespawnFadeInAsync(_respawnFadeInSignal);
                    }

                    yield return WaitForSignalAsync().ToCoroutine();
                }

                if(!fadeInSignaled) {
                    Debug.LogWarning(
                        "[SceneTransitionManager] Respawn fade-in signal timed out or was canceled. Forcing overlay recovery.");
                    yield return FadeInRespawnOverlay();
                    ForceHideRespawnOverlay();
                    yield break;
                }

                // Signal that fade in is starting (for restoring control)

                // Fade back in (using default fade duration) - always use black for respawn
                yield return FadeInRespawnOverlay();
            } finally {
                _respawnFadeInSignal = null;
            }
        }

        /// <summary>
        /// Server-authoritative: Signal that fade in should start (called by server after hold duration)
        /// </summary>
        public void SignalFadeInStart() {
            _serverSignaledFadeIn = true;
            _respawnFadeInSignal?.TrySetResult(true);
        }

        /// <summary>
        /// Starts a menu music fade-out if music is currently playing.
        /// </summary>
        private void StartMenuMusicFadeOut(float requestedDuration) {
            var visualFadeDuration = Mathf.Max(0f, requestedDuration);
            var fadeOutDuration = Mathf.Max(musicFadeDuration, visualFadeDuration);
            var menuMusicPlayer = ResolveMenuMusicPlayer();
            if(menuMusicPlayer == null) {
                return;
            }
            menuMusicPlayer.FadeOutForTransition(fadeOutDuration);
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

            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new(0) });
            _transitionOverlay.style.transitionDuration = instantList;

            _transitionOverlay.RemoveFromClassList("hidden");
            _transitionOverlay.AddToClassList("visible");
            _transitionOverlayState = OverlayVisualState.Opaque;

            // Also fade out music instantly
            var menuMusicPlayer = ResolveMenuMusicPlayer();
            if(menuMusicPlayer != null) {
                menuMusicPlayer.StopForTransition();
            }

            // Restore normal transition duration after one frame
            StartCoroutine(RestoreTransitionDuration());
        }

        public void FadeInImmediate() {
            if(_transitionOverlay == null) {
                RefreshOverlayReference();
            }

            if(_transitionOverlay == null) return;

            var instantList = new StyleList<TimeValue>(new List<TimeValue> { new(0) });
            _transitionOverlay.style.transitionDuration = instantList;

            _transitionOverlay.RemoveFromClassList("visible");
            _transitionOverlay.AddToClassList("hidden");
            _transitionOverlay.pickingMode = PickingMode.Ignore;
            _transitionOverlay.style.display = DisplayStyle.None;
            _transitionOverlayState = OverlayVisualState.Hidden;

            // Stop loading ball animation when transition overlay is hidden
            if(_loadingBallAnimation != null) {
                _loadingBallAnimation.StopAnimation();
            }

            StartCoroutine(RestoreTransitionDuration());
        }

        private IEnumerator RestoreTransitionDuration() {
            yield return null;
            if(_transitionOverlay != null) {
                _transitionOverlay.style.transitionDuration = StyleKeyword.Null;
            }
        }

        private static void SetTransitionDuration(VisualElement overlay, float durationSeconds) {
            if(overlay == null) return;

            var clampedDuration = Mathf.Max(0f, durationSeconds);
            var durationList = new StyleList<TimeValue>(new List<TimeValue> { new(clampedDuration) });
            overlay.style.transitionDuration = durationList;
        }

        private MenuMusicPlayer ResolveMenuMusicPlayer() {
            if(_menuMusicPlayer != null) {
                return _menuMusicPlayer;
            }

            _menuMusicPlayer = MenuMusicPlayer.Instance;
            return _menuMusicPlayer;
        }

        private async UniTask WaitForOpacityAsync(VisualElement overlay, float expectedDurationSeconds) {
            if(overlay?.panel == null) return;

            var transitionCompleted = new UniTaskCompletionSource<bool>();

            EventCallback<TransitionEndEvent> onTransitionEnd = evt => {
                if(!ReferenceEquals(evt.target, overlay)) return;
                transitionCompleted.TrySetResult(true);
            };

            EventCallback<TransitionCancelEvent> onTransitionCancel = evt => {
                if(!ReferenceEquals(evt.target, overlay)) return;
                transitionCompleted.TrySetResult(true);
            };

            overlay.RegisterCallback(onTransitionEnd);
            overlay.RegisterCallback(onTransitionCancel);

            var timeoutSeconds = Mathf.Max(0.05f, expectedDurationSeconds + transitionCompletionGraceSeconds);

            try {
                await UniTask.WhenAny(
                    WaitForTransitionAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds),
                        cancellationToken: this.GetCancellationTokenOnDestroy()));
            } catch(OperationCanceledException) {
                // Object destroyed during scene change.
            } finally {
                overlay.UnregisterCallback(onTransitionEnd);
                overlay.UnregisterCallback(onTransitionCancel);
            }

            return;

            async UniTask WaitForTransitionAsync() {
                await transitionCompleted.Task;
            }
        }

        private async UniTask<bool> WaitForRespawnFadeInAsync(UniTaskCompletionSource<bool> signal) {
            if(signal == null) return true;

            var signalReceived = false;
            async UniTask WaitForSignalAsync() {
                signalReceived = await signal.Task;
            }

            var timeoutSeconds = Mathf.Max(0.1f, respawnFadeInSignalTimeoutSeconds);
            try {
                var winner = await UniTask.WhenAny(
                    WaitForSignalAsync(),
                    UniTask.Delay(TimeSpan.FromSeconds(timeoutSeconds),
                        cancellationToken: this.GetCancellationTokenOnDestroy()));
                return winner == 0 && signalReceived;
            } catch(OperationCanceledException) {
                return false;
            }
        }

        private void ForceHideRespawnOverlay() {
            if(_respawnFadeOverlay == null) {
                RefreshRespawnFadeOverlay();
            }

            if(_respawnFadeOverlay == null) return;

            _respawnFadeOverlay.RemoveFromClassList("visible");
            _respawnFadeOverlay.AddToClassList("hidden");
            _respawnFadeOverlay.pickingMode = PickingMode.Ignore;
            _respawnFadeOverlay.style.display = DisplayStyle.None;
            _respawnOverlayState = OverlayVisualState.Hidden;
        }
    }
}
