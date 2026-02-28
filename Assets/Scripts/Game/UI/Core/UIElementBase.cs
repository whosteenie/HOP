using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    /// <summary>
    /// Base class for UI modules that standardizes initialization, binding, lifecycle, and cleanup.
    /// Provides automatic validation of required UI elements and safe event handler management.
    /// </summary>
    public abstract class UIElementBase : MonoBehaviour {
        [Header("UI References")]
        [SerializeField] public UIDocument uiDocument;

        protected VisualElement Root { get; private set; }
        protected bool IsInitialized { get; private set; }

        private List<System.Action> _cleanupActions = new();
        private bool _cleanupInvoked;

        #region Unity Lifecycle

        protected virtual void Awake() {
            if(uiDocument == null) {
                Debug.LogError($"[{GetType().Name}] UIDocument is not assigned!");
            }
        }

        protected virtual void Start() {
            if(!TryBindRoot()) {
                Debug.LogError($"[{GetType().Name}] Failed to get root visual element from UIDocument!");
                return;
            }
            Initialize();
        }

        protected virtual void OnEnable() {
        }

        protected virtual void OnDisable() {
        }

        protected virtual void OnDestroy() {
            Cleanup();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the UI element. Called automatically in Start().
        /// Override OnInitialize() to perform custom initialization logic.
        /// </summary>
        protected void Initialize() {
            if(IsInitialized) return;
            if(Root == null) {
                TryBindRoot();
            }
            if(Root == null) {
                Debug.LogError($"[{GetType().Name}] Cannot initialize: Root is null");
                return;
            }

            var missingElements = ValidateRequiredElements();
            if(missingElements.Count > 0) {
                Debug.LogError($"[{GetType().Name}] Missing required UI elements: {string.Join(", ", missingElements)}");
                return;
            }

            OnInitialize();
            IsInitialized = true;
        }

        public void Initialize(VisualElement root) {
            if(IsInitialized) return;
            Root = root;
            Initialize();
        }

        private bool TryBindRoot() {
            if(Root != null) return true;
            if(uiDocument == null) return false;

            Root = uiDocument.rootVisualElement;
            return Root != null;
        }

        /// <summary>
        /// Override this method to perform initialization logic after required elements are validated.
        /// </summary>
        protected virtual void OnInitialize() {
        }

        /// <summary>
        /// Override this method to define which UI elements are required for this module.
        /// Return a dictionary mapping element names to their expected types.
        /// </summary>
        protected virtual Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type>();
        }

        /// <summary>
        /// Validates that all required elements exist in the UI tree.
        /// </summary>
        private List<string> ValidateRequiredElements() {
            var required = GetRequiredElements();
            if(required == null || required.Count == 0) return new List<string>();

            return UIBindingHelper.ValidateRequiredElements(Root, required, GetType().Name);
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Registers a cleanup action to be executed when the element is destroyed.
        /// Useful for unregistering event handlers, stopping coroutines, etc.
        /// </summary>
        protected void RegisterCleanup(System.Action cleanupAction) {
            if(cleanupAction == null) return;
            _cleanupActions ??= new List<System.Action>();
            _cleanupActions.Add(cleanupAction);
        }

        /// <summary>
        /// Performs cleanup of registered actions. Called automatically in OnDestroy().
        /// Override OnCleanup() to add custom cleanup logic.
        /// </summary>
        private void Cleanup() {
            if(_cleanupInvoked) return;
            _cleanupInvoked = true;

            try {
                OnCleanup();
            } catch(System.Exception ex) {
                Debug.LogError($"[{GetType().Name}] Error during OnCleanup: {ex}");
            }

            if(_cleanupActions == null || _cleanupActions.Count == 0) return;

            var cleanupSnapshot = _cleanupActions.ToArray();
            foreach(var action in cleanupSnapshot) {
                try {
                    action?.Invoke();
                } catch(System.Exception ex) {
                    Debug.LogError($"[{GetType().Name}] Error during cleanup action: {ex}");
                }
            }

            _cleanupActions.Clear();
        }

        /// <summary>
        /// Override this method to perform custom cleanup logic.
        /// </summary>
        protected virtual void OnCleanup() {
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Queries a required UI element and logs an error if missing.
        /// </summary>
        protected T QRequired<T>(string elementName) where T : VisualElement {
            return UIBindingHelper.QRequired<T>(Root, elementName, GetType().Name);
        }

        /// <summary>
        /// Queries an optional UI element (returns null if missing, no error logged).
        /// </summary>
        protected T QOptional<T>(string elementName) where T : VisualElement {
            return Root?.Q<T>(elementName);
        }

        #endregion
    }
}
