using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Game.Player {
    /// <summary>
    /// Handles player statistics tracking including velocity and ping.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerStatsController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        [Header("Velocity Tracking")]
        [SerializeField] private float velocitySampleInterval = 0.1f;

        // Network variables (moved from PlayerController)
        public NetworkVariable<float> averageVelocity = new();
        public NetworkVariable<int> pingMs = new();

        // Private fields for velocity tracking
        private float _totalVelocitySampled;
        private int _velocitySampleCount;
        private float _velSampleAccum;
        private int _velSampleCount;
        private float _velSampleTimer;

        // Timer for periodic updates
        private float _timer;

        private void Awake() {
            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }
        }

        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            // Component reference should be assigned in the inspector
            // Only use GetComponent as a last resort fallback if not assigned
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }
        }

        private void Update() {
            if(!IsServer) return;

            // Update ping every second
            _timer += Time.deltaTime;
            if(!(_timer >= 1f)) return;
            _timer = 0f;
            UpdatePing();
        }

        /// <summary>
        /// Called by PlayerController to track velocity.
        /// Should be called every frame when the player is moving.
        /// </summary>
        public void TrackVelocity() {
            if(!IsOwner || playerController == null) return;

            // Get velocity from PlayerController
            var velocity = playerController.GetFullVelocity;
            var speed = velocity.sqrMagnitude;
            // Only track if moving at walk speed or faster
            const float walkSpeed = 5f;
            if(speed >= walkSpeed * walkSpeed) {
                _velSampleAccum += Mathf.Sqrt(speed);
                _velSampleCount++;
            }

            _velSampleTimer += Time.deltaTime;
            if(!(_velSampleTimer >= velocitySampleInterval) || _velSampleCount <= 0) return;
            var avg = _velSampleAccum / _velSampleCount;
            SubmitVelocitySampleServerRpc(avg);
            _velSampleTimer = 0f;
            _velSampleAccum = 0f;
            _velSampleCount = 0;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
        private void SubmitVelocitySampleServerRpc(float speed) {
            _totalVelocitySampled += speed;
            _velocitySampleCount++;
            averageVelocity.Value = _totalVelocitySampled / _velocitySampleCount;
        }

        private void UpdatePing() {
            if(!IsServer) return;

            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            if(!transport) return;

            var rtt = transport.GetCurrentRtt(OwnerClientId);
            pingMs.Value = (int)rtt;
        }
    }
}