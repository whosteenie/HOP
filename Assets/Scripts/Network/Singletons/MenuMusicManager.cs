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
        private int _previousTrackIndex = -1; // Track the last played song

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
            if(!musicSource.isPlaying && menuMusicTracks is { Length: > 0 }) {
                PlayNextTrack();
            }
        }

        private void PlayMenuMusic() {
            if(menuMusicTracks == null || menuMusicTracks.Length == 0) return;

            _currentTrackIndex = shuffleTracks ? Random.Range(0, menuMusicTracks.Length) : 0;
            _previousTrackIndex = _currentTrackIndex; // Set initial previous track
            StartCoroutine(FadeIn(menuMusicTracks[_currentTrackIndex]));
        }

        private void PlayNextTrack() {
            // Store the track that just finished as previous
            _previousTrackIndex = _currentTrackIndex;

            if(shuffleTracks) {
                // Ensure next track is different from the one that just finished
                if(menuMusicTracks.Length > 1) {
                    int nextIndex;
                    do {
                        nextIndex = Random.Range(0, menuMusicTracks.Length);
                    } while(nextIndex == _previousTrackIndex);

                    _currentTrackIndex = nextIndex;
                } else {
                    // Only one track, can't avoid repetition
                    _currentTrackIndex = 0;
                }
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