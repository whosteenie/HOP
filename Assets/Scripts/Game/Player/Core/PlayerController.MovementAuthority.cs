using System.Collections.Generic;
using Network.Session;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Game.Player {
    public partial class PlayerController {
        [System.Flags]
        private enum MovementAuthorityFlags : ushort {
            None = 0,
            Grounded = 1 << 0,
            Crouching = 1 << 1,
            Sliding = 1 << 2,
            WallRunning = 1 << 3,
            Grappling = 1 << 4,
            Mantling = 1 << 5,
            JumpPadLaunch = 1 << 6,
            SprintHeld = 1 << 7,
            CrouchHeld = 1 << 8,
            JumpHeld = 1 << 9,
            JumpPressed = 1 << 10,
            GrappleHeld = 1 << 11,
            GrapplePressed = 1 << 12
        }

        private struct MovementInputCommand : INetworkSerializable {
            public uint Tick;
            public float DeltaTime;
            public Vector2 MoveInput;
            public Vector2 LookInput;
            public float YawDegrees;
            public float PitchDegrees;
            public MovementAuthorityFlags Flags;
            public double ClientLocalTime;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
                serializer.SerializeValue(ref Tick);
                serializer.SerializeValue(ref DeltaTime);
                serializer.SerializeValue(ref MoveInput);
                serializer.SerializeValue(ref LookInput);
                serializer.SerializeValue(ref YawDegrees);
                serializer.SerializeValue(ref PitchDegrees);
                serializer.SerializeValue(ref Flags);
                serializer.SerializeValue(ref ClientLocalTime);
            }
        }

        private struct PredictedMovementState {
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 HorizontalVelocity;
            public float VerticalVelocity;
            public MovementAuthorityFlags Flags;
            public Vector3 IntendedDisplacement;
            public Vector3 ActualDisplacement;
            public int CollisionFlags;
            public bool GroundedBeforeMove;
            public bool GroundedAfterMove;
        }

        private struct MovementAuthoritativeSnapshot : INetworkSerializable {
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 HorizontalVelocity;
            public float VerticalVelocity;
            public MovementAuthorityFlags Flags;
            public Vector3 IntendedDisplacement;
            public Vector3 ActualDisplacement;
            public int CollisionFlags;
            public bool GroundedBeforeMove;
            public bool GroundedAfterMove;
            public Vector3 WallNormal;
            public Vector3 GrapplePoint;
            public Vector3 MantleTargetPosition;
            public float MantleProgress01;
            public double ServerTime;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
                serializer.SerializeValue(ref Tick);
                serializer.SerializeValue(ref Position);
                serializer.SerializeValue(ref Rotation);
                serializer.SerializeValue(ref HorizontalVelocity);
                serializer.SerializeValue(ref VerticalVelocity);
                serializer.SerializeValue(ref Flags);
                serializer.SerializeValue(ref IntendedDisplacement);
                serializer.SerializeValue(ref ActualDisplacement);
                serializer.SerializeValue(ref CollisionFlags);
                serializer.SerializeValue(ref GroundedBeforeMove);
                serializer.SerializeValue(ref GroundedAfterMove);
                serializer.SerializeValue(ref WallNormal);
                serializer.SerializeValue(ref GrapplePoint);
                serializer.SerializeValue(ref MantleTargetPosition);
                serializer.SerializeValue(ref MantleProgress01);
                serializer.SerializeValue(ref ServerTime);
            }
        }

        private struct BufferedMovementCommand {
            public MovementInputCommand Command;
            public double ReceivedServerTime;
        }

        private const int MovementAuthorityFoundationBufferSize = 64;
        private const float MovementAuthorityNoopPositionErrorThresholdMeters = 0.075f;
        private const float MovementAuthorityNoopYawErrorThresholdDegrees = 1.5f;
        private const float MovementAuthorityNoopHorizontalVelocityErrorThreshold = 0.05f;
        private const float MovementAuthorityNoopVerticalVelocityErrorThreshold = 0.05f;
        private float _movementAuthorityTickInterval;
        private float _movementAuthorityOwnerAccumulator;
        private float _movementAuthorityServerAccumulator;
        private uint _nextMovementAuthorityTick = 1;
        private bool _lastMovementAuthorityJumpHeld;
        private bool _lastMovementAuthorityGrappleHeld;
        private readonly Queue<uint> _predictedMovementTicks = new();
        private readonly Dictionary<uint, PredictedMovementState> _predictedMovementSnapshots = new();
        private readonly Queue<uint> _ownerMovementCommandTicks = new();
        private readonly Dictionary<uint, MovementInputCommand> _ownerMovementCommandHistory = new();
        private readonly Queue<uint> _authoritativeMovementTicks = new();
        private readonly Dictionary<uint, MovementAuthoritativeSnapshot> _authoritativeMovementSnapshots = new();
        private readonly Queue<BufferedMovementCommand> _serverBufferedMovementCommands = new();
        private MovementInputCommand _latestServerMovementCommand;
        private bool _hasLatestServerMovementCommand;
        private MovementAuthoritativeSnapshot _latestAuthoritativeMovementSnapshot;
        private float _latestMovementAuthorityErrorMeters;
        private bool _movementAuthorityJumpConsumedForCurrentServerTick;
        private uint _lastMovementAuthorityAcknowledgedTick;
        private bool _isMovementSimulationDeltaTimeOverridden;
        private float _movementSimulationDeltaTimeOverride;
        private bool _queuedMovementAuthorityJumpPress;
        private float _movementAuthorityPredictedYawDegrees;
        private bool _hasMovementAuthorityPredictedYaw;
        private float _movementAuthorityVisualYawDegrees;
        private bool _hasMovementAuthorityVisualYaw;

        public float LatestMovementAuthorityErrorMeters => _latestMovementAuthorityErrorMeters;
        internal float MovementSimulationDeltaTime => _isMovementSimulationDeltaTimeOverridden
            ? _movementSimulationDeltaTimeOverride
            : Time.deltaTime;

        private void ResetMovementAuthorityFoundationState() {
            _movementAuthorityOwnerAccumulator = 0f;
            _movementAuthorityServerAccumulator = 0f;
            _nextMovementAuthorityTick = 1;
            _lastMovementAuthorityJumpHeld = false;
            _lastMovementAuthorityGrappleHeld = false;
            _predictedMovementTicks.Clear();
            _predictedMovementSnapshots.Clear();
            _ownerMovementCommandTicks.Clear();
            _ownerMovementCommandHistory.Clear();
            _authoritativeMovementTicks.Clear();
            _authoritativeMovementSnapshots.Clear();
            _serverBufferedMovementCommands.Clear();
            _latestServerMovementCommand = default;
            _hasLatestServerMovementCommand = false;
            _latestAuthoritativeMovementSnapshot = default;
            _latestMovementAuthorityErrorMeters = 0f;
            _movementAuthorityJumpConsumedForCurrentServerTick = false;
            _lastMovementAuthorityAcknowledgedTick = 0;
            _isMovementSimulationDeltaTimeOverridden = false;
            _movementSimulationDeltaTimeOverride = 0f;
            _queuedMovementAuthorityJumpPress = false;
            _movementAuthorityPredictedYawDegrees = 0f;
            _hasMovementAuthorityPredictedYaw = false;
            _movementAuthorityVisualYawDegrees = 0f;
            _hasMovementAuthorityVisualYaw = false;
        }

        private void UpdateMovementAuthorityFoundation() {
            if(!IsSpawned) return;

            EnsureMovementAuthorityTickInterval();

            if(IsOwner) {
                _movementAuthorityOwnerAccumulator += Time.deltaTime;
                TickMovementAuthorityOwnerCommands();
            }

            if(IsServer) {
                _movementAuthorityServerAccumulator += Time.deltaTime;
                TickMovementAuthorityServerSnapshots();
            }
        }

        private void EnsureMovementAuthorityTickInterval() {
            if(_movementAuthorityTickInterval > 0f) return;

            var tickRate = 60u;
            if(NetworkManager != null && NetworkManager.NetworkConfig != null) {
                tickRate = System.Math.Max(1u, NetworkManager.NetworkConfig.TickRate);
            }

            _movementAuthorityTickInterval = 1f / tickRate;
        }

        private void TickMovementAuthorityOwnerCommands() {
            if(_movementAuthorityTickInterval <= 0f) return;

            var tickBudget = 0;
            while(_movementAuthorityOwnerAccumulator >= _movementAuthorityTickInterval && tickBudget < 4) {
                _movementAuthorityOwnerAccumulator -= _movementAuthorityTickInterval;
                var command = BuildMovementInputCommand();
                BufferOwnerMovementCommand(command);

                if(ShouldUseBasicMovementAuthoritySliceLocally()) {
                    ApplyOwnerLookForMovementAuthoritySlice(command);
                    SimulateBasicLocomotionCommand(command);
                    var predictedSnapshot = CapturePredictedMovementState(command.Tick);
                    BufferPredictedMovementSnapshot(predictedSnapshot);
                    AnticipateClientPredictedTransform(predictedSnapshot);
                }

                if(IsServer) {
                    BufferServerMovementCommand(command, Time.timeAsDouble);
                } else {
                    SubmitMovementInputCommandServerRpc(command);
                }

                tickBudget++;
            }
        }

        private void TickMovementAuthorityServerSnapshots() {
            if(_movementAuthorityTickInterval <= 0f) return;

            var tickBudget = 0;
            while(_serverBufferedMovementCommands.Count > 0 && tickBudget < 8) {
                var bufferedCommand = _serverBufferedMovementCommands.Dequeue();
                _latestServerMovementCommand = bufferedCommand.Command;
                _hasLatestServerMovementCommand = true;
                _movementAuthorityJumpConsumedForCurrentServerTick = false;

                if(ShouldRunServerAuthoritativeBasicLocomotion()) {
                    SimulateBasicLocomotionCommand(_latestServerMovementCommand);
                }

                var snapshot = CaptureMovementAuthoritySnapshot(_latestServerMovementCommand.Tick, Time.timeAsDouble);
                BufferAuthoritativeMovementSnapshot(snapshot);

                if(IsOwner) {
                    ApplyReceivedMovementAuthoritySnapshot(snapshot);
                } else {
                    ReceiveMovementAuthoritySnapshotOwnerRpc(snapshot);
                }

                tickBudget++;
            }
        }

        private MovementInputCommand BuildMovementInputCommand() {
            var jumpHeld = playerInput != null && playerInput.IsJumpHeld;
            var grappleHeld = playerInput != null && playerInput.IsGrappleHeld;
            var jumpPressed = ConsumeQueuedMovementAuthorityJumpPress();
            var sampledMoveInput = playerInput != null ? playerInput.SampleMovementAuthorityMoveInput() : moveInput;
            var sampledLookInput = playerInput != null ? playerInput.ConsumeMovementAuthorityLookInput() : lookInput;
            var yawDegrees = GetMovementAuthorityCommandYawDegrees(sampledLookInput.x);

            var flags = MovementAuthorityFlags.None;
            if(IsGrounded) flags |= MovementAuthorityFlags.Grounded;
            if(IsCrouching) flags |= MovementAuthorityFlags.Crouching;
            if(movementController != null && movementController.IsSliding) flags |= MovementAuthorityFlags.Sliding;
            if(wallRunController != null && wallRunController.IsWallRunning) flags |= MovementAuthorityFlags.WallRunning;
            if(grappleController != null && grappleController.IsGrappling) flags |= MovementAuthorityFlags.Grappling;
            if(mantleController != null && mantleController.IsMantling) flags |= MovementAuthorityFlags.Mantling;
            if(movementController != null && movementController.IsInJumpPadLaunch) flags |= MovementAuthorityFlags.JumpPadLaunch;
            if(sprintInput) flags |= MovementAuthorityFlags.SprintHeld;
            if(crouchInput) flags |= MovementAuthorityFlags.CrouchHeld;
            if(jumpHeld) flags |= MovementAuthorityFlags.JumpHeld;
            if(grappleHeld) flags |= MovementAuthorityFlags.GrappleHeld;
            if(jumpPressed) flags |= MovementAuthorityFlags.JumpPressed;
            if(grappleHeld && !_lastMovementAuthorityGrappleHeld) flags |= MovementAuthorityFlags.GrapplePressed;

            _lastMovementAuthorityJumpHeld = jumpHeld;
            _lastMovementAuthorityGrappleHeld = grappleHeld;

            return new MovementInputCommand {
                Tick = _nextMovementAuthorityTick++,
                DeltaTime = _movementAuthorityTickInterval,
                MoveInput = sampledMoveInput,
                LookInput = sampledLookInput,
                YawDegrees = yawDegrees,
                PitchDegrees = lookController != null ? lookController.CurrentPitch : 0f,
                Flags = flags,
                ClientLocalTime = Time.timeAsDouble
            };
        }

        private bool ConsumeQueuedMovementAuthorityJumpPress() {
            var jumpPressed = _queuedMovementAuthorityJumpPress;
            _queuedMovementAuthorityJumpPress = false;
            return jumpPressed;
        }

        private MovementAuthoritativeSnapshot CaptureMovementAuthoritySnapshot(uint tick, double timestamp) {
            var flags = CaptureMovementAuthorityFlags();

            return new MovementAuthoritativeSnapshot {
                Tick = tick,
                Position = playerTransform != null ? playerTransform.position : transform.position,
                Rotation = playerTransform != null ? playerTransform.rotation : transform.rotation,
                HorizontalVelocity = movementController != null ? movementController.HorizontalVelocity : Vector3.zero,
                VerticalVelocity = movementController != null ? movementController.VerticalVelocity : 0f,
                Flags = flags,
                IntendedDisplacement = movementController != null ? movementController.LastIntendedDisplacement : Vector3.zero,
                ActualDisplacement = movementController != null ? movementController.LastActualDisplacement : Vector3.zero,
                CollisionFlags = movementController != null ? (int)movementController.LastMoveCollisionFlags : 0,
                GroundedBeforeMove = movementController != null && movementController.LastGroundedBeforeMove,
                GroundedAfterMove = movementController != null && movementController.LastGroundedAfterMove,
                WallNormal = wallRunController != null ? wallRunController.CurrentWallNormal : Vector3.zero,
                GrapplePoint = grappleController != null ? grappleController.CurrentGrapplePoint : Vector3.zero,
                MantleTargetPosition = mantleController != null ? mantleController.CurrentMantleTargetPosition : Vector3.zero,
                MantleProgress01 = mantleController != null ? mantleController.CurrentMantleProgress01 : 0f,
                ServerTime = timestamp
            };
        }

        private PredictedMovementState CapturePredictedMovementState(uint tick) {
            return new PredictedMovementState {
                Tick = tick,
                Position = playerTransform != null ? playerTransform.position : transform.position,
                Rotation = playerTransform != null ? playerTransform.rotation : transform.rotation,
                HorizontalVelocity = movementController != null ? movementController.HorizontalVelocity : Vector3.zero,
                VerticalVelocity = movementController != null ? movementController.VerticalVelocity : 0f,
                Flags = CaptureMovementAuthorityFlags(),
                IntendedDisplacement = movementController != null ? movementController.LastIntendedDisplacement : Vector3.zero,
                ActualDisplacement = movementController != null ? movementController.LastActualDisplacement : Vector3.zero,
                CollisionFlags = movementController != null ? (int)movementController.LastMoveCollisionFlags : 0,
                GroundedBeforeMove = movementController != null && movementController.LastGroundedBeforeMove,
                GroundedAfterMove = movementController != null && movementController.LastGroundedAfterMove
            };
        }

        private MovementAuthorityFlags CaptureMovementAuthorityFlags() {
            var flags = MovementAuthorityFlags.None;
            if(IsGrounded) flags |= MovementAuthorityFlags.Grounded;
            if(IsCrouching) flags |= MovementAuthorityFlags.Crouching;
            if(movementController != null && movementController.IsSliding) flags |= MovementAuthorityFlags.Sliding;
            if(wallRunController != null && wallRunController.IsWallRunning) flags |= MovementAuthorityFlags.WallRunning;
            if(grappleController != null && grappleController.IsGrappling) flags |= MovementAuthorityFlags.Grappling;
            if(mantleController != null && mantleController.IsMantling) flags |= MovementAuthorityFlags.Mantling;
            if(movementController != null && movementController.IsInJumpPadLaunch) flags |= MovementAuthorityFlags.JumpPadLaunch;
            if(sprintInput) flags |= MovementAuthorityFlags.SprintHeld;
            if(crouchInput) flags |= MovementAuthorityFlags.CrouchHeld;
            return flags;
        }

        private void BufferPredictedMovementSnapshot(PredictedMovementState snapshot) {
            _predictedMovementSnapshots[snapshot.Tick] = snapshot;
            _predictedMovementTicks.Enqueue(snapshot.Tick);
            while(_predictedMovementTicks.Count > MovementAuthorityFoundationBufferSize) {
                var staleTick = _predictedMovementTicks.Dequeue();
                _predictedMovementSnapshots.Remove(staleTick);
            }
        }

        private void BufferOwnerMovementCommand(MovementInputCommand command) {
            _ownerMovementCommandHistory[command.Tick] = command;
            _ownerMovementCommandTicks.Enqueue(command.Tick);
            while(_ownerMovementCommandTicks.Count > MovementAuthorityFoundationBufferSize) {
                var staleTick = _ownerMovementCommandTicks.Dequeue();
                _ownerMovementCommandHistory.Remove(staleTick);
            }
        }

        private void BufferAuthoritativeMovementSnapshot(MovementAuthoritativeSnapshot snapshot) {
            _authoritativeMovementSnapshots[snapshot.Tick] = snapshot;
            _authoritativeMovementTicks.Enqueue(snapshot.Tick);
            TrimMovementSnapshotBuffer(_authoritativeMovementTicks, _authoritativeMovementSnapshots);
        }

        private static void TrimMovementSnapshotBuffer(Queue<uint> tickQueue,
            Dictionary<uint, MovementAuthoritativeSnapshot> snapshotLookup) {
            while(tickQueue.Count > MovementAuthorityFoundationBufferSize) {
                var staleTick = tickQueue.Dequeue();
                snapshotLookup.Remove(staleTick);
            }
        }

        private void BufferServerMovementCommand(MovementInputCommand command, double receivedServerTime) {
            _latestServerMovementCommand = command;
            _hasLatestServerMovementCommand = true;
            _movementAuthorityJumpConsumedForCurrentServerTick = false;
            _serverBufferedMovementCommands.Enqueue(new BufferedMovementCommand {
                Command = command,
                ReceivedServerTime = receivedServerTime
            });

            while(_serverBufferedMovementCommands.Count > MovementAuthorityFoundationBufferSize) {
                _serverBufferedMovementCommands.Dequeue();
            }
        }

        private void ApplyReceivedMovementAuthoritySnapshot(MovementAuthoritativeSnapshot snapshot) {
            if(snapshot.Tick <= _lastMovementAuthorityAcknowledgedTick) {
                return;
            }

            _latestAuthoritativeMovementSnapshot = snapshot;
            BufferAuthoritativeMovementSnapshot(snapshot);
        }

        private void ReconcileOwnerPredictedMovement(MovementAuthoritativeSnapshot authoritativeSnapshot) {
            if(!IsOwner) return;
            if(netIsDead.Value) return;
            if(characterController == null || movementController == null) return;
            if(authoritativeSnapshot.Tick <= _lastMovementAuthorityAcknowledgedTick) return;

            _lastMovementAuthorityAcknowledgedTick = authoritativeSnapshot.Tick;
            var authoritativePosition = authoritativeSnapshot.Position;
            var authoritativeRotation = authoritativeSnapshot.Rotation;
            var positionErrorMeters = 0f;
            var yawErrorDegrees = 0f;
            var horizontalVelocityError = 0f;
            var verticalVelocityError = 0f;
            var predictedFlags = MovementAuthorityFlags.None;
            var hasPredictedSnapshot = false;

            if(_predictedMovementSnapshots.TryGetValue(authoritativeSnapshot.Tick, out var predictedSnapshot)) {
                hasPredictedSnapshot = true;
                positionErrorMeters = Vector3.Distance(predictedSnapshot.Position, authoritativePosition);
                yawErrorDegrees = Mathf.Abs(Mathf.DeltaAngle(predictedSnapshot.Rotation.eulerAngles.y, authoritativeRotation.eulerAngles.y));
                horizontalVelocityError = Vector3.Distance(predictedSnapshot.HorizontalVelocity, authoritativeSnapshot.HorizontalVelocity);
                verticalVelocityError = Mathf.Abs(predictedSnapshot.VerticalVelocity - authoritativeSnapshot.VerticalVelocity);
                predictedFlags = predictedSnapshot.Flags;
                _latestMovementAuthorityErrorMeters = positionErrorMeters;
            } else {
                _latestMovementAuthorityErrorMeters = 0f;
            }

            if(ShouldSkipMovementAuthorityReconcile(hasPredictedSnapshot, predictedFlags, authoritativeSnapshot.Flags,
                   positionErrorMeters, yawErrorDegrees, horizontalVelocityError, verticalVelocityError)) {
                return;
            }

            var preReconcilePosition = playerTransform != null ? playerTransform.position : transform.position;
            ApplyAuthoritativeMovementSnapshot(authoritativeSnapshot);

            var ticksToReplay = new List<uint>();
            foreach(var bufferedTick in _ownerMovementCommandTicks) {
                if(bufferedTick > authoritativeSnapshot.Tick) {
                    ticksToReplay.Add(bufferedTick);
                }
            }

            foreach(var replayTick in ticksToReplay) {
                if(!_ownerMovementCommandHistory.TryGetValue(replayTick, out var replayCommand)) {
                    continue;
                }

                if(!ShouldUseBasicMovementAuthoritySliceLocally()) {
                    break;
                }

                SimulateBasicLocomotionCommand(replayCommand);
                var replayedSnapshot = CapturePredictedMovementState(replayCommand.Tick);
                BufferPredictedMovementSnapshot(replayedSnapshot);
            }

            var anticipatedSnapshot = CapturePredictedMovementState(
                ticksToReplay.Count > 0 ? ticksToReplay[^1] : authoritativeSnapshot.Tick);
            var rootCorrectionDelta = anticipatedSnapshot.Position - preReconcilePosition;
            if(rootCorrectionDelta.sqrMagnitude > 0.000001f && lookController != null) {
                lookController.CompensateForRootTranslation(rootCorrectionDelta);
            }
            AnticipateClientPredictedTransform(anticipatedSnapshot);
            LogMovementAuthoritySimulationDiagnostics(authoritativeSnapshot, hasPredictedSnapshot, predictedFlags,
                positionErrorMeters, yawErrorDegrees, horizontalVelocityError, verticalVelocityError,
                ticksToReplay.Count);
        }

        private void ApplyAuthoritativeMovementSnapshot(MovementAuthoritativeSnapshot authoritativeSnapshot) {
            var shouldDisableCharacterController = characterController.enabled;
            if(shouldDisableCharacterController) {
                characterController.enabled = false;
            }

            var authoritativePosition = authoritativeSnapshot.Position;
            var authoritativeRotation = authoritativeSnapshot.Rotation;

            playerTransform.SetPositionAndRotation(authoritativePosition, authoritativeRotation);
            _movementAuthorityPredictedYawDegrees = authoritativeRotation.eulerAngles.y;
            _hasMovementAuthorityPredictedYaw = true;
            _movementAuthorityVisualYawDegrees = authoritativeRotation.eulerAngles.y;
            _hasMovementAuthorityVisualYaw = true;
            movementController.SetVelocity(authoritativeSnapshot.HorizontalVelocity);
            movementController.VerticalVelocity = authoritativeSnapshot.VerticalVelocity;

            if(shouldDisableCharacterController) {
                characterController.enabled = true;
            }
        }

        private bool ShouldRunServerAuthoritativeBasicLocomotion() {
            if(!IsServer || IsOwner) return false;
            if(!_hasLatestServerMovementCommand) return false;
            if(netIsDead.Value) return false;
            if(characterController == null || !characterController.enabled) return false;
            if(movementController == null) return false;
            if(!UsesBasicLocomotionAuthorityFlags(_latestServerMovementCommand.Flags)) return false;
            if(grappleController != null && grappleController.IsGrappling) return false;
            if(mantleController != null && mantleController.IsMantling) return false;
            if(wallRunController != null && wallRunController.IsWallRunning) return false;

            return UsesBasicLocomotionAuthorityFlags(_latestAuthoritativeMovementSnapshot.Flags) ||
                   _latestAuthoritativeMovementSnapshot.Tick == 0;
        }

        private static bool UsesBasicLocomotionAuthorityFlags(MovementAuthorityFlags flags) {
            var unsupportedFlags = MovementAuthorityFlags.WallRunning |
                                   MovementAuthorityFlags.Grappling |
                                   MovementAuthorityFlags.Mantling;
            return (flags & unsupportedFlags) == 0;
        }

        internal bool ShouldUseBasicMovementAuthoritySliceLocally() {
            if(!IsOwner) return false;
            if(netIsDead.Value) return false;
            if(characterController == null || !characterController.enabled) return false;
            if(movementController == null) return false;
            if(grappleController != null && grappleController.IsGrappling) return false;
            if(mantleController != null && mantleController.IsMantling) return false;
            if(wallRunController != null && wallRunController.IsWallRunning) return false;
            return true;
        }

        internal bool ShouldWriteMovementStateFromServerAuthoritySlice() {
            return ShouldRunServerAuthoritativeBasicLocomotion();
        }

        internal bool ShouldWriteMovementStateFromOwnerLocalPath() {
            return IsOwner && !ShouldUseBasicMovementAuthoritySliceLocally();
        }

        internal bool ShouldBypassLegacyMovementValidation() {
            return ShouldRunServerAuthoritativeBasicLocomotion();
        }

        internal void QueueMovementAuthorityJumpPress() {
            _queuedMovementAuthorityJumpPress = true;
        }

        private void SimulateBasicLocomotionCommand(MovementInputCommand command) {
            moveInput = command.MoveInput;
            lookInput = command.LookInput;
            sprintInput = (command.Flags & MovementAuthorityFlags.SprintHeld) != 0;
            crouchInput = (command.Flags & MovementAuthorityFlags.CrouchHeld) != 0;

            if(playerTransform != null) {
                playerTransform.rotation = Quaternion.Euler(0f, command.YawDegrees, 0f);
            }

            _movementAuthorityPredictedYawDegrees = command.YawDegrees;
            _hasMovementAuthorityPredictedYaw = true;
            if(!ShouldUseBasicMovementAuthoritySliceLocally()) {
                _movementAuthorityVisualYawDegrees = command.YawDegrees;
                _hasMovementAuthorityVisualYaw = true;
            }

            BeginMovementSimulationDeltaOverride(command.DeltaTime);
            if(!_movementAuthorityJumpConsumedForCurrentServerTick &&
               (command.Flags & MovementAuthorityFlags.JumpPressed) != 0) {
                _movementAuthorityJumpConsumedForCurrentServerTick = true;
                TryJump();

                if(grappleController != null) {
                    grappleController.CancelGrapple();
                }
            }

            movementController.SimulateAuthoritativeMovement();
            movementController.SimulateAuthoritativeCrouch(IsOwner ? fpCamera : null);
            EndMovementSimulationDeltaOverride();
        }

        private void AnticipateClientPredictedTransform(PredictedMovementState anticipatedSnapshot) {
            if(clientNetworkTransform == null) return;
            _movementAuthorityPredictedYawDegrees = anticipatedSnapshot.Rotation.eulerAngles.y;
            _hasMovementAuthorityPredictedYaw = true;
            clientNetworkTransform.AnticipateState(new AnticipatedNetworkTransform.TransformState {
                Position = anticipatedSnapshot.Position,
                Rotation = anticipatedSnapshot.Rotation,
                Scale = Vector3.one
            });
        }

        private void BeginMovementSimulationDeltaOverride(float deltaTime) {
            _movementSimulationDeltaTimeOverride = Mathf.Max(0.0001f, deltaTime);
            _isMovementSimulationDeltaTimeOverridden = true;
        }

        private void EndMovementSimulationDeltaOverride() {
            _isMovementSimulationDeltaTimeOverridden = false;
            _movementSimulationDeltaTimeOverride = 0f;
        }

        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable, InvokePermission = RpcInvokePermission.Owner)]
        private void SubmitMovementInputCommandServerRpc(MovementInputCommand command, RpcParams rpcParams = default) {
            if(rpcParams.Receive.SenderClientId != OwnerClientId) {
                return;
            }

            BufferServerMovementCommand(command, Time.timeAsDouble);
        }

        [Rpc(SendTo.Owner, Delivery = RpcDelivery.Unreliable)]
        private void ReceiveMovementAuthoritySnapshotOwnerRpc(MovementAuthoritativeSnapshot snapshot) {
            ApplyReceivedMovementAuthoritySnapshot(snapshot);
        }

        public override void OnReanticipate(double lastRoundTripTime) {
            base.OnReanticipate(lastRoundTripTime);

            if(!IsOwner) return;
            if(clientNetworkTransform == null || !clientNetworkTransform.ShouldReanticipate) return;
            if(_latestAuthoritativeMovementSnapshot.Tick == 0) return;
            if(!ShouldUseBasicMovementAuthoritySliceLocally()) return;

            ReconcileOwnerPredictedMovement(_latestAuthoritativeMovementSnapshot);
        }

        private void ApplyOwnerLookForMovementAuthoritySlice(MovementInputCommand command) {
            if(!ShouldUseBasicMovementAuthoritySliceLocally()) {
                _hasMovementAuthorityPredictedYaw = false;
                _hasMovementAuthorityVisualYaw = false;
                return;
            }
        }

        internal void UpdateOwnerRenderLookForMovementAuthoritySlice() {
            if(!ShouldUseBasicMovementAuthoritySliceLocally()) {
                _hasMovementAuthorityVisualYaw = false;
                if(lookController != null) {
                    lookController.ResetViewPresentationOffsets();
                }
                return;
            }

            if(lookController == null || playerInput == null) return;

            if(!_hasMovementAuthorityVisualYaw) {
                _movementAuthorityVisualYawDegrees = playerTransform != null ? playerTransform.eulerAngles.y : transform.eulerAngles.y;
                _hasMovementAuthorityVisualYaw = true;
            }

            var lookDelta = playerInput.ConsumeMovementAuthorityRenderLookInput();
            var rootYaw = playerTransform != null ? playerTransform.eulerAngles.y : transform.eulerAngles.y;

            if(lookDelta != Vector2.zero) {
                lookController.ApplyPitchAndPresentation(lookDelta.y, lookDelta.x);
                _movementAuthorityVisualYawDegrees = NormalizeMovementAuthorityYawDegrees(_movementAuthorityVisualYawDegrees + lookDelta.x);
            }

            var viewYawOffset = Mathf.DeltaAngle(rootYaw, _movementAuthorityVisualYawDegrees);
            lookController.SetViewYawOffset(viewYawOffset);
        }

        private float GetMovementAuthorityCommandYawDegrees(float lookYawDelta) {
            if(!ShouldUseBasicMovementAuthoritySliceLocally()) {
                return playerTransform != null ? playerTransform.eulerAngles.y : transform.eulerAngles.y;
            }

            if(!_hasMovementAuthorityPredictedYaw) {
                _movementAuthorityPredictedYawDegrees = playerTransform != null ? playerTransform.eulerAngles.y : transform.eulerAngles.y;
                _hasMovementAuthorityPredictedYaw = true;
            }

            return NormalizeMovementAuthorityYawDegrees(_movementAuthorityPredictedYawDegrees + lookYawDelta);
        }

        private float NormalizeMovementAuthorityYawDegrees(float yawDegrees) {
            yawDegrees %= 360f;
            if(yawDegrees < 0f) {
                yawDegrees += 360f;
            }

            return yawDegrees;
        }

        private bool ShouldSkipMovementAuthorityReconcile(bool hasPredictedSnapshot, MovementAuthorityFlags predictedFlags,
            MovementAuthorityFlags authoritativeFlags, float positionErrorMeters, float yawErrorDegrees,
            float horizontalVelocityError, float verticalVelocityError) {
            if(!hasPredictedSnapshot) {
                return false;
            }

            if(predictedFlags != authoritativeFlags) {
                return false;
            }

            if(positionErrorMeters > MovementAuthorityNoopPositionErrorThresholdMeters) {
                return false;
            }

            if(yawErrorDegrees > MovementAuthorityNoopYawErrorThresholdDegrees) {
                return false;
            }

            if(horizontalVelocityError > MovementAuthorityNoopHorizontalVelocityErrorThreshold) {
                return false;
            }

            if(verticalVelocityError > MovementAuthorityNoopVerticalVelocityErrorThreshold) {
                return false;
            }

            return true;
        }

        private void LogMovementAuthoritySimulationDiagnostics(MovementAuthoritativeSnapshot authoritativeSnapshot,
            bool hasPredictedSnapshot, MovementAuthorityFlags predictedFlags, float positionErrorMeters,
            float yawErrorDegrees, float horizontalVelocityError, float verticalVelocityError, int replayCount) {
            var latestLocalTick = _nextMovementAuthorityTick > 0 ? _nextMovementAuthorityTick - 1 : 0;
            var tickGap = latestLocalTick >= authoritativeSnapshot.Tick ? latestLocalTick - authoritativeSnapshot.Tick : 0;
            if(!_predictedMovementSnapshots.TryGetValue(authoritativeSnapshot.Tick, out var predictedSnapshot)) {
                predictedSnapshot = default;
            }

            Debug.Log(
                $"[MoveSimDbg] tick={authoritativeSnapshot.Tick} latest={latestLocalTick} gap={tickGap} replay={replayCount} " +
                $"hasPred={hasPredictedSnapshot} posErr={positionErrorMeters:0.###} yawErr={yawErrorDegrees:0.###} " +
                $"hVelErr={horizontalVelocityError:0.###} vVelErr={verticalVelocityError:0.###} " +
                $"predFlags={FormatMovementAuthorityFlags(predictedFlags)} authFlags={FormatMovementAuthorityFlags(authoritativeSnapshot.Flags)} " +
                $"predInt={FormatMovementAuthorityVector(predictedSnapshot.IntendedDisplacement)} authInt={FormatMovementAuthorityVector(authoritativeSnapshot.IntendedDisplacement)} " +
                $"predAct={FormatMovementAuthorityVector(predictedSnapshot.ActualDisplacement)} authAct={FormatMovementAuthorityVector(authoritativeSnapshot.ActualDisplacement)} " +
                $"predCol={FormatCollisionFlags(predictedSnapshot.CollisionFlags)} authCol={FormatCollisionFlags(authoritativeSnapshot.CollisionFlags)} " +
                $"predGround={predictedSnapshot.GroundedBeforeMove}->{predictedSnapshot.GroundedAfterMove} " +
                $"authGround={authoritativeSnapshot.GroundedBeforeMove}->{authoritativeSnapshot.GroundedAfterMove}",
                this);
        }

        private static string FormatMovementAuthorityFlags(MovementAuthorityFlags flags) {
            return flags == MovementAuthorityFlags.None ? "None" : flags.ToString();
        }

        private static string FormatMovementAuthorityVector(Vector3 value) {
            return $"{value.x:0.###},{value.y:0.###},{value.z:0.###}";
        }

        private static string FormatCollisionFlags(int flagsValue) {
            var flags = (CollisionFlags)flagsValue;
            return flags == CollisionFlags.None ? "None" : flags.ToString();
        }
    }
}
