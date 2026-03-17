using Game.Player.Contracts;
using Network.Core;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Game.Player.Combat {
    /// <summary>
    /// Handles player statistics tracking including velocity and ping.
    /// </summary>
    [DefaultExecutionOrder(-90)] // Initialize after PlayerController
    public class PlayerStatsController : NetworkBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerStatsContext _playerContext;

        [Header("Velocity Tracking")]
        [SerializeField] private float velocitySampleInterval = 0.1f;

        private static readonly NetworkVariable<float> MissingAverageVelocityState = new();
        private static readonly NetworkVariable<int> MissingPingState = new();

        public NetworkVariable<float> AverageVelocity {
            get {
                if(_playerContext == null) return MissingAverageVelocityState;
                var playerState = _playerContext.PlayerState;
                return playerState != null ? playerState.averageVelocity : MissingAverageVelocityState;
            }
        }

        public NetworkVariable<int> PingMs {
            get {
                if(_playerContext == null) return MissingPingState;
                var playerState = _playerContext.PlayerState;
                return playerState != null ? playerState.pingMs : MissingPingState;
            }
        }

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
            if(PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) return;
            Debug.LogError("[PlayerStatsController] IPlayerStatsContext not found!");
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
            if(!NetworkAuthority.HasGlobalAuthority(this) || _playerContext == null || AverageVelocity == null) return;

            var speed = _playerContext.ObservedServerMovementSpeed;
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
            AverageVelocity.Value = _totalVelocitySampled / _velocitySampleCount;
            _velSampleTimer = 0f;
            _velSampleAccum = 0f;
            _velSampleCount = 0;
        }

        /// <summary>
        /// Updates the player's ping based on the network transport.
        /// </summary>
        private void UpdatePing() {
            if(!NetworkAuthority.HasGlobalAuthority(this) || _playerContext == null || PingMs == null) return;

            var networkManager = NetworkManager;
            if(networkManager == null || !networkManager.IsListening) {
                return;
            }

            var transport = networkManager.NetworkConfig.NetworkTransport as UnityTransport;
            if(!transport) return;

            var rtt = transport.GetCurrentRtt(_playerContext.OwnerClientId);
            PingMs.Value = (int)rtt;
        }
    }
}

