using UnityEngine;

public class PlayerAnimationEvents : MonoBehaviour
{
    public FpController fpController;
    
    private void OnValidate() {
        if(fpController == null) {
            fpController = transform.parent.GetComponent<FpController>();
        }
    }
    
    public void PlayWalkSound() {
        fpController.PlayWalkSound();
    }

    public void PlayRunSound() {
        fpController.PlayRunSound();
    }
}
