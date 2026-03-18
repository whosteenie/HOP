using Events;
using Game.Settings;
using Game.Social;
using Steamworks;

namespace Game.Player.Core {
    internal sealed class PlayerSpawnPresentation {
        private readonly PlayerController _player;

        public PlayerSpawnPresentation(PlayerController player) {
            _player = player;
        }

        public void HandleNetworkSpawnPresentation() {
            EnsureCharacterControllerEnabled();
            RestoreMenuAndHudPresentation();
            ResetAnimationStateForSpawn();

            if(_player.IsOwner) {
                ApplyOwnerSpawnPresentation();
                return;
            }

            ApplyRemoteSpawnPresentation();
        }

        private void EnsureCharacterControllerEnabled() {
            var characterController = _player.CharacterController;
            if(characterController != null && characterController.enabled == false && !_player.NetIsDead.Value) {
                characterController.enabled = true;
            }
        }

        private static void RestoreMenuAndHudPresentation() {
            EventBus.Publish(new RestoreGameplayMenuPresentationEvent());
            EventBus.Publish(new ShowHUDEvent());
        }

        private void ResetAnimationStateForSpawn() {
            var animationController = _player.AnimationController;
            if(animationController == null) return;

            animationController.ResetSpawnTime();
            if(_player.IsOwner) return;

            animationController.ApplyRemoteStateSnapshot(_player.NetIsJumping.Value, _player.NetIsFalling.Value,
                _player.NetIsSliding.Value);
            animationController.ApplyRemoteWallRunState(_player.NetIsWallRunning.Value, _player.NetIsRightWallRun.Value,
                _player.NetWallRunDirection.Value);
        }

        private void ApplyOwnerSpawnPresentation() {
            if(_player.FpCamera && _player.LookController != null) {
                _player.FpCamera.Lens.FieldOfView = _player.LookController.BaseFov;
            }

            var displayName = StreamerMode.LocalDisplayName;
            var localSteamId = 0UL;
            if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                displayName = StreamerMode.GetLocalDisplayName();
                localSteamId = SteamClient.SteamId.Value;
            }

            var ugsPlayerId = LocalIdentity.GetUgsPlayerId();
            _player.BeginIdentitySyncFromSpawn(localSteamId, ugsPlayerId, displayName);

            _player.primaryWeaponIndex.Value = GameSettings.Data.player.primaryWeaponIndex;
            _player.secondaryWeaponIndex.Value = GameSettings.Data.player.secondaryWeaponIndex;
            _player.LoadMaterialPrefsForSpawn();

            EventBus.Publish(new LocalPlayerReadyEvent(_player.OwnerClientId));

            if(PlayerMatchRules.IsGunTagMode && _player.TagController != null) {
                EventBus.Publish(new UpdateTagStatusEvent(_player.TagController.IsTagged.Value));
            }

            if(_player.PlayerShadow != null) {
                _player.PlayerShadow.ApplyOwnerDefaultShadowState();
            }
        }

        private void ApplyRemoteSpawnPresentation() {
            var playerModelRoot = _player.PlayerModelRoot;
            if(playerModelRoot != null && !playerModelRoot.activeSelf) {
                playerModelRoot.SetActive(true);
            }

            var visualController = _player.VisualController;
            if(visualController != null) {
                visualController.InvalidateRendererCache();
                visualController.SetRenderersEnabled(true);
                visualController.ForceRendererBoundsUpdate();
            }

            if(_player.PlayerShadow != null) {
                _player.PlayerShadow.ApplyVisibleShadowState();
            }
        }
    }
}
