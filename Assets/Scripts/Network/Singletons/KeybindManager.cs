using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;

namespace Network.Singletons {
    public class KeybindManager : MonoBehaviour {
        public static KeybindManager Instance { get; private set; }

        [SerializeField] private InputActionAsset inputActionAsset;
        
        private Dictionary<string, InputAction> _actions = new Dictionary<string, InputAction>();
        private Dictionary<string, List<InputBinding>> _pendingBindings = new Dictionary<string, List<InputBinding>>();
        
        // Helper: Store path strings directly for scroll bindings (since InputBinding.path is read-only)
        private Dictionary<string, Dictionary<int, string>> _pendingBindingPaths = new Dictionary<string, Dictionary<int, string>>();
        
        // For custom scroll and mouse button input detection
        private Coroutine _scrollListenerCoroutine;
        private Coroutine _mouseButtonListenerCoroutine;
        private string _currentRebindingKeybindName;
        private int _currentRebindingIndex;
        private Action<string> _currentRebindingCallback;
        private InputActionRebindingExtensions.RebindingOperation _currentRebindingOperation;
        private InputAction _currentRebindingAction;
        private bool _currentRebindingWasEnabled;
        
        // TODO: Revisit preventing left click binding when clicking UI elements
        // Currently allowing left click to be bound even when clicking UI elements
        // Future improvement: detect when user clicks UI buttons/tabs/etc during rebinding
        // and cancel the rebind to allow normal UI interaction
        
        // Map display names to action names and composite parts
        private readonly Dictionary<string, (string actionName, string compositePart)> _keybindMap = new Dictionary<string, (string, string)> {
            { "forward", ("Move", "up") },
            { "back", ("Move", "down") },
            { "left", ("Move", "left") },
            { "right", ("Move", "right") },
            { "jump", ("Jump", null) },
            { "shoot", ("Attack", null) },
            { "reload", ("Reload", null) },
            { "grapple", ("Grapple", null) },
            { "primary", ("Primary", null) },
            { "secondary", ("Secondary", null) },
            { "nextweapon", ("NextWeapon", null) },
            { "previousweapon", ("PreviousWeapon", null) }
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

        public void StartRebinding(string keybindName, int bindingIndex, Action<string> onComplete) {
            if(!_keybindMap.TryGetValue(keybindName, out var value)) {
                Debug.LogError($"[KeybindManager] Unknown keybind: {keybindName}");
                onComplete?.Invoke(null);
                return;
            }

            var (_, compositePart) = value;
            if(!_actions.ContainsKey(keybindName)) {
                Debug.LogError($"[KeybindManager] Action not found for keybind: {keybindName}");
                onComplete?.Invoke(null);
                return;
            }

            // Store current rebinding state
            _currentRebindingKeybindName = keybindName;
            _currentRebindingIndex = bindingIndex;
            _currentRebindingCallback = onComplete;
            
            // Start custom input listeners (scroll and mouse buttons)
            StartCustomInputListeners();

            var action = _actions[keybindName];
            
            // Debug: Log action binding structure
            Debug.Log($"[KeybindManager] Starting rebind for {keybindName}[{bindingIndex}], action: {action.name}, compositePart: {compositePart ?? "null"}");
            Debug.Log($"[KeybindManager] Action has {action.bindings.Count} total bindings:");
            for(var i = 0; i < action.bindings.Count; i++) {
                var binding = action.bindings[i];
                var effectivePath = binding.effectivePath ?? binding.path;
                Debug.Log($"  [{i}] path={binding.path}, effectivePath={effectivePath}, groups={binding.groups}, isComposite={binding.isComposite}, isPartOfComposite={binding.isPartOfComposite}, name={binding.name}");
            }
            
            var actualBindingIndex = GetActualBindingIndex(action, bindingIndex, compositePart);
            Debug.Log($"[KeybindManager] Calculated actualBindingIndex: {actualBindingIndex} for bindingIndex: {bindingIndex}");
            
            if(actualBindingIndex < 0 || actualBindingIndex >= action.bindings.Count) {
                Debug.LogError($"[KeybindManager] Binding index {actualBindingIndex} is out of range for action '{action.name}' with {action.bindings.Count} bindings");
                CleanupRebindingState();
                onComplete?.Invoke(null);
                return;
            }
            
            // Disable action before rebinding (required by Input System)
            var wasEnabled = action.enabled;
            if(wasEnabled) {
                action.Disable();
            }
            
            // Store action state for cleanup
            _currentRebindingAction = action;
            _currentRebindingWasEnabled = wasEnabled;
            
            // Start rebinding operation - exclude scroll to let our custom listener handle it
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
                    
                    Debug.Log($"[KeybindManager] Rebinding completed for {keybindName}[{bindingIndex}], actualIndex: {actualBindingIndex}, path: {bindingPath}");
                    
                    // Re-enable action if it was enabled before
                    if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                        _currentRebindingAction.Enable();
                    }
                    
                    // If Input System captured scroll, cancel it - our listener handles scroll
                    if(!string.IsNullOrEmpty(bindingPath) && (bindingPath.Contains("scroll") || bindingPath.Contains("Scroll"))) {
                        Debug.Log($"[KeybindManager] Scroll detected, canceling rebind");
                        CleanupRebindingState();
                        operation.Dispose();
                        onComplete?.Invoke(null); // Re-enable button, user can try again
                        return;
                    }
                    
                    // Normal binding captured - create a new InputBinding with the captured path
                    // This ensures we store the actual path that was captured, not the original binding
                    var capturedBinding = new InputBinding {
                        path = bindingPath,
                        overridePath = bindingPath
                    };
                    
                    CleanupRebindingState();
                    var displayString = GetBindingDisplayString(capturedBinding);
                    Debug.Log($"[KeybindManager] Storing binding: {keybindName}[{bindingIndex}] = {displayString} (path: {bindingPath})");
                    StorePendingBinding(keybindName, bindingIndex, capturedBinding);
                    onComplete?.Invoke(displayString);
                    operation.Dispose();
                })
                .OnCancel(operation => {
                    // Re-enable action if it was enabled before
                    if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                        _currentRebindingAction.Enable();
                    }
                    
                    CleanupRebindingState();
                    // ESC pressed - clear binding
                    StorePendingBinding(keybindName, bindingIndex, new InputBinding { overridePath = "" });
                    onComplete?.Invoke("None");
                    operation.Dispose();
                })
                .Start();
        }

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
            if(this == null) return; // MonoBehaviour might be destroyed
            
            if(_scrollListenerCoroutine != null) {
                try {
                    StopCoroutine(_scrollListenerCoroutine);
                } catch {
                    // Coroutine might already be stopped or MonoBehaviour destroyed
                }
                _scrollListenerCoroutine = null;
            }
            if(_mouseButtonListenerCoroutine != null) {
                try {
                    StopCoroutine(_mouseButtonListenerCoroutine);
                } catch {
                    // Coroutine might already be stopped or MonoBehaviour destroyed
                }
                _mouseButtonListenerCoroutine = null;
            }
            _currentRebindingKeybindName = null;
            _currentRebindingCallback = null;
            _currentRebindingOperation = null;
            _currentRebindingAction = null;
            _currentRebindingWasEnabled = false;
        }

        private int GetActualBindingIndex(InputAction action, int bindingIndex, string compositePart) {
            if(!string.IsNullOrEmpty(compositePart)) {
                // For composite bindings, find the nth occurrence of the composite part
                var occurrence = 0;
                for(var i = 0; i < action.bindings.Count; i++) {
                    var binding = action.bindings[i];
                    if(binding.isPartOfComposite && binding.name == compositePart) {
                        if(occurrence == bindingIndex) {
                            return i;
                        }
                        occurrence++;
                    }
                }
            } else {
                // For non-composite actions, find the nth non-composite binding
                // Prioritize Mouse and Keyboard bindings over other input devices
                var nonCompositeIndex = 0;
                var mouseKeyboardBindings = new List<int>();
                var otherBindings = new List<int>();
                
                // First pass: separate mouse/keyboard bindings from others
                for(var i = 0; i < action.bindings.Count; i++) {
                    var binding = action.bindings[i];
                    if(binding is { isComposite: false, isPartOfComposite: false }) {
                        // Check the ORIGINAL path (not effectivePath) to determine device type
                        // effectivePath includes overrides which can make a Gamepad binding look like Mouse/Keyboard
                        var path = binding.path;
                        
                        // Only classify as Mouse/Keyboard if the ORIGINAL path explicitly starts with <Mouse>/ or <Keyboard>/
                        var isMouseKeyboard = !string.IsNullOrEmpty(path) && 
                                             (path.StartsWith("<Mouse>/") || path.StartsWith("<Keyboard>/"));
                        
                        Debug.Log($"[KeybindManager] Binding [{i}]: path='{binding.path}', effectivePath='{binding.effectivePath}', isMouseKeyboard={isMouseKeyboard}");
                        
                        if(isMouseKeyboard) {
                            mouseKeyboardBindings.Add(i);
                        } else {
                            otherBindings.Add(i);
                        }
                    }
                }
                
                Debug.Log($"[KeybindManager] Prioritized bindings - Mouse/Keyboard: [{string.Join(", ", mouseKeyboardBindings)}], Others: [{string.Join(", ", otherBindings)}]");
                
                // Only use Mouse/Keyboard bindings for the index - don't mix with other device types
                // If we need more Mouse/Keyboard bindings than exist, create new ones
                if(bindingIndex < mouseKeyboardBindings.Count) {
                    var result = mouseKeyboardBindings[bindingIndex];
                    Debug.Log($"[KeybindManager] Found existing Mouse/Keyboard binding at index {bindingIndex}: actualIndex={result}");
                    return result;
                }
                
                // Need to add a new Mouse/Keyboard binding
                Debug.Log($"[KeybindManager] Need to add new Mouse/Keyboard binding for index {bindingIndex} (only {mouseKeyboardBindings.Count} exist)");
                if(mouseKeyboardBindings.Count > 0) {
                    // Copy from the first Mouse/Keyboard binding
                    var firstBinding = action.bindings[mouseKeyboardBindings[0]];
                    Debug.Log($"[KeybindManager] Adding new binding based on Mouse/Keyboard binding [{mouseKeyboardBindings[0]}]");
                    action.AddBinding(firstBinding.path, firstBinding.groups, firstBinding.interactions);
                    var newIndex = action.bindings.Count - 1;
                    Debug.Log($"[KeybindManager] Added new binding at index {newIndex}");
                    return newIndex;
                } else {
                    // No Mouse/Keyboard bindings exist - create one with a default path
                    Debug.LogWarning($"[KeybindManager] No Mouse/Keyboard bindings exist for {action.name}, cannot create new binding");
                    return -1;
                }
            }
            return -1;
        }

        private void StorePendingBinding(string keybindName, int bindingIndex, InputBinding binding) {
            // Safety check - if keybindName is null, rebinding state was cleaned up
            if(string.IsNullOrEmpty(keybindName)) {
                Debug.LogWarning("[KeybindManager] Attempted to store binding with null keybindName - rebinding state was cleaned up");
                return;
            }
            
            if(!_pendingBindings.ContainsKey(keybindName)) {
                _pendingBindings[keybindName] = new List<InputBinding>();
            }
            while(_pendingBindings[keybindName].Count <= bindingIndex) {
                _pendingBindings[keybindName].Add(new InputBinding { overridePath = "<UNSET>" });
            }
            _pendingBindings[keybindName][bindingIndex] = binding;
            
            // Store path string directly for all bindings (InputBinding.path is read-only)
            // This ensures we can save scroll bindings and cleared bindings correctly
            var path = binding.overridePath ?? binding.path;
            if(!_pendingBindingPaths.ContainsKey(keybindName)) {
                _pendingBindingPaths[keybindName] = new Dictionary<int, string>();
            }
            _pendingBindingPaths[keybindName][bindingIndex] = path ?? "";
            
            Debug.Log($"[KeybindManager] Stored pending binding: {keybindName}[{bindingIndex}] = path:'{binding.path}', overridePath:'{binding.overridePath}', storedPath:'{path}'");
        }

        private IEnumerator ListenForScrollInput() {
            var lastScrollValue = Vector2.zero;
            var scrollCooldown = 0f;
            
            while(_currentRebindingKeybindName != null) {
                if(!IsMouseOverScrollbar() && Mouse.current != null) {
                    var currentScroll = Mouse.current.scroll.value;
                    var scrollDelta = currentScroll - lastScrollValue;
                    
                    if(scrollCooldown <= 0f && scrollDelta.magnitude > 0.1f) {
                        // Scroll should be bindable even over UI elements - no check needed here
                        
                        string scrollPath;
                        string displayString;
                        
                        // Determine scroll direction
                        if(Mathf.Abs(scrollDelta.y) > Mathf.Abs(scrollDelta.x)) {
                            scrollPath = scrollDelta.y > 0 ? "SCROLL_UP" : "SCROLL_DOWN";
                            displayString = scrollDelta.y > 0 ? "Scroll Up" : "Scroll Down";
                        } else {
                            scrollPath = scrollDelta.x > 0 ? "SCROLL_RIGHT" : "SCROLL_LEFT";
                            displayString = scrollDelta.x > 0 ? "Scroll Right" : "Scroll Left";
                        }
                        
                        // Store state before cleanup
                        var keybindName = _currentRebindingKeybindName;
                        var bindingIndex = _currentRebindingIndex;
                        var callback = _currentRebindingCallback;
                        
                        // Cancel rebinding operation
                        if(_currentRebindingOperation != null) {
                            _currentRebindingOperation.Cancel();
                            _currentRebindingOperation = null;
                        }
                        
                        // Re-enable action if it was enabled before
                        if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                            _currentRebindingAction.Enable();
                        }
                        
                        // Store scroll binding - use overridePath since path is read-only
                        StorePendingBinding(keybindName, bindingIndex, new InputBinding { overridePath = scrollPath });
                        
                        // Cleanup state
                        CleanupRebindingState();
                        
                        // Invoke callback
                        callback?.Invoke(displayString);
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
                    // Check mouse 4
                    var mouse4Pressed = CheckMouseButton("xButton1", "forwardButton", ref mouse4WasPressed);
                    if(mouse4Pressed) {
                        HandleMouseButtonBinding("<Mouse>/xButton1", "Mouse 4");
                        yield break;
                    }
                    
                    // Check mouse 5
                    var mouse5Pressed = CheckMouseButton("xButton2", "backButton", ref mouse5WasPressed);
                    if(mouse5Pressed) {
                        HandleMouseButtonBinding("<Mouse>/xButton2", "Mouse 5");
                        yield break;
                    }
                }
                
                yield return null;
            }
        }

        private bool CheckMouseButton(string primaryControl, string fallbackControl, ref bool wasPressed) {
            if(Mouse.current == null) return false;
            
            var pressedThisFrame = false;

            // Check primary control
            try {
                if(Mouse.current[primaryControl] is ButtonControl { wasPressedThisFrame: true }) {
                    pressedThisFrame = true;
                }
            } catch {
                // Control doesn't exist on this mouse - ignore
            }
            
            // Check fallback control if primary didn't trigger
            if(!pressedThisFrame) {
                try {
                    if(Mouse.current[fallbackControl] is ButtonControl { wasPressedThisFrame: true }) {
                        pressedThisFrame = true;
                    }
                } catch {
                    // Control doesn't exist on this mouse - ignore
                }
            }
            
            if(pressedThisFrame && !wasPressed) {
                wasPressed = true;
                return true;
            }
            
            // Update state when released
            if(!pressedThisFrame) {
                try {
                    var primaryButton = Mouse.current[primaryControl] as ButtonControl;
                    var fallbackButton = Mouse.current[fallbackControl] as ButtonControl;
                    var isHeld = (primaryButton?.isPressed == true) || (fallbackButton?.isPressed == true);
                    if(!isHeld) {
                        wasPressed = false;
                    }
                } catch {
                    // Control doesn't exist on this mouse - ignore
                }
            }
            
            return false;
        }

        private void HandleMouseButtonBinding(string path, string displayName) {
            // Safety check - ensure rebinding state is still valid
            if(string.IsNullOrEmpty(_currentRebindingKeybindName)) {
                return;
            }
            
            // Store state before cleanup
            var keybindName = _currentRebindingKeybindName;
            var bindingIndex = _currentRebindingIndex;
            var callback = _currentRebindingCallback;
            
            // Cancel rebinding operation
            if(_currentRebindingOperation != null) {
                _currentRebindingOperation.Cancel();
                _currentRebindingOperation = null;
            }
            
            // Re-enable action if it was enabled before
            if(_currentRebindingWasEnabled && _currentRebindingAction != null) {
                _currentRebindingAction.Enable();
            }
            
            // Store binding
            StorePendingBinding(keybindName, bindingIndex, new InputBinding { overridePath = path });
            
            // Cleanup state
            CleanupRebindingState();
            
            // Invoke callback
            callback?.Invoke(displayName);
        }

        private bool IsMouseOverScrollbar() {
            if(Mouse.current == null) return false;
            
            var mousePos = Mouse.current.position.value;
            var uiDocuments = FindObjectsByType<UIDocument>(FindObjectsSortMode.None);
            
            foreach(var doc in uiDocuments) {
                if(doc.rootVisualElement == null) continue;
                
                var element = doc.rootVisualElement.panel.Pick(new Vector2(mousePos.x, Screen.height - mousePos.y));
                var current = element;
                while(current != null) {
                    if(current is ScrollView || 
                       current.name.Contains("ScrollView") || 
                       current.name.Contains("scroll") ||
                       current.ClassListContains("scroll")) {
                        return true;
                    }
                    current = current.parent;
                }
            }
            
            return false;
        }

        public string GetBindingDisplayString(string keybindName, int bindingIndex) {
            var key = $"Keybind_{keybindName}_{bindingIndex}";
            
            // Check PlayerPrefs first - this is the source of truth for scroll bindings
            var scrollBinding = GetScrollBindingFromPlayerPrefs(key);
            if(scrollBinding != null) {
                return scrollBinding;
            }
            
            if(!_keybindMap.ContainsKey(keybindName) || !_actions.TryGetValue(keybindName, out var action)) {
                return "None";
            }

            var (_, compositePart) = _keybindMap[keybindName];
            
            if(!string.IsNullOrEmpty(compositePart)) {
                // For composite bindings, find the nth occurrence
                var occurrence = 0;
                foreach(var binding in action.bindings) {
                    if(binding.isPartOfComposite && binding.name == compositePart) {
                        if(occurrence == bindingIndex) {
                            return GetBindingDisplayString(binding);
                        }

                        occurrence++;
                    }
                }
            } else {
                // For regular bindings - use same prioritization as GetActualBindingIndex
                // Only count Mouse/Keyboard bindings, ignore Gamepad/Touchscreen/etc
                var mouseKeyboardBindings = new List<int>();
                
                // First pass: find mouse/keyboard bindings only (by ORIGINAL path, not effectivePath)
                for(var i = 0; i < action.bindings.Count; i++) {
                    var binding = action.bindings[i];
                    if(binding is { isComposite: false, isPartOfComposite: false }) {
                        // Check the ORIGINAL path (not effectivePath) to determine device type
                        var path = binding.path;
                        
                        // Only classify as Mouse/Keyboard if the ORIGINAL path explicitly starts with <Mouse>/ or <Keyboard>/
                        var isMouseKeyboard = !string.IsNullOrEmpty(path) && 
                                             (path.StartsWith("<Mouse>/") || path.StartsWith("<Keyboard>/"));
                        
                        if(isMouseKeyboard) {
                            mouseKeyboardBindings.Add(i);
                        }
                    }
                }
                
                // Use the Mouse/Keyboard binding at the requested index
                if(bindingIndex < mouseKeyboardBindings.Count) {
                    var actualIndex = mouseKeyboardBindings[bindingIndex];
                    var binding = action.bindings[actualIndex];
                    return GetBindingDisplayString(binding);
                }
            }

            return "None";
        }

        private string GetScrollBindingFromPlayerPrefs(string key) {
            if(!PlayerPrefs.HasKey(key)) {
                return null;
            }
            
            var savedPath = PlayerPrefs.GetString(key);
            if(string.IsNullOrEmpty(savedPath)) {
                return "None";
            }
            
            // Scroll bindings are stored as "SCROLL_UP", "SCROLL_DOWN", etc.
            if(savedPath.StartsWith("SCROLL_")) {
                return GetBindingDisplayString(new InputBinding { path = savedPath });
            }
            
            return null;
        }

        private string GetBindingDisplayString(InputBinding binding) {
            var path = binding.effectivePath ?? binding.path;
            
            if(string.IsNullOrEmpty(path)) {
                return "None";
            }
            
            if(path == "<Invalid>/None" || path.Contains("<Invalid>")) {
                return "None";
            }

            switch(path) {
                // Handle special scroll bindings
                case "SCROLL_UP":
                    return "Scroll Up";
                case "SCROLL_DOWN":
                    return "Scroll Down";
                case "SCROLL_LEFT":
                    return "Scroll Left";
                case "SCROLL_RIGHT":
                    return "Scroll Right";
            }

            // Convert path to display string
            var control = InputSystem.FindControl(path);
            if(control != null) {
                var displayName = control.displayName;
                // Fix scroll controls showing "Up/Down"
                if((path.Contains("scroll") || path.Contains("Scroll")) && 
                   (displayName == "Up" || displayName == "Down" || displayName == "Left" || displayName == "Right")) {
                    return $"Scroll {displayName}";
                }
                return displayName;
            }
            
            // Fallback parsing
            if(path.StartsWith("<Keyboard>/")) {
                return path.Replace("<Keyboard>/", "").ToUpper();
            }
            if(path.StartsWith("<Mouse>/")) {
                var mousePath = path.Replace("<Mouse>/", "");
                switch(mousePath) {
                    case "leftButton":
                        return "Left Click";
                    case "rightButton":
                        return "Right Click";
                    case "middleButton":
                        return "Middle Click";
                    case "xButton1":
                    case "forwardButton":
                        return "Mouse 4";
                    case "xButton2":
                    case "backButton":
                        return "Mouse 5";
                }

                if(mousePath.Contains("scroll")) {
                    if(mousePath.Contains("up")) return "Scroll Up";
                    if(mousePath.Contains("down")) return "Scroll Down";
                    if(mousePath.Contains("left")) return "Scroll Left";
                    if(mousePath.Contains("right")) return "Scroll Right";
                }
                if(mousePath.StartsWith("xButton")) {
                    return $"Mouse {mousePath.Replace("xButton", "")}";
                }
                return mousePath;
            }
            
            return path;
        }

        public void SaveBindings() {
            foreach(var kvp in _pendingBindings) {
                var keybindName = kvp.Key;
                var bindings = kvp.Value;
                
                // Load existing bindings to preserve unchanged ones
                var existingBindings = new List<string>();
                for(var i = 0; i < 2; i++) {
                    var key = $"Keybind_{keybindName}_{i}";
                    existingBindings.Add(PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key) : "");
                }
                
                // Save only changed bindings
                for(var i = 0; i < bindings.Count; i++) {
                    var binding = bindings[i];
                    var key = $"Keybind_{keybindName}_{i}";
                    
                    // Check if we have a stored path string (for scroll bindings or cleared bindings)
                    string pathToSave;
                    if(_pendingBindingPaths.ContainsKey(keybindName) && _pendingBindingPaths[keybindName].ContainsKey(i)) {
                        pathToSave = _pendingBindingPaths[keybindName][i];
                        Debug.Log($"[KeybindManager] Saving {keybindName}[{i}] from _pendingBindingPaths: '{pathToSave}'");
                    } else {
                        // Get path from InputBinding
                        pathToSave = binding.overridePath ?? binding.path;
                        Debug.Log($"[KeybindManager] Saving {keybindName}[{i}] from InputBinding: path='{binding.path}', overridePath='{binding.overridePath}', final='{pathToSave}'");
                    }
                    
                    if(pathToSave == "<UNSET>") {
                        // Preserve existing
                        if(i < existingBindings.Count && !string.IsNullOrEmpty(existingBindings[i])) {
                            PlayerPrefs.SetString(key, existingBindings[i]);
                            Debug.Log($"[KeybindManager] Preserving existing binding for {keybindName}[{i}]: '{existingBindings[i]}'");
                        }
                        continue;
                    }
                    
                    // Save the path (empty string for cleared bindings, actual path for set bindings)
                    PlayerPrefs.SetString(key, string.IsNullOrEmpty(pathToSave) ? "" : pathToSave);
                    Debug.Log($"[KeybindManager] Saved {keybindName}[{i}] to PlayerPrefs: '{pathToSave}'");
                    
                    // Debug log for scroll bindings
                    if(!string.IsNullOrEmpty(pathToSave) && pathToSave.StartsWith("SCROLL_")) {
                        Debug.Log($"[KeybindManager] Saving scroll binding: {keybindName}[{i}] = {pathToSave}");
                    }
                }
            }
            
            PlayerPrefs.Save();
            ApplyPendingBindings();
            _pendingBindings.Clear();
            _pendingBindingPaths.Clear();
            LoadAllBindings();
        }

        public void CancelBindings() {
            // Cancel any active rebinding operation first
            CancelActiveRebinding();
            // Clear pending bindings
            _pendingBindings.Clear();
            _pendingBindingPaths.Clear();
        }

        public void CancelActiveRebinding() {
            // Cancel the rebinding operation if it's still running
            // Clear the callback first so OnCancel handler doesn't update the UI
            // We'll revert to PlayerPrefs values manually instead
            if(_currentRebindingOperation != null) {
                _currentRebindingCallback = null; // Prevent OnCancel from updating UI
                try {
                    _currentRebindingOperation.Cancel();
                    _currentRebindingOperation.Dispose();
                } catch {
                    // Operation might already be disposed or canceled
                }
                _currentRebindingOperation = null;
            }
            // Clean up listener coroutines
            if(this != null) {
                CleanupRebindingState();
            }
        }

        public bool HasPendingBindings() {
            // Check if there are any pending bindings that aren't just <UNSET>
            foreach(var kvp in _pendingBindings) {
                foreach(var binding in kvp.Value) {
                    var path = binding.overridePath ?? binding.path;
                    if(path != "<UNSET>" && !string.IsNullOrEmpty(path)) {
                        return true;
                    }
                }
            }
            // Also check _pendingBindingPaths (early exit if found)
            foreach(var kvp in _pendingBindingPaths) {
                foreach(var path in kvp.Value.Values) {
                    if(path != "<UNSET>" && !string.IsNullOrEmpty(path)) {
                        return true;
                    }
                }
            }
            return false;
        }

        private void ApplyPendingBindings() {
            foreach(var (keybindName, value) in _pendingBindings) {
                if(!_actions.TryGetValue(keybindName, out var action)) continue;

                var (_, compositePart) = _keybindMap[keybindName];
                
                for(var i = 0; i < value.Count; i++) {
                    var binding = value[i];
                    
                    // Skip unset bindings
                    if(binding.path == "<UNSET>") {
                        continue;
                    }
                    
                    // Get path from _pendingBindingPaths first (most accurate), then fall back to InputBinding
                    string bindingPath;
                    if(_pendingBindingPaths.ContainsKey(keybindName) && _pendingBindingPaths[keybindName].ContainsKey(i)) {
                        bindingPath = _pendingBindingPaths[keybindName][i];
                    } else {
                        bindingPath = binding.overridePath ?? binding.path;
                    }
                    
                    // Skip scroll bindings (they're saved to PlayerPrefs but not applied to Input System)
                    if(bindingPath.StartsWith("SCROLL_")) {
                        continue;
                    }
                    
                    if(string.IsNullOrEmpty(bindingPath)) {
                        bindingPath = null;
                    }
                    
                    Debug.Log($"[KeybindManager] Applying binding: {keybindName}[{i}] = {bindingPath ?? "null"}");
                    ApplyBindingToAction(action, compositePart, i, bindingPath);
                }
            }
        }

        private void ApplyBindingToAction(InputAction action, string compositePart, int bindingIndex, string bindingPath) {
            if(!string.IsNullOrEmpty(compositePart)) {
                // Composite binding
                var occurrence = 0;
                for(var j = 0; j < action.bindings.Count; j++) {
                    var binding = action.bindings[j];
                    if(binding.isPartOfComposite && binding.name == compositePart) {
                        if(occurrence == bindingIndex) {
                            try {
                                action.ApplyBindingOverride(j, bindingPath ?? "<Invalid>/None");
                            } catch(Exception e) {
                                Debug.LogWarning($"[KeybindManager] Failed to apply binding override for {action.name}[{j}]: {e.Message}");
                            }
                            break;
                        }
                        occurrence++;
                    }
                }
            } else {
                // Regular binding - use same prioritization logic as GetActualBindingIndex
                EnsureBindingExists(action, bindingIndex);
                
                // Prioritize Mouse and Keyboard bindings over other input devices
                var mouseKeyboardBindings = new List<int>();
                
                // First pass: find mouse/keyboard bindings only (by ORIGINAL path, not effectivePath)
                for(var j = 0; j < action.bindings.Count; j++) {
                    var binding = action.bindings[j];
                    if(binding is { isComposite: false, isPartOfComposite: false }) {
                        // Check the ORIGINAL path (not effectivePath) to determine device type
                        // This ensures Gamepad bindings with mouse overrides are NOT included
                        var path = binding.path;
                        
                        // Only classify as Mouse/Keyboard if the ORIGINAL path explicitly starts with <Mouse>/ or <Keyboard>/
                        var isMouseKeyboard = !string.IsNullOrEmpty(path) && 
                                             (path.StartsWith("<Mouse>/") || path.StartsWith("<Keyboard>/"));
                        
                        if(isMouseKeyboard) {
                            mouseKeyboardBindings.Add(j);
                        }
                    }
                }
                
                Debug.Log($"[KeybindManager] ApplyBindingToAction: Found {mouseKeyboardBindings.Count} Mouse/Keyboard bindings for {action.name}, applying to index {bindingIndex}");
                
                // Only use Mouse/Keyboard bindings for the index - don't mix with other device types
                if(bindingIndex < mouseKeyboardBindings.Count) {
                    var actualIndex = mouseKeyboardBindings[bindingIndex];
                    Debug.Log($"[KeybindManager] Applying to actual binding index {actualIndex} (Mouse/Keyboard binding #{bindingIndex})");
                    try {
                        if(bindingPath == null) {
                            action.RemoveBindingOverride(actualIndex);
                            Debug.Log($"[KeybindManager] Removed binding override at index {actualIndex}");
                        } else {
                            action.ApplyBindingOverride(actualIndex, bindingPath);
                            Debug.Log($"[KeybindManager] Applied binding override at index {actualIndex}: {bindingPath}");
                        }
                    } catch(Exception e) {
                        Debug.LogWarning($"[KeybindManager] Failed to apply binding override for {action.name}[{actualIndex}]: {e.Message}");
                    }
                } else {
                    Debug.LogWarning($"[KeybindManager] Cannot apply binding for {action.name}[{bindingIndex}] - only {mouseKeyboardBindings.Count} Mouse/Keyboard bindings exist. Need to create new binding.");
                    // Try to create the binding if possible
                    if(mouseKeyboardBindings.Count > 0 && bindingPath != null) {
                        var firstBinding = action.bindings[mouseKeyboardBindings[0]];
                        action.AddBinding(firstBinding.path, firstBinding.groups, firstBinding.interactions);
                        var newIndex = action.bindings.Count - 1;
                        action.ApplyBindingOverride(newIndex, bindingPath);
                        Debug.Log($"[KeybindManager] Created and applied new binding at index {newIndex}: {bindingPath}");
                    }
                }
            }
        }

        private void EnsureBindingExists(InputAction action, int bindingIndex) {
            // Count only Mouse/Keyboard non-composite bindings
            var mouseKeyboardBindings = new List<int>();
            for(var i = 0; i < action.bindings.Count; i++) {
                var binding = action.bindings[i];
                if(binding is { isComposite: false, isPartOfComposite: false }) {
                    var path = binding.path;
                    if(!string.IsNullOrEmpty(path) && 
                       (path.StartsWith("<Mouse>/") || path.StartsWith("<Keyboard>/"))) {
                        mouseKeyboardBindings.Add(i);
                    }
                }
            }

            // If we need more Mouse/Keyboard bindings, create them based on the first one
            if(bindingIndex >= mouseKeyboardBindings.Count && mouseKeyboardBindings.Count > 0) {
                var firstBinding = action.bindings[mouseKeyboardBindings[0]];
                while(mouseKeyboardBindings.Count <= bindingIndex) {
                    action.AddBinding(firstBinding.path, firstBinding.groups, firstBinding.interactions);
                    mouseKeyboardBindings.Add(action.bindings.Count - 1);
                }
            }
        }

        private void SetDefaultBindingsIfNeeded() {
            var defaults = new Dictionary<string, string[]> {
                { "forward", new[] { "<Keyboard>/w", "" } },
                { "back", new[] { "<Keyboard>/s", "" } },
                { "left", new[] { "<Keyboard>/a", "" } },
                { "right", new[] { "<Keyboard>/d", "" } },
                { "reload", new[] { "<Keyboard>/r", "" } },
                { "grapple", new[] { "<Keyboard>/q", "" } },
                { "jump", new[] { "<Keyboard>/space", "SCROLL_DOWN" } },
                { "shoot", new[] { "<Mouse>/leftButton", "<Mouse>/rightButton" } },
                { "primary", new[] { "<Keyboard>/1", "" } },
                { "secondary", new[] { "<Keyboard>/2", "" } },
                { "nextweapon", new[] { "", "" } },
                { "previousweapon", new[] { "", "" } }
            };
            
            // Check if any bindings exist
            var hasAnySavedBindings = false;
            foreach(var kvp in _keybindMap) {
                if(PlayerPrefs.HasKey($"Keybind_{kvp.Key}_0")) {
                    hasAnySavedBindings = true;
                    break;
                }
            }
            
            if(!hasAnySavedBindings) {
                foreach(var kvp in defaults) {
                    for(var i = 0; i < kvp.Value.Length; i++) {
                        if(!string.IsNullOrEmpty(kvp.Value[i])) {
                            PlayerPrefs.SetString($"Keybind_{kvp.Key}_{i}", kvp.Value[i]);
                        }
                    }
                }
                PlayerPrefs.Save();
            }
        }

        private void LoadAllBindings() {
            foreach(var kvp in _keybindMap) {
                var keybindName = kvp.Key;
                var bindings = new List<string>();
                
                var hasAnyKeys = false;
                for(var i = 0; i < 2; i++) {
                    var key = $"Keybind_{keybindName}_{i}";
                    if(PlayerPrefs.HasKey(key)) {
                        bindings.Add(PlayerPrefs.GetString(key));
                        hasAnyKeys = true;
                    } else {
                        bindings.Add("");
                    }
                }
                
                if(hasAnyKeys) {
                    ApplySavedBindings(keybindName, bindings);
                }
            }
        }

        private void ApplySavedBindings(string keybindName, List<string> paths) {
            if(!_actions.TryGetValue(keybindName, out var action)) return;

            var (_, compositePart) = _keybindMap[keybindName];
            
            for(var i = 0; i < paths.Count; i++) {
                var path = paths[i];
                
                // Skip scroll bindings - they're handled via PlayerPrefs
                if(path.StartsWith("SCROLL_")) {
                    continue;
                }
                
                // Ensure binding exists for non-composite actions
                if(string.IsNullOrEmpty(compositePart)) {
                    EnsureBindingExists(action, i);
                }
                
                ApplyBindingToAction(action, compositePart, i, string.IsNullOrEmpty(path) ? null : path);
            }
        }

        public bool IsKeyPressed(string keybindName, int bindingIndex = 0) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].IsPressed();
        }

        public bool WasKeyPressedThisFrame(string keybindName, int bindingIndex = 0) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].WasPressedThisFrame();
        }

        public bool WasKeyReleasedThisFrame(string keybindName, int bindingIndex = 0) {
            return _actions.ContainsKey(keybindName) && _actions[keybindName].WasReleasedThisFrame();
        }
    }
}
