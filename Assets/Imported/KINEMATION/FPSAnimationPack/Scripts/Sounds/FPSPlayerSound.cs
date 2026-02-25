// Designed by KINEMATION, 2025.

using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace KINEMATION.FPSAnimationPack.Scripts.Sounds
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Character/FPS Player Sound")]
    public class FPSPlayerSound : MonoBehaviour
    {
        [Header("Weapon swapping")]
        [SerializeField] private AudioClip equipSound;
        [SerializeField] private AudioClip unEquipSound;
        
        [Header("Movement")]
        [SerializeField] private List<AudioClip> walkSounds;
        [SerializeField] private float walkDelay = 0.4f;
        [SerializeField] private List<AudioClip> sprintSounds;
        [SerializeField] private float sprintDelay = 0.4f;
        [SerializeField] private float tacSprintDelay = 0.4f;
        
        [Header("Jumping")]
        [SerializeField] private AudioClip jumpSound;
        [SerializeField] private AudioClip landSound;

        [Header("Aiming")]
        [SerializeField] private AudioClip aimInSound;
        [SerializeField] private AudioClip aimOutSound;
        
        [Header("FireMode")]
        [SerializeField] private AudioClip fireModeSwitchSound;

        private Animator _animator;
        private AudioSource _playerAudioSource;
        private bool _isSourceValid;

        private static int GAIT = Animator.StringToHash("Gait");
        private static int IS_IN_AIR = Animator.StringToHash("IsInAir");
        private float _playback = 0f;

        public static AudioClip GetRandomAudioClip(List<AudioClip> audioClips)
        {
            int index = Random.Range(0, audioClips.Count - 1);
            return audioClips[index];
        }

        private void Start()
        {
            _playerAudioSource = GetComponent<AudioSource>();
            _isSourceValid = _playerAudioSource != null;

            _animator = GetComponent<Animator>();
        }

        private void PlayMovementSounds(float gait, float error = 0.4f)
        {
            if (gait >= error && gait <= 1f + error)
            {
                if (_playback >= walkDelay)
                {
                    PlayWalkSound();
                    _playback = 0f;
                }
                return;
            }

            if (gait >= 1f + error && gait <= 2f + error)
            {
                if (_playback >= sprintDelay)
                {
                    PlaySprintSound();
                    _playback = 0f;
                }
                return;
            }

            if (gait >= 2f + error && gait <= 3f)
            {
                if (_playback >= tacSprintDelay)
                {
                    PlaySprintSound();
                    _playback = 0f;
                }
            }
        }

        private void Update()
        {
            float gait = _animator.GetFloat(GAIT);
            if (Mathf.Approximately(gait, 0f) || _animator.GetBool(IS_IN_AIR))
            {
                _playback = 0f;
                return;
            }

            PlayMovementSounds(gait);
            _playback += Time.deltaTime;
        }

        private bool CheckAudioSource()
        {
            if (!_isSourceValid)
            {
                Debug.LogWarning($"Player Audio Source is invalid!");
                return false;
            }

            return true;
        }

        public void PlayAimSound(bool isAimIn = true)
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(isAimIn ? aimInSound : aimOutSound);
        }

        public void PlayFireModeSwitchSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(fireModeSwitchSound, Random.Range(0.2f, 0.25f));
        }

        public void PlayEquipSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(equipSound);
        }
        
        public void PlayUnEquipSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(unEquipSound);
        }
        
        public void PlayWalkSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(GetRandomAudioClip(walkSounds));
        }
        
        public void PlaySprintSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(GetRandomAudioClip(sprintSounds));
        }
        
        public void PlayJumpSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(jumpSound);
        }
        
        public void PlayLandSound()
        {
            if (!CheckAudioSource()) return;
            _playerAudioSource.PlayOneShot(landSound);
        }
    }
}