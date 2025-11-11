using System.Collections.Generic;
using Network.Singletons;
using Unity.Netcode;
using UnityEngine;

namespace Network.Rpc {
    public class NetworkSfxRelay : NetworkBehaviour {
        // simple anti-spam per key
        private readonly Dictionary<SfxKey, float> _lastSent = new();
        [SerializeField] private float walkMinInterval = 0.10f;
        [SerializeField] private float runMinInterval  = 0.10f;
        [SerializeField] private float landMinInterval = 0.25f;
        [SerializeField] private float jumpMinInterval = 0.10f;
        [SerializeField] private float reloadMinInterval = 0.10f;
        [SerializeField] private float dryMinInterval = 0.05f;
        [SerializeField] private float shootMinInterval = 0.01f;
        [SerializeField] private float jumpPadMinInterval = 0.25f;
        [SerializeField] private float grappleMinInterval = 0.10f;

        private float GetMinInterval(SfxKey key) => key switch {
            SfxKey.Walk    => walkMinInterval,
            SfxKey.Run     => runMinInterval,
            SfxKey.Land    => landMinInterval,
            SfxKey.Jump    => jumpMinInterval,
            SfxKey.Reload  => reloadMinInterval,
            SfxKey.Dry     => dryMinInterval,
            SfxKey.Shoot   => shootMinInterval,
            SfxKey.JumpPad => jumpPadMinInterval,
            SfxKey.Grapple => grappleMinInterval,
            _ => 0.1f
        };

        /// <summary>
        /// Call this locally on the owner when an SFX-worthy event happens.
        /// attachToSelf=true parents the AudioSource to this player so it follows them.
        /// </summary>
        public void RequestWorldSfx(SfxKey key, bool attachToSelf = true, bool allowOverlap = false) {
            // optional rate limit on the caller side as well
            var t = Time.time;
            if (_lastSent.TryGetValue(key, out var last) && t - last < GetMinInterval(key)) return;
            _lastSent[key] = t;

            RequestWorldSfxServerRpc(key, attachToSelf, allowOverlap);
        }

        [Rpc(SendTo.Server)]
        private void RequestWorldSfxServerRpc(SfxKey key, bool attachToSelf, bool allowOverlap) {
            // basic sanity
            if (!IsSpawned) return;

            var srcRef = new NetworkObjectReference(NetworkObject);
            var pos = transform.position;

            PlayWorldSfxClientRpc(key, srcRef, pos, attachToSelf, allowOverlap);
        }

        [Rpc(SendTo.Everyone)]
        private void PlayWorldSfxClientRpc(SfxKey key, NetworkObjectReference sourceRef, Vector3 pos, bool attachToSource, bool allowOverlap) {
            Transform parent = null;
            if(attachToSource && sourceRef.TryGet(out var no) && no != null)
                parent = no.transform;

            SoundFXManager.Instance?.PlayKey(key, parent, pos, allowOverlap);
        }
    }
}
