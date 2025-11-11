using UnityEngine;

namespace Network.Singletons {
    public class MenuMusicPlayer : MonoBehaviour {
        [Header("Menu Music")]
        [SerializeField] private AudioClip[] menuMusicTracks;
        [SerializeField] private AudioSource musicSource;
        
        [Header("Settings")]
        [SerializeField] private bool shuffleTracks = true;
        [SerializeField] private float fadeTime = 2f;
        
        private int _currentTrackIndex;
        
        private void Start() {
            if(musicSource == null) {
                musicSource = gameObject.AddComponent<AudioSource>();
            }
            
            musicSource.loop = false;
            musicSource.playOnAwake = false;
            
            PlayMenuMusic();
        }
        
        private void Update() {
            // Auto-advance to next track
            if(!musicSource.isPlaying && menuMusicTracks != null && menuMusicTracks.Length > 0) {
                PlayNextTrack();
            }
        }
        
        private void PlayMenuMusic() {
            if(menuMusicTracks == null || menuMusicTracks.Length == 0) return;
            
            _currentTrackIndex = shuffleTracks ? Random.Range(0, menuMusicTracks.Length) : 0;
            StartCoroutine(FadeIn(menuMusicTracks[_currentTrackIndex]));
        }
        
        private void PlayNextTrack() {
            if(shuffleTracks) {
                _currentTrackIndex = Random.Range(0, menuMusicTracks.Length);
            } else {
                _currentTrackIndex = (_currentTrackIndex + 1) % menuMusicTracks.Length;
            }
            
            musicSource.clip = menuMusicTracks[_currentTrackIndex];
            musicSource.Play();
        }
        
        private System.Collections.IEnumerator FadeIn(AudioClip clip) {
            musicSource.clip = clip;
            musicSource.volume = 0f;
            musicSource.Play();
            
            var elapsed = 0f;
            while(elapsed < fadeTime) {
                elapsed += Time.deltaTime;
                musicSource.volume = Mathf.Lerp(0f, 1f, elapsed / fadeTime);
                yield return null;
            }
            
            musicSource.volume = 1f;
        }
    }
}