using System.Collections.Generic;
using Unity.Netcode;
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
            public MovementAuthorityFlags Flags;
            public double ClientLocalTime;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
                serializer.SerializeValue(ref Tick);
                serializer.SerializeValue(ref DeltaTime);
                serializer.SerializeValue(ref MoveInput);
                serializer.SerializeValue(ref LookInput);
                serializer.SerializeValue(ref Flags);
                serializer.SerializeValue(ref ClientLocalTime);
            }
        }

        private struct MovementAuthoritativeSnapshot : INetworkSerializable {
            public uint Tick;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 HorizontalVelocity;
            public float VerticalVelocity;
            public MovementAuthorityFlags Flags;
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
        private float _movementAuthorityTickInterval;
        private float _movementAuthorityOwnerAccumulator;
        private float _movementAuthorityServerAccumulator;
        private uint _nextMovementAuthorityTick = 1;
        private bool _lastMovementAuthorityJumpHeld;
        private bool _lastMovementAuthorityGrappleHeld;
        private readonly Queue<uint> _predictedMovementTicks = new();
        private readonly Dictionary<uint, MovementAuthoritativeSnapshot> _predictedMovementSnapshots = new();
        private readonly Queue<uint> _authoritativeMovementTicks = new();
        private readonly Dictionary<uint, MovementAuthoritativeSnapshot> _authoritativeMovementSnapshots = new();
        private readonly Queue<BufferedMovementCommand> _serverBufferedMovementCommands = new();
        private MovementInputCommand _latestServerMovementCommand;
        private bool _hasLatestServerMovementCommand;
        private MovementAuthoritativeSnapshot _latestAuthoritativeMovementSnapshot;
        private float _latestMovementAuthorityErrorMeters;

        public float LatestMovementAuthorityErrorMeters => _latestMovementAuthorityErrorMeters;

        private void ResetMovementAuthorityFoundationState() {
            _movementAuthorityOwnerAccumulator = 0f;
            _movementAuthorityServerAccumulator = 0f;
            _nextMovementAuthorityTick = 1;
            _lastMovementAuthorityJumpHeld = false;
            _lastMovementAuthorityGrappleHeld = false;
            _predictedMovementTicks.Clear();
            _predictedMovementSnapshots.Clear();
            _authoritativeMovementTicks.Clear();
            _authoritativeMovementSnapshots.Clear();
            _serverBufferedMovementCommands.Clear();
            _latestServerMovementCommand = default;
            _hasLatestServerMovementCommand = false;
            _latestAuthoritativeMovementSnapshot = default;
            _latestMovementAuthorityErrorMeters = 0f;
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
                tickRate = Mathf.Max(1u, NetworkManager.NetworkConfig.TickRate);
            }

            _movementAuthorityTickInterval = 1f / tickRate;
        }

        private void TickMovementAuthorityOwnerCommands() {
            if(_movementAuthorityTickInterval <= 0f) return;

            var tickBudget = 0;
            while(_movementAuthorityOwnerAccumulator >= _movementAuthorityTickInterval && tickBudget < 4) {
                _movementAuthorityOwnerAccumulator -= _movementAuthorityTickInterval;
                var command = BuildMovementInputCommand();
                var predictedSnapshot = CaptureMovementAuthoritySnapshot(command.Tick, Time.timeAsDouble);
                BufferPredictedMovementSnapshot(predictedSnapshot);

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
            while(_movementAuthorityServerAccumulator >= _movementAuthorityTickInterval && tickBudget < 4) {
                _movementAuthorityServerAccumulator -= _movementAuthorityTickInterval;
                if(!_hasLatestServerMovementCommand) {
                    tickBudget++;
                    continue;
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

            var flags = MovementAuthorityFlags.None;
            if(sprintInput) flags |= MovementAuthorityFlags.SprintHeld;
            if(crouchInput) flags |= MovementAuthorityFlags.CrouchHeld;
            if(jumpHeld) flags |= MovementAuthorityFlags.JumpHeld;
            if(grappleHeld) flags |= MovementAuthorityFlags.GrappleHeld;
            if(jumpHeld && !_lastMovementAuthorityJumpHeld) flags |= MovementAuthorityFlags.JumpPressed;
            if(grappleHeld && !_lastMovementAuthorityGrappleHeld) flags |= MovementAuthorityFlags.GrapplePressed;

            _lastMovementAuthorityJumpHeld = jumpHeld;
            _lastMovementAuthorityGrappleHeld = grappleHeld;

            return new MovementInputCommand {
                Tick = _nextMovementAuthorityTick++,
                DeltaTime = _movementAuthorityTickInterval,
                MoveInput = moveInput,
                LookInput = lookInput,
                Flags = flags,
                ClientLocalTime = Time.timeAsDouble
            };
        }

        private MovementAuthoritativeSnapshot CaptureMovementAuthoritySnapshot(uint tick, double timestamp) {
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

            return new MovementAuthoritativeSnapshot {
                Tick = tick,
                Position = playerTransform != null ? playerTransform.position : transform.position,
                Rotation = playerTransform != null ? playerTransform.rotation : transform.rotation,
                HorizontalVelocity = movementController != null ? movementController.HorizontalVelocity : Vector3.zero,
                VerticalVelocity = movementController != null ? movementController.VerticalVelocity : 0f,
                Flags = flags,
                WallNormal = wallRunController != null ? wallRunController.CurrentWallNormal : Vector3.zero,
                GrapplePoint = grappleController != null ? grappleController.CurrentGrapplePoint : Vector3.zero,
                MantleTargetPosition = mantleController != null ? mantleController.CurrentMantleTargetPosition : Vector3.zero,
                MantleProgress01 = mantleController != null ? mantleController.CurrentMantleProgress01 : 0f,
                ServerTime = timestamp
            };
        }

        private void BufferPredictedMovementSnapshot(MovementAuthoritativeSnapshot snapshot) {
            _predictedMovementSnapshots[snapshot.Tick] = snapshot;
            _predictedMovementTicks.Enqueue(snapshot.Tick);
            TrimMovementSnapshotBuffer(_predictedMovementTicks, _predictedMovementSnapshots);
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
            _serverBufferedMovementCommands.Enqueue(new BufferedMovementCommand {
                Command = command,
                ReceivedServerTime = receivedServerTime
            });

            while(_serverBufferedMovementCommands.Count > MovementAuthorityFoundationBufferSize) {
                _serverBufferedMovementCommands.Dequeue();
            }
        }

        private void ApplyReceivedMovementAuthoritySnapshot(MovementAuthoritativeSnapshot snapshot) {
            _latestAuthoritativeMovementSnapshot = snapshot;
            BufferAuthoritativeMovementSnapshot(snapshot);

            if(_predictedMovementSnapshots.TryGetValue(snapshot.Tick, out var predictedSnapshot)) {
                _latestMovementAuthorityErrorMeters =
                    Vector3.Distance(predictedSnapshot.Position, snapshot.Position);
                return;
            }

            _latestMovementAuthorityErrorMeters = 0f;
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
    }
}
