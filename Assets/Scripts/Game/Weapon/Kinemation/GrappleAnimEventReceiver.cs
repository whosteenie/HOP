using Network.Events;
using UnityEngine;

namespace Game.Weapon.Kinemation {
    /// <summary>
    /// Receives animation events from the grapple clip.
    /// Added at runtime to the FP viewmodel so it's in the Animator's hierarchy.
    /// </summary>
    public class GrappleAnimEventReceiver : MonoBehaviour {
        /// <summary>
        /// Called from grapple animation event when the first frame is reached.
        /// </summary>
        public void OnGrappleFirstFrame() {
            EventBus.Publish(new GrappleAnimFirstFrameEvent());
        }

        /// <summary>
        /// Called from grapple animation event when the hand returns.
        /// Supports both OnHideGrapple and HideGrapple as anim event names.
        /// </summary>
        public void OnHideGrapple() => EventBus.Publish(new GrappleAnimHideEvent());
        public void HideGrapple() => EventBus.Publish(new GrappleAnimHideEvent());
    }
}
