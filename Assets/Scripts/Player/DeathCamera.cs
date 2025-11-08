using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using Weapon;

namespace Player {
    public class DeathCamera : NetworkBehaviour {
        [Header("References")]
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private CinemachineCamera deathCamera;
        [SerializeField] private SkinnedMeshRenderer playerMesh;
        [SerializeField] private WeaponManager weaponManager;

        public void EnableDeathCamera() {
            playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            deathCamera.Priority = fpCamera.Priority + 1;
        }

        public void DisableDeathCamera() {
            playerMesh.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            deathCamera.Priority = fpCamera.Priority - 1;
        }
    }
}