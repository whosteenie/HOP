using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Core {
    /// <summary>
    /// Centralized host for managing modal dialogs. Supports dynamic instantiation
    /// from UXML templates and provides a consistent API for showing/hiding modals.
    /// </summary>
    public class UIModalHost {
        private readonly VisualElement _root;
        private readonly Dictionary<string, VisualElement> _activeModals;
        private readonly Stack<VisualElement> _modalStack;

        public UIModalHost(VisualElement root) {
            _root = root;
            _activeModals = new Dictionary<string, VisualElement>();
            _modalStack = new Stack<VisualElement>();
        }

        /// <summary>
        /// Shows a confirmation modal with Yes/No buttons.
        /// </summary>
        /// <param name="template">UXML template for the modal (must contain modal-root, modal-title, modal-message, modal-yes, modal-no)</param>
        /// <param name="title">Title text to display</param>
        /// <param name="message">Message text to display</param>
        /// <param name="onYes">Callback when Yes is clicked</param>
        /// <param name="onNo">Callback when No is clicked (optional)</param>
        /// <param name="yesText">Text for Yes button (default: "Yes")</param>
        /// <param name="noText">Text for No button (default: "No")</param>
        /// <param name="modalId">Unique identifier for this modal instance (auto-generated if not provided)</param>
        /// <returns>The created modal VisualElement</returns>
        public VisualElement ShowConfirmation(
            VisualTreeAsset template,
            string title,
            string message,
            Action onYes,
            Action onNo = null,
            string yesText = "Yes",
            string noText = "No",
            string modalId = null
        ) {
            if(template == null) {
                Debug.LogError("[UIModalHost] Cannot show confirmation modal: template is null");
                return null;
            }

            var modalIdFinal = modalId ?? Guid.NewGuid().ToString();
            var modalRoot = template.CloneTree();
            var modalContainer = modalRoot.Q<VisualElement>("modal-root");
            
            if(modalContainer == null) {
                Debug.LogError("[UIModalHost] Modal template must contain 'modal-root' element");
                return null;
            }

            var titleLabel = modalContainer.Q<Label>("modal-title");
            var messageLabel = modalContainer.Q<Label>("modal-message");
            var yesButton = modalContainer.Q<Button>("modal-yes");
            var noButton = modalContainer.Q<Button>("modal-no");

            if(titleLabel != null) titleLabel.text = title;
            if(messageLabel != null) messageLabel.text = message;
            if(yesButton != null) {
                yesButton.text = yesText;
                yesButton.clicked += () => {
                    HideModal(modalIdFinal);
                    onYes?.Invoke();
                };
                UISound.RegisterButtonHover(yesButton);
            }
            if(noButton != null) {
                noButton.text = noText;
                noButton.clicked += () => {
                    HideModal(modalIdFinal);
                    onNo?.Invoke();
                };
                UISound.RegisterButtonHover(noButton);
            }

            _root.Add(modalContainer);
            _activeModals[modalIdFinal] = modalContainer;
            _modalStack.Push(modalContainer);

            ShowModal(modalContainer);
            return modalContainer;
        }

        /// <summary>
        /// Shows an existing modal element (already in the visual tree).
        /// </summary>
        public void ShowExistingModal(VisualElement modal, string modalId = null) {
            if(modal == null) return;

            var modalIdFinal = modalId ?? Guid.NewGuid().ToString();
            _activeModals.TryAdd(modalIdFinal, modal);
            _modalStack.Push(modal);
            ShowModal(modal);
        }

        /// <summary>
        /// Hides a modal by ID.
        /// </summary>
        public void HideModal(string modalId) {
            if(!_activeModals.TryGetValue(modalId, out var modal)) return;
            HideModal(modal);
            _activeModals.Remove(modalId);
            
            if(_modalStack.Count > 0 && _modalStack.Peek() == modal) {
                _modalStack.Pop();
            }
        }

        private static void ShowModal(VisualElement modal) {
            if(modal == null) return;
            modal.RemoveFromClassList("hidden");
            modal.style.display = DisplayStyle.Flex;
            modal.BringToFront();
        }

        private static void HideModal(VisualElement modal) {
            if(modal == null) return;
            modal.AddToClassList("hidden");
            modal.style.display = StyleKeyword.Null;
        }
    }
}
