using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class DeathCameraController : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private PlayerController playerController;

        private CinemachineCamera _fpCamera;
        private CinemachineCamera _deathCamera;

        private SpeedTrail _speedTrail;

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
            if(_speedTrail == null) _speedTrail = playerController.SpeedTrail;
        }

        public void EnableDeathCamera() {
            playerController.PlayerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _deathCamera.Priority = _fpCamera.Priority + 1;
            _deathCamera.gameObject.SetActive(true);
        }

        public void DisableDeathCamera() {
            _deathCamera.gameObject.SetActive(false);
            playerController.PlayerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _deathCamera.Priority = _fpCamera.Priority - 1;

            playerController.SpeedTrail.ClearAllTrails();
        }
    }
}