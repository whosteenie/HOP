using UnityEngine;

public class SoundFXManager : MonoBehaviour
{
    public static SoundFXManager Instance;

    [SerializeField] private AudioSource soundFXObject;
    
    private void Awake() {
        if(Instance == null) {
            Instance = this;
        }
    }

    public void PlaySoundFX(AudioClip clip, Transform spawnTransform) {
        PlayClip(clip, spawnTransform);
    }
    
    public void PlayRandomSoundFX(AudioClip[] clips, Transform spawnTransform) {
        if(clips.Length == 0) {
            Debug.LogWarning("No AudioClips provided to PlayRandomSoundFX.");
            return;
        }
        
        var clip = clips[Random.Range(0, clips.Length)];
        
        PlayClip(clip, spawnTransform);
    }

    private void PlayClip(AudioClip clip, Transform spawnTransform) {
        // spawn GameObject
        var audioSource = Instantiate(soundFXObject, spawnTransform.position, Quaternion.identity);
        
        // Assign AudioClip
        audioSource.clip = clip;
        
        // Calculate volume
        var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
        var dbSfx = PlayerPrefs.GetFloat("SFXVolume", 0f);
        var volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);
        
        // Assign volume
        audioSource.volume = volume;

        // Play sound
        audioSource.Play();
        
        // Get length of soundFX
        var clipLength = audioSource.clip.length;
        
        // Destroy GameObject
        Destroy(audioSource.gameObject, clipLength);
    }

    private static float DbToLinear(float db) {
        return db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);
    }
}
