using UnityEngine;

namespace Game.Player.Contracts {
    public interface IPlayerRagdollContext {
        CharacterController CharacterController { get; }
        Animator PlayerAnimator { get; }
    }
}
