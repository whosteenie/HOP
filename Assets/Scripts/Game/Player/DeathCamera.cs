using Game.Weapons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class DeathCamera : NetworkBehaviour {
        [Header("References")] [SerializeField]
        private CinemachineCamera fpCamera;

        [SerializeField] private CinemachineCamera deathCamera;
        [SerializeField] private SkinnedMeshRenderer playerMesh;
        [SerializeField] private WeaponManager weaponManager;
        [SerializeField] private SpeedTrail speedTrail;

        public void EnableDeathCamera() {
            playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            deathCamera.Priority = fpCamera.Priority + 1;
            deathCamera.gameObject.SetActive(true);
        }

        public void DisableDeathCamera() {
            deathCamera.gameObject.SetActive(false);
            playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            deathCamera.Priority = fpCamera.Priority - 1;
            
            // Hide owned speed trails when death cam is disabled (on respawn)
            if(speedTrail != null) {
                speedTrail.ClearAllTrails();
            }
        }
    }
}