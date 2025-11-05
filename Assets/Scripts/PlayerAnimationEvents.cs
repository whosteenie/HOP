using UnityEngine;
using UnityEngine.Serialization;

public class PlayerAnimationEvents : MonoBehaviour
{
    [FormerlySerializedAs("fpController")] public PlayerController playerController;
    
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
