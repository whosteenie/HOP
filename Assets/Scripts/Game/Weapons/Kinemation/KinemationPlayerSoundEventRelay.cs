using UnityEngine;

namespace Game.Weapons {
    /// <summary>
    /// No-op receiver for KINEMATION player sound animation events when internal player sounds are disabled.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class KinemationPlayerSoundEventRelay : MonoBehaviour {
        public void PlayAimSound() {
        }

        public void PlayAimSound(bool isAimIn) {
        }

        public void PlayFireModeSwitchSound() {
        }

        public void PlayEquipSound() {
        }

        public void PlayUnEquipSound() {
        }

        public void PlayWalkSound() {
        }

        public void PlaySprintSound() {
        }

        public void PlayJumpSound() {
        }

        public void PlayLandSound() {
        }
    }
}
