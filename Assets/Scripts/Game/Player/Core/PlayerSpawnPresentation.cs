using Game.Match;
using Game.Menu;
using Game.Settings;
using Game.Social;
using Network.Core;
using Steamworks;
using UnityEngine.UIElements;

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
            ClearPausedMenuState();

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
            var gameMenu = GameMenuManager.Instance;
            if(gameMenu != null && gameMenu.TryGetComponent(out UIDocument doc)) {
                var root = doc.rootVisualElement;
                VisualElement rootContainer = null;
                if(root != null) {
                    rootContainer = root.Q<VisualElement>("root-container");
                }

                if(rootContainer != null) {
                    rootContainer.style.display = DisplayStyle.Flex;
                }
            }

            PlayerUiEventBridge.PublishShowHud();
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

        private static void ClearPausedMenuState() {
            if(GameMenuManager.Instance.IsPaused) {
                GameMenuManager.Instance.TogglePause();
            }
        }

        private void ApplyOwnerSpawnPresentation() {
            if(_player.FpCamera && _player.LookController != null) {
                _player.FpCamera.Lens.FieldOfView = _player.LookController.BaseFov;
            }

            var displayName = Social.StreamerMode.LocalDisplayName;
            var localSteamId = 0UL;
            if(SteamClient.IsValid && SteamClient.IsLoggedOn) {
                displayName = Social.StreamerMode.GetLocalDisplayName();
                localSteamId = SteamClient.SteamId.Value;
            }

            var ugsPlayerId = LocalIdentity.GetUgsPlayerId();
            _player.BeginIdentitySyncFromSpawn(localSteamId, ugsPlayerId, displayName);

            _player.primaryWeaponIndex.Value = GameSettings.Data.player.primaryWeaponIndex;
            _player.secondaryWeaponIndex.Value = GameSettings.Data.player.secondaryWeaponIndex;
            _player.LoadMaterialCustomizationFromPrefsForSpawn();

            PlayerUiEventBridge.PublishLocalPlayerReady(_player);

            var matchSettings = MatchSettingsManager.Instance;
            if(matchSettings != null && matchSettings.selectedGameModeId == "Gun Tag" && _player.TagController != null) {
                PlayerUiEventBridge.PublishTagStatus(_player.TagController.IsTagged.Value);
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
