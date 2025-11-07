// SoundFXManager.cs (replace/extend your current version)
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SoundFXManager : MonoBehaviour
{
    public static SoundFXManager Instance;

    [Header("Pool")]
    [SerializeField] private AudioSource soundFXPrefab;
    [SerializeField] private int poolSize = 32;

    // Banks for each gameplay key (assign in inspector)
    [Header("Banks")]
    [SerializeField] private AudioClip[] walkClips;
    [SerializeField] private AudioClip[] runClips;
    [SerializeField] private AudioClip[] jumpClips;
    [SerializeField] private AudioClip[] landClips;
    [SerializeField] private AudioClip[] reloadClips;
    [SerializeField] private AudioClip[] dryClips;
    [SerializeField] private AudioClip[] shootClips;
    [SerializeField] private AudioClip[] jumpPadClips;
    [SerializeField] private AudioClip[] grappleClips;

    private readonly Queue<AudioSource> _audioPool = new();
    private readonly Dictionary<string, AudioSource> _activeSounds = new();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        InitializePool();
    }

    private void InitializePool()
    {
        for (int i = 0; i < poolSize; i++)
        {
            var src = Instantiate(soundFXPrefab, transform);
            src.gameObject.SetActive(false);
            _audioPool.Enqueue(src);
        }
    }

    private AudioSource GetPooled()
    {
        if (_audioPool.Count > 0)
        {
            var s = _audioPool.Dequeue();
            s.gameObject.SetActive(true);
            return s;
        }
        return Instantiate(soundFXPrefab, transform);
    }

    private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);

    private AudioClip PickRandomFrom(SFXKey key)
    {
        AudioClip[] bank = key switch
        {
            SFXKey.Walk    => walkClips,
            SFXKey.Run     => runClips,
            SFXKey.Jump    => jumpClips,
            SFXKey.Land    => landClips,
            SFXKey.Reload  => reloadClips,
            SFXKey.Dry     => dryClips,
            SFXKey.Shoot   => shootClips,
            SFXKey.JumpPad => jumpPadClips,
            SFXKey.Grapple => grappleClips,
            _ => null
        };
        if (bank == null || bank.Length == 0) return null;
        return bank[Random.Range(0, bank.Length)];
    }

    /// <summary>
    /// Network-consumed entrypoint. If parent != null, the AudioSource is parented (follows player).
    /// Else, it's placed at the provided world position.
    /// </summary>
    public void PlayKey(SFXKey key, Transform parent, Vector3 worldPos, bool allowOverlap)
    {
        var clip = PickRandomFrom(key);
        if (clip == null) return;

        // overlap policy (shares your old logic; keep simple per-key gate)
        string trackKey = key.ToString();
        if (!allowOverlap && _activeSounds.TryGetValue(trackKey, out var playing) && playing != null && playing.isPlaying)
            return;

        var src = GetPooled();

        // parent vs world-pos
        if (parent != null)
        {
            src.transform.SetParent(parent, false);
            src.transform.localPosition = Vector3.zero;
        }
        else
        {
            src.transform.SetParent(transform, false);
            src.transform.position = worldPos;
        }
        
        src.spatialBlend = 1f;
        src.minDistance = 1f;
        src.maxDistance = 50f;

        // apply volumes from PlayerPrefs (per-client mixer-like control)
        var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
        var dbSfx    = PlayerPrefs.GetFloat("SFXVolume", 0f);
        src.volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);

        src.clip = clip;
        _activeSounds[trackKey] = src;
        src.Play();

        StartCoroutine(ReturnAfter(src, clip.length, trackKey));
    }

    private IEnumerator ReturnAfter(AudioSource src, float delay, string trackKey)
    {
        yield return new WaitForSeconds(delay);
        if (_activeSounds.TryGetValue(trackKey, out var cur) && cur == src)
            _activeSounds.Remove(trackKey);
        src.Stop();
        src.clip = null;
        src.transform.SetParent(transform, false);
        src.gameObject.SetActive(false);
        _audioPool.Enqueue(src);
    }

    // Keep your UI/local helpers if you like:
    public void PlayUISound(AudioClip clip)
    {
        if (clip == null) return;
        var src = GetPooled();
        // UI is non-spatial; parent to manager
        src.transform.SetParent(transform, false);
        src.transform.localPosition = Vector3.zero;

        src.spatialBlend = 0f;
        src.minDistance = 1f;
        src.maxDistance = 500f;

        var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
        var dbSfx    = PlayerPrefs.GetFloat("SFXVolume", 0f);
        src.volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);

        src.clip = clip;
        src.Play();
        StartCoroutine(ReturnAfter(src, clip.length, "ui_" + clip.GetInstanceID()));
    }
}