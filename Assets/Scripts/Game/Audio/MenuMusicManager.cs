using UnityEngine;
using UnityEngine.Audio;

namespace Game.Audio {
    public class MenuMusicPlayer : MonoBehaviour {
        public static MenuMusicPlayer Instance { get; private set; }

        [Header("Menu Music")]
        [SerializeField] private AudioClip[] menuMusicTracks;

        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioMixerGroup musicMixerGroup;

        [Header("Settings")]
        [SerializeField] private bool shuffleTracks = true;

        [SerializeField] private float fadeTime = 2f;

        private int _currentTrackIndex;
        private int _previousTrackIndex = -1; // Track the last played song
        private Coroutine _musicFadeCoroutine;
        private bool _allowAutoAdvance = true;

        private void OnEnable() {
            if(Instance == null || Instance == this) {
                Instance = this;
            } else {
                Debug.LogWarning("[MenuMusicPlayer] Multiple instances detected. Using the most recently enabled instance.");
                Instance = this;
            }
        }

        private void OnDisable() {
            if(Instance == this) {
                Instance = null;
            }
        }

        private void Start() {
            EnsureMusicSource();
            if(musicSource == null) return;

            musicSource.loop = false;
            musicSource.playOnAwake = false;
            if(musicMixerGroup != null) {
                musicSource.outputAudioMixerGroup = musicMixerGroup;
            } else {
                Debug.LogWarning("[MenuMusicPlayer] Music mixer group not assigned. Music volume sliders may not affect menu music.");
            }

            PlayMenuMusic();
        }

        private void Update() {
            if(!_allowAutoAdvance || musicSource == null) return;

            // Auto-advance to next track
            if(!musicSource.isPlaying && menuMusicTracks is { Length: > 0 }) {
                PlayNextTrack();
            }
        }

        private void PlayMenuMusic() {
            if(!_allowAutoAdvance) return;
            if(menuMusicTracks is not { Length: not 0 }) return;

            _currentTrackIndex = shuffleTracks ? Random.Range(0, menuMusicTracks.Length) : 0;
            _previousTrackIndex = _currentTrackIndex; // Set initial previous track
            StartFadeIn(menuMusicTracks[_currentTrackIndex]);
        }

        private void PlayNextTrack() {
            if(!_allowAutoAdvance || musicSource == null) return;

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

        public void FadeOutForTransition(float duration) {
            _allowAutoAdvance = false;
            if(musicSource == null) {
                EnsureMusicSource();
            }

            if(musicSource == null) return;

            if(_musicFadeCoroutine != null) {
                StopCoroutine(_musicFadeCoroutine);
                _musicFadeCoroutine = null;
            }

            var fadeDuration = Mathf.Max(0.01f, duration);
            _musicFadeCoroutine = StartCoroutine(FadeOutAndStop(fadeDuration));
        }

        public void StopForTransitionImmediate() {
            _allowAutoAdvance = false;

            if(_musicFadeCoroutine != null) {
                StopCoroutine(_musicFadeCoroutine);
                _musicFadeCoroutine = null;
            }

            if(musicSource == null) {
                EnsureMusicSource();
            }

            if(musicSource == null) return;

            musicSource.volume = 0f;
            musicSource.Stop();
        }

        private void EnsureMusicSource() {
            if(musicSource == null) {
                musicSource = GetComponent<AudioSource>();
            }

            if(musicSource == null) {
                musicSource = gameObject.AddComponent<AudioSource>();
            }
        }

        private void StartFadeIn(AudioClip clip) {
            if(_musicFadeCoroutine != null) {
                StopCoroutine(_musicFadeCoroutine);
                _musicFadeCoroutine = null;
            }

            _musicFadeCoroutine = StartCoroutine(FadeIn(clip));
        }

        private System.Collections.IEnumerator FadeIn(AudioClip clip) {
            if(musicSource == null || clip == null) {
                _musicFadeCoroutine = null;
                yield break;
            }

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
            _musicFadeCoroutine = null;
        }

        private System.Collections.IEnumerator FadeOutAndStop(float duration) {
            if(musicSource == null) {
                _musicFadeCoroutine = null;
                yield break;
            }

            if(!musicSource.isPlaying) {
                musicSource.volume = 0f;
                _musicFadeCoroutine = null;
                yield break;
            }

            var startVolume = musicSource.volume;
            var elapsed = 0f;
            while(elapsed < duration) {
                if(musicSource == null) {
                    _musicFadeCoroutine = null;
                    yield break;
                }

                elapsed += Time.deltaTime;
                musicSource.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                yield return null;
            }

            if(musicSource != null) {
                musicSource.volume = 0f;
                musicSource.Stop();
            }

            _musicFadeCoroutine = null;
        }
    }
}
