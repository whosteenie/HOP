using Game.Player.Contracts;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Movement {
    /// <summary>
    /// Shelved movement prototype.
    /// Not part of current production gameplay paths; retained for future experimentation.
    /// </summary>
    [AddComponentMenu("Shelved/Movement/Dash Controller (Unused Prototype)")]
    public class DashController : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private MonoBehaviour playerContextSource;

        private IPlayerMovementContext _playerContext;
        private PlayerMovementController _movementController;

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

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve<IPlayerMovementContext>(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[DashController] IPlayerMovementContext not found!");
                enabled = false;
                return;
            }

            if(_movementController == null) {
                _movementController = GetComponent<PlayerMovementController>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();
            _netIsDashing.OnValueChanged += OnDashChanged;
        }

        public override void OnNetworkDespawn() {
            _netIsDashing.OnValueChanged -= OnDashChanged;
        }

        /// <summary>
        /// Starts the dash on the server.
        /// </summary>
        [Rpc(SendTo.Server)]
        private void StartDashRpc(Vector3 direction) {
            if(_netIsDashing.Value || _dashCooldownTimer > 0) return;

            _netIsDashing.Value = true;
            _dashVelocity = direction * dashSpeed;
            _dashTimer = 0f;
            _airDashPendingGround = !_playerContext.IsGrounded;
        }

        private void OnDashChanged(bool _, bool dashing) {
            IsDashing = dashing;
        }

        private void TryDash(Vector2 moveInput) {
            if(!IsOwner || IsDashing || _dashCooldownTimer > 0) return;

            if(moveInput.sqrMagnitude < 0.1f) return;

            var playerTransform = transform;
            var dashDir = (playerTransform.forward * moveInput.y + playerTransform.right * moveInput.x).normalized;
            dashDir.y = 0f;

            StartDashRpc(dashDir);
        }

        private void Update() {
            if(!IsOwner) return;

            _dashCooldownTimer = Mathf.Max(0, _dashCooldownTimer - Time.deltaTime);

            if(_airDashPendingGround && _playerContext.IsGrounded) {
                _airDashPendingGround = false;
                _dashCooldownTimer = dashCooldown;
            }

            var currentVel = _playerContext.FullVelocity;
            var dashDir = _dashVelocity.normalized;
            var boostedVel = currentVel + dashDir * dashSpeed;

            boostedVel.y = currentVel.y; // Keep vertical

            characterController.Move(boostedVel * Time.deltaTime);

            if(!(_dashTimer >= dashDuration)) return;
            if(_movementController != null) _movementController.SetVelocity(new Vector3(boostedVel.x, 0f, boostedVel.z));

            EndDash();
        }

        private void EndDash() {
            IsDashing = false;
            _netIsDashing.Value = false;

            if(!_airDashPendingGround) {
                _dashCooldownTimer = dashCooldown;
            }
        }

        /// <summary>
        /// Called when the dash input is received.
        /// </summary>
        public void OnDashInput() {
            TryDash(_playerContext.MoveInput);
        }
    }
}
