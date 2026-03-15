namespace Game.Player.Core {
    internal sealed class PlayerPresentationState {
        private readonly PlayerController _player;

        public PlayerPresentationState(PlayerController player) {
            _player = player;
        }

        public void Subscribe() {
            _player.netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            _player.netIsCrouching.OnValueChanged += OnCrouchStateChanged;
            _player.netIsSliding.OnValueChanged -= OnSlidingStateChanged;
            _player.netIsSliding.OnValueChanged += OnSlidingStateChanged;
            _player.netIsJumping.OnValueChanged -= OnJumpingStateChanged;
            _player.netIsJumping.OnValueChanged += OnJumpingStateChanged;
            _player.netIsFalling.OnValueChanged -= OnFallingStateChanged;
            _player.netIsFalling.OnValueChanged += OnFallingStateChanged;
            _player.jumpAnimationSequence.OnValueChanged -= OnJumpAnimationSequenceChanged;
            _player.jumpAnimationSequence.OnValueChanged += OnJumpAnimationSequenceChanged;
            _player.landAnimationSequence.OnValueChanged -= OnLandAnimationSequenceChanged;
            _player.landAnimationSequence.OnValueChanged += OnLandAnimationSequenceChanged;
            _player.mantleAnimationSequence.OnValueChanged -= OnMantleAnimationSequenceChanged;
            _player.mantleAnimationSequence.OnValueChanged += OnMantleAnimationSequenceChanged;
            _player.netIsWallRunning.OnValueChanged -= OnWallRunStateChanged;
            _player.netIsWallRunning.OnValueChanged += OnWallRunStateChanged;
            _player.netIsRightWallRun.OnValueChanged -= OnWallRunOrientationChanged;
            _player.netIsRightWallRun.OnValueChanged += OnWallRunOrientationChanged;
            _player.netWallRunDirection.OnValueChanged -= OnWallRunDirectionChanged;
            _player.netWallRunDirection.OnValueChanged += OnWallRunDirectionChanged;
        }

        public void Unsubscribe() {
            _player.netIsCrouching.OnValueChanged -= OnCrouchStateChanged;
            _player.netIsSliding.OnValueChanged -= OnSlidingStateChanged;
            _player.netIsJumping.OnValueChanged -= OnJumpingStateChanged;
            _player.netIsFalling.OnValueChanged -= OnFallingStateChanged;
            _player.jumpAnimationSequence.OnValueChanged -= OnJumpAnimationSequenceChanged;
            _player.landAnimationSequence.OnValueChanged -= OnLandAnimationSequenceChanged;
            _player.mantleAnimationSequence.OnValueChanged -= OnMantleAnimationSequenceChanged;
            _player.netIsWallRunning.OnValueChanged -= OnWallRunStateChanged;
            _player.netIsRightWallRun.OnValueChanged -= OnWallRunOrientationChanged;
            _player.netWallRunDirection.OnValueChanged -= OnWallRunDirectionChanged;
        }

        public void OnHealthChanged(float newHealthValue) {
            if(_player.IsOwner) {
                PlayerUiEventBridge.PublishHealthUpdated(newHealthValue, 100f);
            }
        }

        public void OnDeathStateChanged(bool isDead) {
            if(isDead == false) return;

            var characterController = _player.CharacterController;
            if(characterController != null) {
                characterController.enabled = false;
            }

            _player.ClearTriggerOobCountdownFromPresentation();
            if(_player.IsOwner) {
                _player.HideTriggerOobCountdownLocalFromPresentation();
            }
        }

        private void OnCrouchStateChanged(bool _, bool __) {
            var movementController = _player.MovementController;
            if(movementController != null) {
                movementController.UpdateCrouch(_player.FpCamera);
            }
        }

        private void OnSlidingStateChanged(bool oldValue, bool newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null) return;
            animationController.ApplyRemoteSlidingState(newValue, playTrigger: newValue && !oldValue);
        }

        private void OnJumpingStateChanged(bool _, bool newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null) return;
            animationController.ApplyRemoteJumpingState(newValue);
        }

        private void OnFallingStateChanged(bool _, bool newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null) return;
            animationController.ApplyRemoteFallingState(newValue);
        }

        private void OnJumpAnimationSequenceChanged(int oldValue, int newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteJumpAnimation();
        }

        private void OnLandAnimationSequenceChanged(int oldValue, int newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteLandingAnimation();
        }

        private void OnMantleAnimationSequenceChanged(int oldValue, int newValue) {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null || newValue == oldValue) return;
            animationController.PlayRemoteMantleAnimation();
        }

        private void OnWallRunStateChanged(bool _, bool __) {
            RefreshRemoteWallRunState();
        }

        private void OnWallRunOrientationChanged(bool _, bool __) {
            RefreshRemoteWallRunState();
        }

        private void OnWallRunDirectionChanged(float _, float __) {
            RefreshRemoteWallRunState();
        }

        private void RefreshRemoteWallRunState() {
            var animationController = _player.AnimationController;
            if(_player.IsOwner || animationController == null) return;

            animationController.ApplyRemoteWallRunState(_player.NetIsWallRunning.Value, _player.NetIsRightWallRun.Value,
                _player.NetWallRunDirection.Value);
        }
    }
}
