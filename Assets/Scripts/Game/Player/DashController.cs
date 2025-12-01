using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class DashController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private PlayerController playerController;

        [SerializeField] private CharacterController characterController;
        [SerializeField] private SwingGrapple swingGrapple;

        [Header("Dash Settings")] [SerializeField] [Range(8f, 15f)]
        private float dashSpeed = 2f;

        [SerializeField] private float dashDuration = 0.05f;
        [SerializeField] private float dashCooldown = 1.5f;

        private bool IsDashing { get; set; }

        private readonly NetworkVariable<bool> _netIsDashing = new();
        private float _dashTimer;
        private Vector3 _dashVelocity;
        private float _dashCooldownTimer;
        private bool _airDashPendingGround;

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _netIsDashing.OnValueChanged += OnDashChanged;
        }

        public override void OnNetworkDespawn() {
            _netIsDashing.OnValueChanged -= OnDashChanged;
        }

        [Rpc(SendTo.Server)]
        private void StartDashRpc(Vector3 direction) {
            if(_netIsDashing.Value || _dashCooldownTimer > 0) return;

            _netIsDashing.Value = true;
            _dashVelocity = direction * dashSpeed;
            _dashTimer = 0f;
            _airDashPendingGround = !playerController.IsGrounded;
        }

        private void OnDashChanged(bool _, bool dashing) {
            IsDashing = dashing;
        }

        private void TryDash(Vector2 moveInput) {
            if(!IsOwner || IsDashing || _dashCooldownTimer > 0) return;

            if(moveInput.sqrMagnitude < 0.1f) return;

            var dashDir = (transform.forward * moveInput.y + transform.right * moveInput.x).normalized;
            dashDir.y = 0f;

            StartDashRpc(dashDir);
        }

        private void Update() {
            if(!IsOwner) return;

            _dashCooldownTimer = Mathf.Max(0, _dashCooldownTimer - Time.deltaTime);

            if(_airDashPendingGround && playerController.IsGrounded) {
                _airDashPendingGround = false;
                _dashCooldownTimer = dashCooldown;
            }

            if(!IsDashing) return;
            _dashTimer += Time.deltaTime;

            // **ADDITIVE: Current momentum + dash boost in input direction**
            var currentVel = playerController.GetFullVelocity;
            var dashDir = _dashVelocity.normalized; // Direction only
            var boostedVel = currentVel + dashDir * dashSpeed;

            boostedVel.y = currentVel.y; // Keep vertical

            characterController.Move(boostedVel * Time.deltaTime);

            if(!(_dashTimer >= dashDuration)) return;
            // **PRESERVE boosted momentum** - let friction/air strafe control it
            playerController.SetVelocity(new Vector3(boostedVel.x, 0f, boostedVel.z));
            EndDash();
        }

        private void EndDash() {
            IsDashing = false;
            _netIsDashing.Value = false;

            if(!_airDashPendingGround) {
                _dashCooldownTimer = dashCooldown;
            }
        }

        public void OnDashInput() {
            TryDash(playerController.moveInput);
        }
    }
}