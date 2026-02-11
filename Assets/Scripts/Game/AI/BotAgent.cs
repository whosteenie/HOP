using System.Collections.Generic;
using Game.Player;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace Game.AI {
    /// <summary>
    /// ML-Agents bot that learns movement through imitation (GAIL).
    /// Observations: position, velocity, rotation (yaw + pitch), grapple state, grapple targets (17 total).
    /// Actions: movement (WASD), look (mouse), jump (held), grapple (held).
    /// </summary>
    public class BotAgent : Agent {
        #region Inspector Fields
        
        [Header("References")]
        [SerializeField] private PlayerController playerController;
        [SerializeField] private PlayerInput playerInput;
        
        [Header("Training Settings")]
        [Tooltip("Enable to record demonstrations. Disable during inference.")]
        public bool recordDemonstrations;

        [Header("Look Input Testing")]
        [Tooltip("TESTING ONLY: Set to absurd value (e.g., 500f) to verify look input is working. Normal value is 200f.")]
        private const float LookInputScaling = 250f;
        private const float TrainingTimeScale = 20f;
        private const float ReferenceFixedDelta = 0.02f;
        
        #endregion

        #region Private Fields
        
        private PlayerMovementController _movement;
        private GrappleController _grapple;
        private PlayerHealthController _health;
        // Cached for performance
        private Transform _transform;
        private List<PlayerController> _allPlayers;
        private float _lastPlayerCacheTime;
        private const float PlayerCacheInterval = 1f; // Update player list every second
        
        // Grapple tracking for reward system
        private float _speedBeforeGrapple;
        private bool _wasGrapplingLastFrame;
        
        // Edge zone tracking
        private bool _isInEdgeZone;
        
        // Episode state
        private bool _episodeEnded;
        
        private bool CanControlBot => playerController.IsOwner;
        
        #endregion

        #region Initialization
        
        public override void Initialize() {
            base.Initialize();
            
            _transform = transform;
            CacheComponentReferences();
            SetupBotInput();
            InitializeTracking();
        }
        
        private void CacheComponentReferences() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }
            
            if(playerInput == null) {
                playerInput = GetComponent<PlayerInput>();
            }

            if(playerController == null) return;
            _movement = playerController.MovementController;
            _grapple = playerController.GrappleController;
            _health = playerController.HealthController;
        }
        
        private void SetupBotInput() {
            // Disable Unity Input System for bots (prevents accidental human input during training/inference)
            // Only allow human input during "Heuristic Only" mode (recording demonstrations)
            if(playerInput == null) return;
            
            var behaviorParams = GetComponent<BehaviorParameters>();
            // IsBot = true for Default (training) and InferenceOnly (using model)
            // IsBot = false only for HeuristicOnly (recording - needs human input)
            var isBot = behaviorParams != null && 
                       behaviorParams.BehaviorType != BehaviorType.HeuristicOnly;
            playerInput.IsBot = isBot;
        }
        
        private void InitializeTracking() {
            _allPlayers = new List<PlayerController>();
            CachePlayers();
            _wasGrapplingLastFrame = false;
            _speedBeforeGrapple = 0f;
        }
        
        #endregion

        #region Unity Lifecycle
        
        private void Update() {
            UpdatePlayerCache();
            HandleDeath();
        }
        
        private void UpdatePlayerCache() {
            if(Time.time - _lastPlayerCacheTime > PlayerCacheInterval) {
                CachePlayers();
                _lastPlayerCacheTime = Time.time;
            }
        }
        
        private void HandleDeath() {
            // End episode when bot dies (allows new episode to start)
            if(playerController != null && playerController.IsDead && !_episodeEnded) {
                AddReward(-0.02f); // Reduced - harsh penalty discourages exploration needed for learning
                EndEpisode();
                _episodeEnded = true;
            }
            
            // Reset episode ended flag when bot respawns
            if(playerController != null && !playerController.IsDead && _episodeEnded) {
                _episodeEnded = false;
            }
        }
        
        #endregion

        #region ML-Agents: Observations
        
        /// <summary>
        /// Collects observations for the ML model.
        /// Total: 16 vector observations (self state + grapple state) plus ray perception sensor output.
        /// Simplified for movement-only training (no enemy/combat data). ML-Agents handles normalization automatically.
        /// </summary>
        public override void CollectObservations(VectorSensor sensor) {
            // Validate references
            if(_movement == null || _grapple == null || _health == null) {
                AddZeroObservations(sensor);
                return;
            }
            
            CollectMovementStateObservations(sensor);
        }
        
        private static void AddZeroObservations(VectorSensor sensor) {
            for(var i = 0; i < 16; i++) {
                sensor.AddObservation(0f);
            }
        }
        
        private void CollectMovementStateObservations(VectorSensor sensor) {
            // Position (raw values - ML-Agents normalizes automatically)
            sensor.AddObservation(_transform.position); // 3
            
            // Velocity (raw values - ML-Agents normalizes automatically)
            sensor.AddObservation(_movement.HorizontalVelocity); // 3
            sensor.AddObservation(_movement.VerticalVelocity); // 1
            
            // Current rotation (yaw as forward direction vector)
            sensor.AddObservation(_transform.forward); // 3
            
            // Current pitch (separate from yaw, raw value - ML-Agents normalizes automatically)
            var currentPitch = playerController != null ? playerController.CurrentPitch : 0f;
            sensor.AddObservation(currentPitch); // 1
            
            // Movement / safety state
            sensor.AddObservation(_movement.IsGrounded ? 1f : 0f); // 1
            sensor.AddObservation(_isInEdgeZone ? 1f : 0f); // 1 (edge warning flag)
            
            // Grapple state
            sensor.AddObservation(_grapple.CanGrapple ? 1f : 0f); // 1
            sensor.AddObservation(_grapple.IsGrappling ? 1f : 0f); // 1
            sensor.AddObservation(_grapple.CooldownProgress); // 1 (0-1)
        }
        
        #endregion

        #region ML-Agents: Actions
        
        /// <summary>
        /// Receives actions from the ML model and applies them to the player.
        /// During recording (Heuristic mode), this is skipped - human input controls the player.
        /// During inference (trained model), this applies the ML model's decisions.
        /// </summary>
        public override void OnActionReceived(ActionBuffers actions) {
            if(playerInput == null || playerController == null) return;
            
            if(!CanControlBot) return;
            
            if(IsHeuristicMode()) {
                ApplyHeuristicModeRewards();
                return;
            }
            
            if(ShouldSkipActions()) {
                ClearInputs();
                return;
            }
            
            ApplyActions(actions);
            CalculateRewards(actions);
        }
        
        private bool IsHeuristicMode() {
            var behaviorParams = GetComponent<BehaviorParameters>();
            return behaviorParams != null && behaviorParams.BehaviorType == BehaviorType.HeuristicOnly;
        }
        
        private void ApplyHeuristicModeRewards() {
            // Still collect minimal rewards for recording metadata, but don't control the player
            AddReward(-0.0001f);
            if(_movement != null) {
                var speed = _movement.HorizontalVelocity.magnitude;
                AddReward(speed / 10000f);
            }
            AddReward(_transform.position.y / 100000f);
        }
        
        private bool ShouldSkipActions() {
            return playerController.IsDead || 
                   (Menu.GameMenuManager.Instance != null && Menu.GameMenuManager.Instance.IsPaused);
        }
        
        private void ClearInputs() {
            playerInput.SetMovementInput(Vector2.zero);
            playerInput.SetLookInput(Vector2.zero);
            playerInput.SetSprintInput(false);
        }
        
        private void ApplyActions(ActionBuffers actions) {
            ApplyMovementActions(actions);
            ApplyLookActions(actions);
            ApplyDiscreteActions(actions);
        }
        
        private void ApplyMovementActions(ActionBuffers actions) {
            var moveX = actions.ContinuousActions[0]; // -1 to 1 (strafe left/right)
            var moveY = actions.ContinuousActions[1]; // -1 to 1 (forward/back)
            playerInput.SetMovementInput(new Vector2(moveX, moveY));
        }
        
        private void ApplyLookActions(ActionBuffers actions) {
            var lookXAction = actions.ContinuousActions[2]; // Raw action value (-1 to 1)
            var lookYAction = actions.ContinuousActions[3]; // Raw action value (-1 to 1)
            
            var lookX = lookXAction * LookInputScaling;
            var lookY = lookYAction * LookInputScaling;
            
            playerInput.SetLookInput(new Vector2(lookX, lookY));
        }
        
        private void ApplyDiscreteActions(ActionBuffers actions) {
            // Jump (branch 0): 0 = don't jump, 1 = jump
            if(actions.DiscreteActions[0] == 1) {
                playerInput.TriggerJump();
            }
            
            // Grapple (branch 1): 0 = don't grapple, 1 = grapple
            if(actions.DiscreteActions[1] == 1) {
                playerInput.TriggerGrapple();
            }

            if(actions.DiscreteActions.Length > 2) {
                var shouldSprint = actions.DiscreteActions[2] == 1;
                playerInput.SetSprintInput(shouldSprint);
            }
        }
        
        #endregion

        #region ML-Agents: Rewards
        
        private void CalculateRewards(ActionBuffers actions) {
            // Survival bonus for movement - lowered threshold to reward moderate speeds
            var currentSpeed = _movement != null ? _movement.HorizontalVelocity.magnitude : 0f;
            if(currentSpeed > 3f) {  // Lowered from 5f - reward moderate speeds while learning
                AddReward(0.002f);
            }
            
            var isGrounded = _movement != null && _movement.IsGrounded;
            var isGrappling = _grapple != null && _grapple.IsGrappling;
            var lookXAction = actions.ContinuousActions[2];
            
            // Re-enabled: Air strafing is THE core bunnyhopping mechanic
            CalculateAirStrafingReward(isGrounded, lookXAction);
            CalculateGrapplePenalty(isGrappling, currentSpeed);
            CalculateEdgeZonePenalty();
            // Re-enabled: Pitch control prevents sky/floor staring
            CalculateNeutralPitchReward();
            // CalculateGeneralMovementRewards(currentSpeed);
        }
        
        private void CalculateAirStrafingReward(bool isGrounded, float horizontalLook) {
            // Rewards horizontal mouse movement when airborne
            // Requires direction matching to encourage forward bunnyhopping (not backwards)
            // No speed gate - bot needs to learn air strafing even when starting slow
            if(isGrounded || _movement == null) return;
            
            var horizontalVelocity = _movement.HorizontalVelocity;
            var horizontalSpeed = horizontalVelocity.magnitude;
            
            // Only check direction matching if actually moving (prevents rewarding random mouse movement when stationary)
            // Still allow reward at low speeds, but require minimum movement for direction matching to be meaningful
            if(horizontalSpeed > 0.5f) {
                // Check if the look direction matches movement direction
                var velocityDirection = horizontalVelocity.normalized.x; // -1 to 1 (left to right)
                var velocitySign = Mathf.Sign(velocityDirection);
                var lookSign = Mathf.Sign(horizontalLook);
                
                // Reward only if directions match (both positive or both negative)
                // Also require minimum look input to avoid rewarding accidental input
                if(Mathf.Abs(horizontalLook) > 0.1f && 
                   Mathf.Approximately(velocitySign, lookSign)) {
                    // Reward proportional to look input magnitude and speed
                    // Use Mathf.Max to avoid division issues when stationary, but still reward at low speeds
                    var strafeReward = Mathf.Abs(horizontalLook) * (Mathf.Max(horizontalSpeed, 1f) / 100f) * 0.01f;
                    AddReward(strafeReward);
                }
            }
            
            // Momentum preservation bonus (maintaining high speed in air)
            // Keep this gated since it's specifically about maintaining high speed
            if(horizontalSpeed > 15f) {
                AddReward(0.0005f);
            }
        }
        
        private void CalculateGrapplePenalty(bool isGrappling, float currentSpeed) {
            // REMOVED: All grapple penalties - GAIL demonstrations teach proper usage
            // Keeping asymmetric penalty (punish bad, no reward for good) confused learning
            // Track state for potential future use only

            _speedBeforeGrapple = isGrappling switch {
                true when !_wasGrapplingLastFrame => currentSpeed,
                _ => _speedBeforeGrapple
            };

            _wasGrapplingLastFrame = isGrappling;
        }
        
        private static void CalculateEdgeZonePenalty() {
            // REMOVED: Edge penalty was overwhelming survival bonus and punishing exploration
            // Agent needs to explore near edges to learn avoidance; GAIL demos show proper behavior
            // The _isInEdgeZone observation remains available for the agent to learn from
        }
        
        private void CalculateNeutralPitchReward() {
            // Reward for neutral pitch - prevents sky/floor staring which breaks hop movement
            if(playerController == null) return;
            
            var currentPitch = playerController.CurrentPitch;
            var absPitch = Mathf.Abs(currentPitch);
            
            // Reward for pitch between -20 and +20 degrees (near horizontal)
            if(absPitch < 20f) {
                AddReward(0.0002f * (1f - absPitch / 20f)); // Doubled - stronger incentive to look forward
            }
            
            // Penalty for extreme pitch angles - STRONGER to stop sky/floor staring
            if(currentPitch > 55f) {
                // Looking UP more than 55° - agent was staring at sky, penalize heavily
                var excessAngle = absPitch - 55f;
                var penalty = (excessAngle / 35f) * 0.02f; // Doubled from 0.005f
                AddReward(-penalty);
            } else if(currentPitch < -55f) {
                // Looking DOWN more than 55° - floor staring
                var excessAngle = absPitch - 55f;
                var penalty = (excessAngle / 35f) * 0.02f; // Doubled from 0.01f
                AddReward(-penalty);
            }
        }
        
        private void CalculateGeneralMovementRewards(float currentSpeed) {
            // Small reward for speed (encourages active movement)
            AddReward(currentSpeed / 10000f);
            
            // Small reward for height (encourages using vertical space)
            AddReward(_transform.position.y / 100000f);
        }
        
        #endregion

        #region ML-Agents: Heuristic (Recording)
        
        /// <summary>
        /// For recording demonstrations: captures human player input when behavior type is "Heuristic Only".
        /// This is CRITICAL for demonstration recording - ML-Agents needs to know what actions you took.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut) {
            if(playerController == null) {
                SetZeroActions(actionsOut);
                return;
            }
            
            CaptureContinuousActions(actionsOut);
            CaptureDiscreteActions(actionsOut);
        }
        
        private static void SetZeroActions(ActionBuffers actionsOut) {
            var zeroActions = actionsOut.ContinuousActions;
            for(var i = 0; i < zeroActions.Length; i++) {
                zeroActions[i] = 0f;
            }
            var zeroDiscrete = actionsOut.DiscreteActions;
            for(var i = 0; i < zeroDiscrete.Length; i++) {
                zeroDiscrete[i] = 0;
            }
        }
        
        private void CaptureContinuousActions(ActionBuffers actionsOut) {
            var continuousActions = actionsOut.ContinuousActions;
            
            // Movement input
            continuousActions[0] = playerController.moveInput.x; // strafe left/right
            continuousActions[1] = playerController.moveInput.y; // forward/back
            
            // Look input (scaled down to match action space -1 to 1 range)
            var (lookX, lookY) = CaptureLookInput();
            continuousActions[2] = lookX;
            continuousActions[3] = lookY;
        }
        
        private (float lookX, float lookY) CaptureLookInput() {
            var rawLookX = playerController.lookInput.x;
            var rawLookY = playerController.lookInput.y;
            // Match the inference scaling (LookInputScaling = 250f) so model learns correct scale
            // This ensures recordings match what the model will output during training/inference
            var lookX = Mathf.Clamp(rawLookX / LookInputScaling, -1f, 1f);
            var lookY = Mathf.Clamp(rawLookY / LookInputScaling, -1f, 1f);
            
            return (lookX, lookY);
        }

        private void CaptureDiscreteActions(ActionBuffers actionsOut) {
            var discreteActions = actionsOut.DiscreteActions;
            
            // Read held state from PlayerInput (respects custom keybinds + scroll wheel)
            // This captures whether the player is HOLDING the button, not just pressing it
            // Critical for auto-hopping behavior where jump is held continuously
            var jumpHeld = playerInput != null && playerInput.IsJumpHeld;
            var grappleHeld = playerInput != null && playerInput.IsGrappleHeld;
            var sprintHeld = playerController != null && playerController.sprintInput;

            discreteActions[0] = jumpHeld ? 1 : 0;  // Jump held = 1, not held = 0
            discreteActions[1] = grappleHeld ? 1 : 0;  // Grapple held = 1, not held = 0

            if(discreteActions.Length > 2) {
                discreteActions[2] = sprintHeld ? 1 : 0; // Sprint held = 1, otherwise 0
            }
        }
        
        #endregion

        #region ML-Agents: Episode Management
        
        /// <summary>
        /// Called when an episode begins (e.g., respawn, match start).
        /// </summary>
        public override void OnEpisodeBegin() {
            CachePlayers();
            _episodeEnded = false;
            _wasGrapplingLastFrame = false;
            _speedBeforeGrapple = 0f;
            _isInEdgeZone = false;
        }
        
        #endregion

        #region Helper Methods
        
        private void CachePlayers() {
            _allPlayers.Clear();
            var allPlayerControllers = FindObjectsByType<PlayerController>(FindObjectsSortMode.None);
            foreach(var p in allPlayerControllers) {
                if(p != null && p != playerController && p.gameObject.activeInHierarchy) {
                    _allPlayers.Add(p);
                }
            }
        }
        
        /// <summary>
        /// Finds the nearest enemy player.
        /// </summary>
        private PlayerController FindNearestEnemy() {
            PlayerController nearest = null;
            var minDist = float.MaxValue;
            
            foreach(var enemy in _allPlayers) {
                if(enemy == null || enemy == playerController) continue;
                if(enemy.IsDead) continue; // Ignore dead players
                
                var dist = Vector3.Distance(_transform.position, enemy.transform.position);
                if(dist < minDist) {
                    minDist = dist;
                    nearest = enemy;
                }
            }
            
            return nearest;
        }
        
        /// <summary>
        /// Called by EdgeZoneTrigger to notify bot when it enters/exits edge zone.
        /// </summary>
        public void SetEdgeZoneState(bool inZone) {
            _isInEdgeZone = inZone;
        }
        
        #endregion
    }
}
