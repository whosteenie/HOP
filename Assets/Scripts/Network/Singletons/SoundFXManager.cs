using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Network.Singletons {
    public class SoundFXManager : MonoBehaviour {
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
        [SerializeField] private AudioClip[] jumpPadClips;
        [SerializeField] private AudioClip[] grappleClips;
        [SerializeField] private AudioClip[] bulletTrailClips;
        [SerializeField] private AudioClip[] bulletImpactClips;
        [SerializeField] private AudioClip[] hopballSpawnClips;

        [Header("Generic Weapon Sound Banks")]
        [SerializeField] private AudioClip[] reloadClips;
        [SerializeField] private AudioClip[] dryClips;
        [SerializeField] private AudioClip[] shootClips;

        [Header("Weapon Specific Shoot Banks")]
        [SerializeField] private AudioClip[] shootPistolClips;
        [SerializeField] private AudioClip[] shootDeagleClips;
        [SerializeField] private AudioClip[] shootSmgClips;
        [SerializeField] private AudioClip[] shootRifleClips;
        [SerializeField] private AudioClip[] shootShotgunClips;
        [SerializeField] private AudioClip[] shootSniperClips;

        [Header("Weapon Specific Reload Banks")]
        [SerializeField] private AudioClip[] reloadPistolClips;
        [SerializeField] private AudioClip[] reloadDeagleClips;
        [SerializeField] private AudioClip[] reloadSmgClips;
        [SerializeField] private AudioClip[] reloadRifleClips;
        [SerializeField] private AudioClip[] reloadShotgunClips;
        [SerializeField] private AudioClip[] reloadSniperClips;

        [Header("UI Sound Banks")]
        [SerializeField] private AudioClip[] buttonClickClips;
        [SerializeField] private AudioClip[] buttonHoverClips;
        [SerializeField] private AudioClip[] backButtonClips;
        [SerializeField] private AudioClip[] timerTickClips;
        [SerializeField] private AudioClip[] hitClips;
        [SerializeField] private AudioClip[] killClips;
        [SerializeField] private AudioClip[] hurtClips;
        [SerializeField] private AudioClip[] taggedClips;
        [SerializeField] private AudioClip[] taggingClips;
        [SerializeField] private AudioClip[] sniperZoomClips;
        [SerializeField] private AudioClip[] weaponSwitchClips;

        [Header("Audio Falloff Settings")]
        [SerializeField] private float walkMaxDistance = 15f;
        [SerializeField] private float runMaxDistance = 20f;
        [SerializeField] private float jumpMaxDistance = 25f;
        [SerializeField] private float landMaxDistance = 25f;
        [SerializeField] private float reloadMaxDistance = 30f;
        [SerializeField] private float dryMaxDistance = 30f;
        [SerializeField] private float shootMaxDistance = 300f; // Heard across map
        [SerializeField] private float jumpPadMaxDistance = 50f;
        [SerializeField] private float grappleMaxDistance = 35f;
        [SerializeField] private float bulletTrailMaxDistance = 150f;
        [SerializeField] private float bulletImpactMaxDistance = 35f;
        [SerializeField] private float hopballSpawnMaxDistance = 300f; // Same as gunshots - heard across map

        private readonly Queue<AudioSource> _audioPool = new();
        private readonly Dictionary<string, AudioSource> _activeSounds = new();

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
            InitializePool();
        }

        private void OnDestroy() {
            // Stop all sounds when manager is destroyed
            StopAllSounds();
        }

        private void InitializePool() {
            for(var i = 0; i < poolSize; i++) {
                var src = Instantiate(soundFXPrefab, transform);
                src.gameObject.SetActive(false);
                _audioPool.Enqueue(src);
            }
        }

        private AudioSource GetPooled() {
            if(_audioPool.Count <= 0) return Instantiate(soundFXPrefab, transform);
            var s = _audioPool.Dequeue();
            s.gameObject.SetActive(true);
            return s;
        }

        private static float DbToLinear(float db) => db <= -80f ? 0f : Mathf.Pow(10f, db / 20f);

        private static AudioClip[] UseFallback(AudioClip[] primary, AudioClip[] fallback) =>
            primary is { Length: > 0 } ? primary : fallback;

        private AudioClip PickRandomFrom(SfxKey key) {
            AudioClip[] bank;
            try {
                bank = key switch {
                    SfxKey.Walk => walkClips,
                    SfxKey.Run => runClips,
                    SfxKey.Jump => jumpClips,
                    SfxKey.Land => landClips,
                    SfxKey.Reload => reloadClips,
                    SfxKey.ReloadPistol => UseFallback(reloadPistolClips, reloadClips),
                    SfxKey.ReloadDeagle => UseFallback(reloadDeagleClips, reloadClips),
                    SfxKey.ReloadSmg => UseFallback(reloadSmgClips, reloadClips),
                    SfxKey.ReloadRifle => UseFallback(reloadRifleClips, reloadClips),
                    SfxKey.ReloadShotgun => UseFallback(reloadShotgunClips, reloadClips),
                    SfxKey.ReloadSniper => UseFallback(reloadSniperClips, reloadClips),
                    SfxKey.Dry => dryClips,
                    SfxKey.Shoot => shootClips,
                    SfxKey.ShootPistol => UseFallback(shootPistolClips, shootClips),
                    SfxKey.ShootDeagle => UseFallback(shootDeagleClips, shootClips),
                    SfxKey.ShootSmg => UseFallback(shootSmgClips, shootClips),
                    SfxKey.ShootRifle => UseFallback(shootRifleClips, shootClips),
                    SfxKey.ShootShotgun => UseFallback(shootShotgunClips, shootClips),
                    SfxKey.ShootSniper => UseFallback(shootSniperClips, shootClips),
                    SfxKey.JumpPad => jumpPadClips,
                    SfxKey.Grapple => grappleClips,
                    SfxKey.BulletTrail => bulletTrailClips,
                    SfxKey.BulletImpact => bulletImpactClips,
                    SfxKey.HopballSpawn => hopballSpawnClips,
                    // UI Sounds
                    SfxKey.ButtonClick => buttonClickClips,
                    SfxKey.ButtonHover => buttonHoverClips,
                    SfxKey.BackButton => backButtonClips,
                    SfxKey.TimerTick => timerTickClips,
                    SfxKey.Hit => hitClips,
                    SfxKey.Kill => killClips,
                    SfxKey.Hurt => hurtClips,
                    SfxKey.Tagged => taggedClips,
                    SfxKey.Tagging => taggingClips,
                    SfxKey.SniperZoom => sniperZoomClips,
                    SfxKey.WeaponSwitch => weaponSwitchClips,
                    _ => null
                };
            } catch(System.Exception) {
                // If bank array == null or invalid, return null
                return null;
            }

            if(bank == null || bank.Length == 0) return null;

            // Filter out null clips and select from valid ones
            var validClips = new List<AudioClip>();
            foreach(var clip in bank) {
                if(clip != null) {
                    validClips.Add(clip);
                }
            }

            return validClips.Count == 0 ? null : validClips[Random.Range(0, validClips.Count)];
        }

        private float GetMaxDistance(SfxKey key) => key switch {
            SfxKey.Walk => walkMaxDistance,
            SfxKey.Run => runMaxDistance,
            SfxKey.Jump => jumpMaxDistance,
            SfxKey.Land => landMaxDistance,
            SfxKey.Reload => reloadMaxDistance,
            SfxKey.ReloadPistol or SfxKey.ReloadDeagle or SfxKey.ReloadSmg or SfxKey.ReloadRifle or SfxKey.ReloadShotgun
                or SfxKey.ReloadSniper => reloadMaxDistance,
            SfxKey.Dry => dryMaxDistance,
            SfxKey.Shoot => shootMaxDistance,
            SfxKey.ShootPistol or SfxKey.ShootDeagle or SfxKey.ShootSmg or SfxKey.ShootRifle or SfxKey.ShootShotgun
                or SfxKey.ShootSniper => shootMaxDistance,
            SfxKey.JumpPad => jumpPadMaxDistance,
            SfxKey.Grapple => grappleMaxDistance,
            SfxKey.BulletTrail => bulletTrailMaxDistance,
            SfxKey.BulletImpact => bulletImpactMaxDistance,
            SfxKey.HopballSpawn => hopballSpawnMaxDistance,
            _ => 50f
        };

        private float GetMinDistance(SfxKey key) {
            // MinDistance is typically 1-5% of max distance for natural falloff
            var maxDist = GetMaxDistance(key);
            return key switch {
                SfxKey.Shoot or SfxKey.ShootPistol or SfxKey.ShootDeagle or SfxKey.ShootSmg or SfxKey.ShootRifle
                    or SfxKey.ShootShotgun or SfxKey.ShootSniper => maxDist * 0.02f, // Louder close-up
                SfxKey.HopballSpawn => maxDist * 0.02f, // Same as gunshots
                SfxKey.BulletImpact => maxDist * 0.05f,
                SfxKey.Walk or SfxKey.Run => 1f, // Very close for footsteps
                _ => maxDist * 0.05f
            };
        }

        /// <summary>
        /// Network-consumed entrypoint. If parent != null, the AudioSource is parented (follows player/bullet tracer).
        /// Else, it's placed at the provided world position.
        /// </summary>
        public void PlayKey(SfxKey key, Transform parent, Vector3 worldPos, bool allowOverlap) {
            var clip = PickRandomFrom(key);
            // Return early if no clip found (allows sounds to be optional in inspector)
            if(clip == null) return;

            // overlap policy (shares your old logic; keep simple per-key gate)
            var trackKey = key.ToString();
            if(!allowOverlap && _activeSounds.TryGetValue(trackKey, out var playing) && playing != null &&
               playing.isPlaying)
                return;

            var src = GetPooled();

            // parent vs world-pos
            if(parent != null) {
                src.transform.SetParent(parent, false);
                src.transform.localPosition = Vector3.zero;
            } else {
                src.transform.SetParent(transform, false);
                src.transform.position = worldPos;
            }

            src.spatialBlend = 1f;
            src.minDistance = GetMinDistance(key);
            src.maxDistance = GetMaxDistance(key);
            src.rolloffMode = AudioRolloffMode.Logarithmic;

            // apply volumes from PlayerPrefs (per-client mixer-like control)
            var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
            var dbSfx = PlayerPrefs.GetFloat("SFXVolume", 0f);
            src.volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);

            src.clip = clip;
            src.Play();

            // Only track in _activeSounds if overlap is not allowed (for cleanup)
            // When overlap is allowed, each sound manages its own cleanup
            if(!allowOverlap) {
                _activeSounds[trackKey] = src;
            }

            // Use unique track key for overlapping sounds to avoid conflicts
            StartCoroutine(ReturnAfter(src, clip.length, allowOverlap ? null : trackKey));
        }

        private IEnumerator ReturnAfter(AudioSource src, float delay, string trackKey = null) {
            yield return new WaitForSeconds(delay);

            // Safety check: if AudioSource or its GameObject was destroyed, skip cleanup
            if(src?.gameObject == null) {
                // Clean up tracking if it exists
                if(trackKey != null && _activeSounds.TryGetValue(trackKey, out var cur) && cur == null)
                    _activeSounds.Remove(trackKey);
                yield break;
            }

            // Only remove from _activeSounds if this was the tracked sound (non-overlapping)
            if(trackKey != null && _activeSounds.TryGetValue(trackKey, out var cur2) && cur2 == src)
                _activeSounds.Remove(trackKey);

            // Additional safety check before accessing AudioSource properties
            src.Stop();
            src.clip = null;
            src.transform.SetParent(transform, false);
            src.gameObject.SetActive(false);
            _audioPool.Enqueue(src);
        }

        /// <summary>
        /// Stop a currently playing sound by key (for canceling reloads, etc.)
        /// </summary>
        public void StopSound(SfxKey key) {
            var trackKey = key.ToString();
            if(!_activeSounds.TryGetValue(trackKey, out var src) || src?.gameObject == null) return;
            if(src.isPlaying) {
                src.Stop();
            }

            src.clip = null;
            src.transform.SetParent(transform, false);
            src.gameObject.SetActive(false);
            _audioPool.Enqueue(src);
            _activeSounds.Remove(trackKey);
        }

        /// <summary>
        /// Stop all currently playing sounds and return them to the pool.
        /// Useful when leaving a game/scene to prevent accessing destroyed audio clips.
        /// </summary>
        public void StopAllSounds() {
            // Stop all tracked sounds
            var keysToRemove = new List<string>();
            foreach(var kvp in _activeSounds) {
                if(kvp.Value?.gameObject != null) {
                    kvp.Value.Stop();
                    kvp.Value.clip = null;
                    kvp.Value.transform.SetParent(transform, false);
                    kvp.Value.gameObject.SetActive(false);
                    _audioPool.Enqueue(kvp.Value);
                }

                keysToRemove.Add(kvp.Key);
            }

            // Clear the active sounds dictionary
            foreach(var key in keysToRemove) {
                _activeSounds.Remove(key);
            }

            // Stop all coroutines on this MonoBehaviour (this will stop all ReturnAfter coroutines)
            StopAllCoroutines();

            // Also stop any pooled AudioSources that might still be playing
            foreach(var src in _audioPool) {
                if(src?.gameObject == null || !src.isPlaying) continue;
                src.Stop();
                src.clip = null;
            }
        }

        /// <summary>
        /// Plays a UI sound using a SfxKey. UI sounds are non-spatial (2D) and heard by all players.
        /// Centralized location for all UI sound clip assignments.
        /// </summary>
        public void PlayUISound(SfxKey key) {
            var clip = PickRandomFrom(key);
            // Return early if no clip found (allows sounds to be optional in inspector)
            if(clip == null) return;

            var src = GetPooled();
            // UI is non-spatial; parent to manager
            src.transform.SetParent(transform, false);
            src.transform.localPosition = Vector3.zero;

            src.spatialBlend = 0f;
            src.minDistance = 1f;
            src.maxDistance = 500f;

            var dbMaster = PlayerPrefs.GetFloat("MasterVolume", 0f);
            var dbSfx = PlayerPrefs.GetFloat("SFXVolume", 0f);
            src.volume = DbToLinear(dbMaster) * DbToLinear(dbSfx);

            src.clip = clip;
            src.Play();
            StartCoroutine(ReturnAfter(src, clip.length));
        }
    }
}