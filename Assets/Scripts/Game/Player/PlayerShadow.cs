using Unity.Netcode;
using UnityEngine;

namespace Game.Player {
    public class PlayerShadow : NetworkBehaviour
    {
        public override void OnNetworkSpawn() {
            base.OnNetworkSpawn();

            if(!IsOwner) return;
        
            var skinnedRenderers = GetComponentsInChildren<SkinnedMeshRenderer>();
            var meshRenderers = GetComponentsInChildren<MeshRenderer>();

            foreach(var meshRenderer in meshRenderers) {
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }

            foreach(var skinnedRenderer in skinnedRenderers) {
                skinnedRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
    }
}
