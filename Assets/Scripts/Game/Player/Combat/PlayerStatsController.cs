using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using Network.Core;

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

        private static readonly NetworkVariable<float> MissingAverageVelocityState = new(0f);
        private static readonly NetworkVariable<int> MissingPingState = new(0);

        public NetworkVariable<float> averageVelocity =>
            playerController != null ? playerController.PlayerState?.averageVelocity ?? MissingAverageVelocityState : MissingAverageVelocityState;
        public NetworkVariable<int> pingMs =>
            playerController != null ? playerController.PlayerState?.pingMs ?? MissingPingState : MissingPingState;

        // Private fields for velocity tracking
        private float _totalVelocitySampled;
        private int _velocitySampleCount;
        private float _velSampleAccum;
        private int _velSampleCount;
        private float _velSampleTimer;

        // Timer for periodic updates
        private float _timer;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController != null) return;
            Debug.LogError("[PlayerStatsController] PlayerController not found!");
            enabled = false;
        }

        private void Update() {
            if(!NetworkAuthority.HasGlobalAuthority(this)) return;

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
        public void UpdateAuthorityStats() {
            if(!NetworkAuthority.HasGlobalAuthority(this) || playerController == null || averageVelocity == null) return;

            var speed = playerController.ObservedServerMovementSpeed;
            const float walkSpeed = 5f;
            if(speed >= walkSpeed) {
                _velSampleAccum += speed;
                _velSampleCount++;
            }

            _velSampleTimer += Time.deltaTime;
            if(!(_velSampleTimer >= velocitySampleInterval) || _velSampleCount <= 0) return;
            var avg = _velSampleAccum / _velSampleCount;
            _totalVelocitySampled += avg;
            _velocitySampleCount++;
            averageVelocity.Value = _totalVelocitySampled / _velocitySampleCount;
            _velSampleTimer = 0f;
            _velSampleAccum = 0f;
            _velSampleCount = 0;
        }

        /// <summary>
        /// Updates the player's ping based on the network transport.
        /// </summary>
        private void UpdatePing() {
            if(!NetworkAuthority.HasGlobalAuthority(this) || playerController == null || pingMs == null) return;

            var networkManager = NetworkManager;
            if(networkManager == null || !networkManager.IsListening) {
                return;
            }

            var transport = networkManager.NetworkConfig.NetworkTransport as UnityTransport;
            if(!transport) return;

            var rtt = transport.GetCurrentRtt(playerController.OwnerClientId);
            pingMs.Value = (int)rtt;
        }
    }
}
