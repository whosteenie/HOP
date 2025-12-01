using Game.Audio;
using Network.Events;
using UnityEngine.UIElements;

namespace Network.Services {
    /// <summary>
    /// Centralized service for UI sound effects.
    /// Provides consistent sound playing across all menu managers.
    /// </summary>
    public static class UISoundService {
        /// <summary>
        /// Plays a button click sound (normal or back button).
        /// </summary>
        /// <param name="isBack">If true, plays back button sound; otherwise plays normal click sound.</param>
        public static void PlayButtonClick(bool isBack = false) {
            var soundKey = !isBack ? SfxKey.ButtonClick : SfxKey.BackButton;
            EventBus.Publish(new PlayUISoundEvent(soundKey));
        }

        /// <summary>
        /// Plays a button hover sound.
        /// Uses MouseEnterEvent which only fires when entering the element, preventing multiple triggers from child elements.
        /// </summary>
        public static void PlayButtonHover() {
            EventBus.Publish(new PlayUISoundEvent(SfxKey.ButtonHover));
        }

        /// <summary>
        /// Registers hover sound callback on a button.
        /// </summary>
        public static void RegisterButtonHover(Button button) {
            if(button == null) return;
            button.UnregisterCallback<MouseEnterEvent>(OnMouseEnter);
            button.RegisterCallback<MouseEnterEvent>(OnMouseEnter);
        }

        /// <summary>
        /// Unregisters hover sound callback from a button.
        /// </summary>
        public static void UnregisterButtonHover(Button button) {
            if(button == null) return;
            button.UnregisterCallback<MouseEnterEvent>(OnMouseEnter);
        }

        private static void OnMouseEnter(MouseEnterEvent evt) {
            PlayButtonHover();
        }
    }
}
