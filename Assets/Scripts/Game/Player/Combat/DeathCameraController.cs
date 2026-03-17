using Game.Player.Core;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Combat {
    public class DeathCameraController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CinemachineCamera _fpCamera;
        private CinemachineCamera _deathCamera;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerController == null) {
                Debug.LogError("[DeathCameraController] PlayerController not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) _fpCamera = playerController.FpCamera;
            if(_deathCamera == null) _deathCamera = playerController.DeathCamera;
        }

        /// <summary>
        /// Enables the death camera and sets its priority.
        /// </summary>
        public void EnableDeathCamera() {
            playerController.PlayerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _deathCamera.Priority = _fpCamera.Priority + 1;
            _deathCamera.gameObject.SetActive(true);
        }

        /// <summary>
        /// Disables the death camera.
        /// </summary>
        public void DisableDeathCamera() {
            _deathCamera.gameObject.SetActive(false);
            playerController.PlayerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _deathCamera.Priority = _fpCamera.Priority - 1;
        }
    }
}
