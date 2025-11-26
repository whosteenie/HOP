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
            playerController ??= GetComponent<PlayerController>();
            
            _fpCamera ??= playerController.FpCamera;
            _deathCamera = playerController.DeathCamera;
            _speedTrail ??= playerController.SpeedTrail;
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