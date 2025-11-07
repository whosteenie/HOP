using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class NetworkSoundRelay : NetworkBehaviour
{
    // simple anti-spam per key
    private readonly Dictionary<SFXKey, float> _lastSent = new();
    [SerializeField] private float walkMinInterval = 0.15f;
    [SerializeField] private float runMinInterval  = 0.12f;
    [SerializeField] private float landMinInterval = 0.25f;
    [SerializeField] private float jumpMinInterval = 0.20f;
    [SerializeField] private float reloadMinInterval = 0.10f;
    [SerializeField] private float dryMinInterval = 0.05f;
    [SerializeField] private float shootMinInterval = 0.01f;
    [SerializeField] private float jumpPadMinInterval = 0.25f;
    [SerializeField] private float grappleMinInterval = 0.10f;

    private float GetMinInterval(SFXKey key) => key switch
    {
        SFXKey.Walk    => walkMinInterval,
        SFXKey.Run     => runMinInterval,
        SFXKey.Land    => landMinInterval,
        SFXKey.Jump    => jumpMinInterval,
        SFXKey.Reload  => reloadMinInterval,
        SFXKey.Dry     => dryMinInterval,
        SFXKey.Shoot   => shootMinInterval,
        SFXKey.JumpPad => jumpPadMinInterval,
        SFXKey.Grapple => grappleMinInterval,
        _ => 0.1f
    };

    /// <summary>
    /// Call this locally on the owner when an SFX-worthy event happens.
    /// attachToSelf=true parents the AudioSource to this player so it follows them.
    /// </summary>
    public void RequestWorldSfx(SFXKey key, bool attachToSelf = true, bool allowOverlap = false)
    {
        // optional rate limit on the caller side as well
        var t = Time.time;
        if (_lastSent.TryGetValue(key, out var last) && t - last < GetMinInterval(key)) return;
        _lastSent[key] = t;

        RequestWorldSfxServerRpc(key, attachToSelf, allowOverlap);
    }

    [Rpc(SendTo.Server)]
    private void RequestWorldSfxServerRpc(SFXKey key, bool attachToSelf, bool allowOverlap)
    {
        // basic sanity
        if (!IsSpawned) return;

        NetworkObjectReference srcRef = new NetworkObjectReference(NetworkObject);
        Vector3 pos = transform.position;

        PlayWorldSfxClientRpc(key, srcRef, pos, attachToSelf, allowOverlap);
    }

    [Rpc(SendTo.Everyone)]
    private void PlayWorldSfxClientRpc(SFXKey key, NetworkObjectReference sourceRef, Vector3 pos, bool attachToSource, bool allowOverlap)
    {
        Transform parent = null;
        if (attachToSource && sourceRef.TryGet(out var no) && no != null)
            parent = no.transform;

        SoundFXManager.Instance?.PlayKey(key, parent, pos, allowOverlap);
    }
}
