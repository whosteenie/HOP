using Game.Player.Contracts;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player.Combat {
    public class DeathCameraController : NetworkBehaviour {
        [Header("References")]
        [HideInInspector, SerializeField] private MonoBehaviour playerContextSource;

        private IPlayerDeathCameraContext _playerContext;

        private CinemachineCamera _fpCamera;
        private CinemachineCamera _deathCamera;
        private SkinnedMeshRenderer _playerMesh;

        private void Awake() {
            ValidateComponents();
        }

        private void ValidateComponents() {
            if(!PlayerContractResolver.TryResolve(this, ref playerContextSource, out _playerContext)) {
                Debug.LogError("[DeathCameraController] IPlayerDeathCameraContext not found!");
                enabled = false;
                return;
            }

            if(_fpCamera == null) _fpCamera = _playerContext.FpCamera;
            if(_deathCamera == null) _deathCamera = _playerContext.DeathCamera;
            if(_playerMesh == null) _playerMesh = _playerContext.PlayerMesh;
        }

        /// <summary>
        /// Enables the death camera and sets its priority.
        /// </summary>
        public void EnableDeathCamera() {
            if(_playerMesh != null) _playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            _deathCamera.Priority = _fpCamera.Priority + 1;
            _deathCamera.gameObject.SetActive(true);
        }

        /// <summary>
        /// Disables the death camera.
        /// </summary>
        public void DisableDeathCamera() {
            _deathCamera.gameObject.SetActive(false);
            if(_playerMesh != null) _playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            _deathCamera.Priority = _fpCamera.Priority - 1;
        }
    }
}

