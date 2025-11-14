// Designed by KINEMATION, 2025.

using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Sounds
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/FPS Weapon Sound")]
    public class FPSWeaponSound : MonoBehaviour
    {
        private FPSWeaponSettings _settings;
        private AudioSource _audioSource;
        
        private void Awake()
        {
            _settings = transform.parent.GetComponent<FPSWeapon>().weaponSettings;
            _audioSource = transform.root.GetComponentInChildren<AudioSource>();
        }

        public void PlayFireSound()
        {
            if (_audioSource == null)
            {
                Debug.LogWarning($"Failed to play weapon sound: invalid Audio Source!");
                return;
            }

            _audioSource.pitch = Random.Range(_settings.firePitchRange.x, _settings.firePitchRange.y);
            _audioSource.volume = Random.Range(_settings.fireVolumeRange.x, _settings.fireVolumeRange.y);
            _audioSource.PlayOneShot(FPSPlayerSound.GetRandomAudioClip(_settings.fireSounds));
        }

        public void PlayWeaponSound(int clipIndex)
        {
            if (clipIndex < 0 || clipIndex > _settings.weaponEventSounds.Count - 1)
            {
                Debug.LogWarning($"Failed to play weapon sound: invalid index!");
                return;
            }

            if (_audioSource == null)
            {
                Debug.LogWarning($"Failed to play weapon sound: invalid Audio Source!");
                return;
            }
            
            _audioSource.PlayOneShot(_settings.weaponEventSounds[clipIndex]);
        }
    }
}