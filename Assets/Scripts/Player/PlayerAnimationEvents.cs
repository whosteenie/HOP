using UnityEngine;

namespace Player {
    public class PlayerAnimationEvents : MonoBehaviour
    {
        public PlayerController playerController;
    
        private void OnValidate() {
            if(playerController == null) {
                playerController = transform.parent.GetComponent<PlayerController>();
            }
        }
    
        public void PlayWalkSound() {
            playerController.PlayWalkSound();
        }

        public void PlayRunSound() {
            playerController.PlayRunSound();
        }
    }
}
