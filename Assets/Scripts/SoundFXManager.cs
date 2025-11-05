using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SoundFXManager : MonoBehaviour {
    public static SoundFXManager Instance;

    [SerializeField] private AudioSource soundFXPrefab; // Assign in inspector
    [SerializeField] private int poolSize = 20;

    private readonly Queue<AudioSource> _audioPool = new();
    private readonly Dictionary<string, AudioSource> _activeSounds = new(); // Track by soundType

    private void Awake() {
        if(Instance == null) {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializePool();
        } else {
            Destroy(gameObject);
        }
    }

    private void InitializePool() {
        for(var i = 0; i < poolSize; i++) {
            var obj = Instantiate(soundFXPrefab, transform);
            obj.gameObject.SetActive(false);
            _audioPool.Enqueue(obj);
        }
    }

    private AudioSource GetPooledAudioSource() {
        if(_audioPool.Count > 0) {
            var source = _audioPool.Dequeue();
            source.gameObject.SetActive(true);
            return source;
        }

        // Fallback: create new
        var newObj = Instantiate(soundFXPrefab, transform);
        newObj.gameObject.SetActive(true);
        return newObj;
    }

    public void PlaySoundFX(AudioClip clip, Transform spawnTransform, bool allowOverlap, string soundType) {
        PlayClip(clip, spawnTransform.position, allowOverlap, soundType);
    }

    public void PlayRandomSoundFX(AudioClip[] clips, Transform spawnTransform, bool allowOverlap, string soundType) {
        if(clips.Length == 0) return;
        var clip = clips[Random.Range(0, clips.Length)];
        PlayClip(clip, spawnTransform.position, allowOverlap, soundType);
    }

    private void PlayClip(AudioClip clip, Vector3 position, bool allowOverlap, string soundType) {
        // Special case: "walk" or "run" → allow up to 2
        if(!allowOverlap && soundType is "walk" or "run") {
            var activeCount = 0;
            foreach(var kvp in _activeSounds) {
                if(kvp.Key.StartsWith(soundType) && kvp.Value.isPlaying)
                    activeCount++;
            }

            if(activeCount >= 2) return; // Block if 2 already playing
        }
        // Normal case: any other soundType → only 1
        else if(!allowOverlap && _activeSounds.TryGetValue(soundType, out var sound)) {
            if(sound.isPlaying) return;
        }

        var source = GetPooledAudioSource();
        source.transform.position = position;
        source.clip = clip;

        // Volume
        var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
        var dbSfx = PlayerPrefs.GetFloat("SFXVolume", 0f);
        source.volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);

        // Track active
        _activeSounds[soundType] = source;

        source.Play();

        // Return to pool when done
        StartCoroutine(ReturnToPoolAfterDelay(source, clip.length, soundType));
    }

    private IEnumerator ReturnToPoolAfterDelay(AudioSource source, float delay, string trackKey) {
        yield return new WaitForSeconds(delay);

        _activeSounds.Remove(trackKey);
        source.Stop();
        source.clip = null;
        source.gameObject.SetActive(false);
        _audioPool.Enqueue(source);
    }

    private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);
}