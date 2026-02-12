using System;
using System.Collections;
using System.Collections.Generic;
using Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Network.Singletons {
    /// <summary>
    /// Simplified keybind manager that uses settings.json as the source of truth.
    /// </summary>
    public class KeybindManager : MonoBehaviour {
        public static KeybindManager Instance { get; private set; }

        [SerializeField] private InputActionAsset inputActionAsset;

        private readonly Dictionary<string, InputAction> _actions = new();
        private readonly Dictionary<string, Dictionary<int, string>> _pendingBindings = new();

        // Rebinding state
        private Coroutine _scrollListenerCoroutine;
        private Coroutine _mouseButtonListenerCoroutine;
        private string _currentRebindingKeybindName;
        private int _currentRebindingIndex;
        private Action<string> _currentRebindingCallback;
        private InputActionRebindingExtensions.RebindingOperation _currentRebindingOperation;
        private InputAction _currentRebindingAction;
        private bool _currentRebindingWasEnabled;

        // Map display names to action names and composite parts
        private readonly Dictionary<string, (string actionName, string compositePart)> _keybindMap =
            new() {
                { "forward", ("Move", "up") },
                { "back", ("Move", "down") },
                { "left", ("Move", "left") },
                { "right", ("Move", "right") },
                { "jump", ("Jump", null) },
                { "interact", ("Interact", null) },
                { "shoot", ("Attack", null) },
                { "ads", ("Zoom", null) },
                { "reload", ("Reload", null) },
                { "grapple", ("Grapple", null) },
                { "primary", ("Primary", null) },
                { "secondary", ("Secondary", null) },
                { "nextweapon", ("NextWeapon", null) },
                { "previousweapon", ("PreviousWeapon", null) },
                { "ptt", ("Voice", null) }
            };

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            if(inputActionAsset == null) {
                Debug.LogError("[KeybindManager] InputActionAsset not assigned!");
                return;
            }

            InitializeActions();
            SetDefaultBindingsIfNeeded();
            LoadAllBindings();
        }

        #region Initialization

        private void InitializeActions() {
            if(inputActionAsset == null) return;

            var playerMap = inputActionAsset.FindActionMap("Player");
            if(playerMap == null) {
                Debug.LogError("[KeybindManager] Player action map not found!");
                return;
            }

            foreach(var kvp in _keybindMap) {
                var actionName = kvp.Value.actionName;
                var action = playerMap.FindAction(actionName);
                if(action != null) {
                    _actions[kvp.Key] = action;
                } else {
                    Debug.LogWarning($"[KeybindManager] Action '{actionName}' not found in Player map");
                }
            }
        }


        private static void SetDefaultBindingsIfNeeded() {
            var defaults = new Dictionary<string, string[]> {
                { "forward", new[] { "<Keyboard>/w", "" } },
                { "back", new[] { "<Keyboard>/s", "" } },
                { "left", new[] { "<Keyboard>/a", "" } },
                { "right", new[] { "<Keyboard>/d", "" } },
                { "reload", new[] { "<Keyboard>/r", "" } },
                { "grapple", new[] { "<Keyboard>/q", "" } },
                { "jump", new[] { "<Keyboard>/space", "SCROLL_DOWN" } },
                { "interact", new[] { "<Keyboard>/e", "" } },
                { "shoot", new[] { "<Mouse>/leftButton", "" } },
                { "ads", new[] { "<Mouse>/rightButton", "" } },
                { "primary", new[] { "<Keyboard>/1", "" } },
                { "secondary", new[] { "<Keyboard>/2", "" } },
                { "nextweapon", new[] { "", "" } },
                { "previousweapon", new[] { "", "" } },
                { "ptt", new[] { "<Keyboard>/v", "" } }
            };

            var settings = GameSettings.Data.keybinds;
            if(settings == null) return;

            if(settings.entries == null) {
                settings.entries = new List<SettingsData.KeybindEntry>();
            }

            var changesMade = false;
            foreach(var kvp in defaults) {
                var entry = GetOrCreateKeybindEntry(settings.entries, kvp.Key);
                if(entry == null) continue;

                // Only set defaults when empty (don't stomp player edits).
                if(string.IsNullOrEmpty(entry.binding0) && !string.IsNullOrEmpty(kvp.Value[0])) {
                    entry.binding0 = kvp.Value[0];
                    changesMade = true;
                }

                if(!string.IsNullOrEmpty(entry.binding1) || string.IsNullOrEmpty(kvp.Value[1])) continue;
                entry.binding1 = kvp.Value[1];
                changesMade = true;
            }

            if(changesMade) {
                GameSettings.Save();
            }
        }

        private void LoadAllBindings() {
            var settings = GameSettings.Data.keybinds;
            if(settings == null) return;
            if(settings.entries == null) return;

            foreach(var keybindName in _keybindMap.Keys) {
                var paths = new List<string>();
                var entry = FindKeybindEntry(settings.entries, keybindName);
                if(entry != null) {
                    paths.Add(entry.binding0);
                    paths.Add(entry.binding1);
                } else {
                    paths.Add("");
                    paths.Add("");
                }

                ApplySavedBindings(keybindName, paths);
            }
        }

        #endregion

        #region Public API

        public void StartRebinding(string keybindName, int bindingIndex, Action<string> onComplete) {
            if(!_keybindMap.TryGetValue(keybindName, out var value)) {
                Debug.LogError($"[KeybindManager] Unknown keybind: {keybindName}");
                if(onComplete != null) {
                    onComplete.Invoke(null);
                }

                return;
            }

            if(!_actions.TryGetValue(keybindName, out var action)) {
                Debug.LogError($"[KeybindManager] Action not found for keybind: {keybindName}");
                return;
            }

            _currentRebindingKeybindName = keybindName;
            _currentRebindingIndex = bindingIndex;
            _currentRebindingCallback = onComplete;

            StartCustomInputListeners();

            var (_, compositePart) = value;
            var actualBindingIndex = GetBindingIndex(action, bindingIndex, compositePart);

            if(actualBindingIndex < 0 || actualBindingIndex >= action.bindings.Count) {
                Debug.LogError($"[KeybindManager] Invalid binding index for {keybindName}[{bindingIndex}]");
                CleanupRebindingState();
                if(onComplete != null) {
                    onComplete.Invoke(null);
                }

                return;
            }

            _currentRebindingAction = action;
            _currentRebindingWasEnabled = action.enabled;
            if(_currentRebindingWasEnabled) {
                action.Disable();
            }

            var rebindOp = action.PerformInteractiveRebinding(actualBindingIndex)
                .WithCancelingThrough("<Keyboard>/escape")
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Pointer>/delta")
                .WithControlsExcluding("<Mouse>/scroll")
                .OnMatchWaitForAnother(0.1f);

            _currentRebindingOperation = rebindOp;

            rebindOp
                .OnComplete(operation => {
                    var binding = action.bindings[actualBindingIndex];
                    var bindingPath = binding.effectivePath ?? binding.path;

                    if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                        _currentRebindingAction.Enable();
                    }

                    // If Input System captured scroll, cancel it - our listener handles scroll
                    if(!string.IsNullOrEmpty(bindingPath) &&
                       (bindingPath.Contains("scroll") || bindingPath.Contains("Scroll"))) {
                        CleanupRebindingState();
                        operation.Dispose();
                        if(onComplete != null) {
                            onComplete.Invoke(null);
                        }

                        return;
                    }

                    // Clean the path - remove any groups/interactions (everything after semicolon)
                    if(!string.IsNullOrEmpty(bindingPath) && bindingPath.Contains(';')) {
                        bindingPath = bindingPath.Split(';')[0].Trim();
                    }

                    CleanupRebindingState();
                    var displayString = GetBindingDisplayString(new InputBinding { path = bindingPath });
                    StorePendingBinding(keybindName, bindingIndex, bindingPath);
                    if(onComplete != null) {
                        onComplete.Invoke(displayString);
                    }

                    operation.Dispose();
                })
                .OnCancel(operation => {
                    if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                        _currentRebindingAction.Enable();
                    }

                    CleanupRebindingState();
                    StorePendingBinding(keybindName, bindingIndex, "");
                    if(onComplete != null) {
                        onComplete.Invoke("None");
                    }

                    operation.Dispose();
                })
                .Start();
        }

        public void SaveBindings() {
            var settings = GameSettings.Data.keybinds;
            if(settings == null) return;
            if(settings.entries == null) settings.entries = new List<SettingsData.KeybindEntry>();

            foreach(var (keybindName, value) in _pendingBindings) {
                foreach(var (bindingIndex, path) in value) {
                    var entry = GetOrCreateKeybindEntry(settings.entries, keybindName);
                    if(entry == null) continue;

                    switch(bindingIndex) {
                        case 0:
                            entry.binding0 = path ?? "";
                            break;
                        case 1:
                            entry.binding1 = path ?? "";
                            break;
                    }
                }
            }

            GameSettings.Save();
            ApplyPendingBindings();
            _pendingBindings.Clear();
            LoadAllBindings();
        }

        public void CancelBindings() {
            CancelActiveRebinding();
            _pendingBindings.Clear();
        }

        public void CancelActiveRebinding() {
            if(_currentRebindingOperation != null) {
                _currentRebindingCallback = null;
                try {
                    _currentRebindingOperation.Cancel();
                    _currentRebindingOperation.Dispose();
                } catch {
                    // Already disposed
                }

                _currentRebindingOperation = null;
            }

            if(this != null) {
                CleanupRebindingState();
            }
        }

        public bool HasPendingBindings() {
            foreach(var kvp in _pendingBindings) {
                foreach(var path in kvp.Value.Values) {
                    if(!string.IsNullOrEmpty(path)) {
                        return true;
                    }
                }
            }

            return false;
        }

        public static string GetBindingDisplayString(string keybindName, int bindingIndex) {
            var settings = GameSettings.Data.keybinds;
            if(settings?.entries == null) return "None";

            var entry = FindKeybindEntry(settings.entries, keybindName);
            if(entry == null) return "None";

            var savedPath = bindingIndex == 0 ? entry.binding0 : entry.binding1;
            if(string.IsNullOrEmpty(savedPath)) return "None";

            return savedPath switch {
                "SCROLL_UP" => "Scroll Up",
                "SCROLL_DOWN" => "Scroll Down",
                _ => GetBindingDisplayString(new InputBinding { path = savedPath })
            };
        }

        public bool IsKeyPressed(string keybindName) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].IsPressed();
        }

        public bool WasKeyPressedThisFrame(string keybindName) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].WasPressedThisFrame();
        }

        public bool WasKeyReleasedThisFrame(string keybindName) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].WasReleasedThisFrame();
        }

        #endregion

        #region Helper Methods

        private static SettingsData.KeybindEntry FindKeybindEntry(List<SettingsData.KeybindEntry> list, string keybindName) {
            if(list == null) return null;
            if(string.IsNullOrEmpty(keybindName)) return null;

            foreach(var e in list) {
                if(e == null) continue;
                if(e.name == keybindName) return e;
            }

            return null;
        }

        private static SettingsData.KeybindEntry GetOrCreateKeybindEntry(List<SettingsData.KeybindEntry> list, string keybindName) {
            var existing = FindKeybindEntry(list, keybindName);
            if(existing != null) return existing;
            if(list == null) return null;
            if(string.IsNullOrEmpty(keybindName)) return null;

            var created = new SettingsData.KeybindEntry {
                name = keybindName
            };
            list.Add(created);
            return created;
        }

        private static int GetBindingIndex(InputAction action, int bindingIndex, string compositePart) {
            if(!string.IsNullOrEmpty(compositePart)) {
                // Composite binding: find nth occurrence of composite part
                var occurrence = 0;
                for(var i = 0; i < action.bindings.Count; i++) {
                    var binding = action.bindings[i];
                    if(!binding.isPartOfComposite || binding.name != compositePart) continue;
                    if(occurrence == bindingIndex) {
                        return i;
                    }

                    occurrence++;
                }
            } else {
                // Regular binding: find nth non-composite binding
                var nonCompositeBindings = new List<int>();
                for(var i = 0; i < action.bindings.Count; i++) {
                    var binding = action.bindings[i];
                    if(binding is { isComposite: false, isPartOfComposite: false }) {
                        nonCompositeBindings.Add(i);
                    }
                }

                if(bindingIndex < nonCompositeBindings.Count) {
                    return nonCompositeBindings[bindingIndex];
                }

                // Need to create new binding - extract just the path from template
                if(nonCompositeBindings.Count <= 0) return -1;
                var templateBinding = action.bindings[nonCompositeBindings[0]];
                // Extract just the path (before any semicolon which indicates groups/interactions)
                var cleanPath = templateBinding.path;
                if(!string.IsNullOrEmpty(cleanPath) && cleanPath.Contains(';')) {
                    cleanPath = cleanPath.Split(';')[0].Trim();
                }

                // Add binding with clean path only (no groups/interactions)
                action.AddBinding(cleanPath);
                return action.bindings.Count - 1;
            }

            return -1;
        }

        private void StorePendingBinding(string keybindName, int bindingIndex, string path) {
            if(!_pendingBindings.ContainsKey(keybindName)) {
                _pendingBindings[keybindName] = new Dictionary<int, string>();
            }

            _pendingBindings[keybindName][bindingIndex] = path ?? "";
        }

        private void ApplySavedBindings(string keybindName, List<string> paths) {
            if(!_actions.TryGetValue(keybindName, out var action)) return;

            var (_, compositePart) = _keybindMap[keybindName];

            for(var i = 0; i < paths.Count; i++) {
                var path = paths[i];

                // Skip scroll bindings - handled via PlayerPrefs in PlayerInput
                if(path.StartsWith("SCROLL_")) {
                    continue;
                }

                // Ensure binding exists
                var bindingIndex = GetBindingIndex(action, i, compositePart);
                if(bindingIndex < 0) {
                    continue;
                }

                // Apply binding override (or remove if empty)
                try {
                    if(string.IsNullOrEmpty(path)) {
                        action.RemoveBindingOverride(bindingIndex);
                    } else {
                        action.ApplyBindingOverride(bindingIndex, path);
                    }
                } catch(Exception e) {
                    Debug.LogWarning(
                        $"[KeybindManager] Failed to apply binding for {keybindName}[{i}]: {e.Message}");
                }
            }
        }

        private void ApplyPendingBindings() {
            foreach(var (keybindName, value) in _pendingBindings) {
                var paths = new List<string>();
                for(var i = 0; i < 2; i++) {
                    paths.Add(value.GetValueOrDefault(i, ""));
                }

                ApplySavedBindings(keybindName, paths);
            }
        }

        private static string GetBindingDisplayString(InputBinding binding) {
            var path = binding.effectivePath ?? binding.path;

            if(string.IsNullOrEmpty(path) || path == "<Invalid>/None" || path.Contains("<Invalid>")) {
                return "None";
            }

            // Handle scroll bindings
            switch(path) {
                case "SCROLL_UP":
                    return "Scroll Up";
                case "SCROLL_DOWN":
                    return "Scroll Down";
                case "SCROLL_LEFT":
                    return "Scroll Left";
                case "SCROLL_RIGHT":
                    return "Scroll Right";
            }

            // Try to get display name from Input System
            var control = InputSystem.FindControl(path);
            if(control != null) {
                var displayName = control.displayName;
                if((path.Contains("scroll") || path.Contains("Scroll")) &&
                   displayName is "Up" or "Down" or "Left" or "Right") {
                    return $"Scroll {displayName}";
                }

                return displayName;
            }

            // Fallback parsing
            if(path.StartsWith("<Keyboard>/")) {
                return path.Replace("<Keyboard>/", "").ToUpper();
            }

            if(!path.StartsWith("<Mouse>/")) return path;
            var mousePath = path.Replace("<Mouse>/", "");
            return mousePath switch {
                "leftButton" => "Left Click",
                "rightButton" => "Right Click",
                "middleButton" => "Middle Click",
                "xButton1" or "forwardButton" => "Mouse 4",
                "xButton2" or "backButton" => "Mouse 5",
                _ => mousePath
            };
        }

        #endregion

        #region Custom Input Listeners

        private void StartCustomInputListeners() {
            if(_mouseButtonListenerCoroutine != null) {
                StopCoroutine(_mouseButtonListenerCoroutine);
            }

            _mouseButtonListenerCoroutine = StartCoroutine(ListenForMouseButtons());

            if(_scrollListenerCoroutine != null) {
                StopCoroutine(_scrollListenerCoroutine);
            }

            _scrollListenerCoroutine = StartCoroutine(ListenForScrollInput());
        }

        private void CleanupRebindingState() {
            if(_scrollListenerCoroutine != null) {
                try {
                    StopCoroutine(_scrollListenerCoroutine);
                } catch {
                    // Already stopped
                }

                _scrollListenerCoroutine = null;
            }

            if(_mouseButtonListenerCoroutine != null) {
                try {
                    StopCoroutine(_mouseButtonListenerCoroutine);
                } catch {
                    // Already stopped
                }

                _mouseButtonListenerCoroutine = null;
            }

            _currentRebindingKeybindName = null;
            _currentRebindingCallback = null;
            _currentRebindingOperation = null;
            _currentRebindingAction = null;
            _currentRebindingWasEnabled = false;
        }

        private IEnumerator ListenForScrollInput() {
            var lastScrollValue = Vector2.zero;
            var scrollCooldown = 0f;

            while(_currentRebindingKeybindName != null) {
                if(Mouse.current != null) {
                    var currentScroll = Mouse.current.scroll.value;
                    var scrollDelta = currentScroll - lastScrollValue;

                    if(scrollCooldown <= 0f && scrollDelta.magnitude > 0.1f) {
                        string scrollPath;
                        string displayString;

                        if(Mathf.Abs(scrollDelta.y) > Mathf.Abs(scrollDelta.x)) {
                            scrollPath = scrollDelta.y > 0 ? "SCROLL_UP" : "SCROLL_DOWN";
                            displayString = scrollDelta.y > 0 ? "Scroll Up" : "Scroll Down";
                        } else {
                            scrollPath = scrollDelta.x > 0 ? "SCROLL_RIGHT" : "SCROLL_LEFT";
                            displayString = scrollDelta.x > 0 ? "Scroll Right" : "Scroll Left";
                        }

                        var keybindName = _currentRebindingKeybindName;
                        var bindingIndex = _currentRebindingIndex;
                        var callback = _currentRebindingCallback;

                        if(_currentRebindingOperation != null) {
                            _currentRebindingOperation.Cancel();
                            _currentRebindingOperation = null;
                        }

                        if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                            _currentRebindingAction.Enable();
                        }

                        StorePendingBinding(keybindName, bindingIndex, scrollPath);
                        CleanupRebindingState();
                        if(callback != null) {
                            callback.Invoke(displayString);
                        }

                        yield break;
                    }

                    lastScrollValue = currentScroll;
                    if(scrollDelta.magnitude > 0.1f) {
                        scrollCooldown = 0.3f;
                    }
                }

                if(scrollCooldown > 0f) {
                    scrollCooldown -= Time.deltaTime;
                }

                yield return null;
            }
        }

        private IEnumerator ListenForMouseButtons() {
            var mouse4WasPressed = false;
            var mouse5WasPressed = false;

            while(_currentRebindingKeybindName != null) {
                if(Mouse.current != null) {
                    if(CheckMouseButton("xButton1", "forwardButton", ref mouse4WasPressed)) {
                        HandleMouseButtonBinding("<Mouse>/xButton1", "Mouse 4");
                        yield break;
                    }

                    if(CheckMouseButton("xButton2", "backButton", ref mouse5WasPressed)) {
                        HandleMouseButtonBinding("<Mouse>/xButton2", "Mouse 5");
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private static bool CheckMouseButton(string primaryControl, string fallbackControl, ref bool wasPressed) {
            if(Mouse.current == null) return false;

            var pressedThisFrame = false;

            try {
                if(Mouse.current[primaryControl] is ButtonControl { wasPressedThisFrame: true }) {
                    pressedThisFrame = true;
                }
            } catch {
                // Control doesn't exist
            }

            if(!pressedThisFrame) {
                try {
                    if(Mouse.current[fallbackControl] is ButtonControl { wasPressedThisFrame: true }) {
                        pressedThisFrame = true;
                    }
                } catch {
                    // Control doesn't exist
                }
            }

            switch(pressedThisFrame) {
                case true when !wasPressed:
                    wasPressed = true;
                    return true;
                case false:
                    try {
                        var primaryButton = Mouse.current[primaryControl] as ButtonControl;
                        var fallbackButton = Mouse.current[fallbackControl] as ButtonControl;
                        var isActuallyPressed = primaryButton is { isPressed: true } 
                                                || fallbackButton is { isPressed: true };

                        if(!isActuallyPressed) {
                            wasPressed = false;
                        }
                    } catch {
                        // Control doesn't exist
                    }

                    break;
            }

            return false;
        }

        private void HandleMouseButtonBinding(string path, string displayName) {
            if(string.IsNullOrEmpty(_currentRebindingKeybindName)) {
                return;
            }

            var keybindName = _currentRebindingKeybindName;
            var bindingIndex = _currentRebindingIndex;
            var callback = _currentRebindingCallback;

            if(_currentRebindingOperation != null) {
                _currentRebindingOperation.Cancel();
                _currentRebindingOperation = null;
            }

            if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                _currentRebindingAction.Enable();
            }

            StorePendingBinding(keybindName, bindingIndex, path);
            CleanupRebindingState();
            if(callback != null) {
                callback.Invoke(displayName);
            }
        }

        #endregion
    }
}