// Designed by KINEMATION, 2025.

using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/Manual Weapon")]
    public class FPSManualWeapon : FPSWeapon
    {
        private static int RELOAD_START = Animator.StringToHash("Reload_Start");
        private static int RELOAD_LOOP = Animator.StringToHash("Reload_Loop");
        private static int RELOAD_END = Animator.StringToHash("Reload_End");

        private float _startDelay = 0f;
        private float _loopDelay = 0f;

        public override void Initialize(GameObject owner)
        {
            base.Initialize(owner);

            foreach (var clip in weaponSettings.characterController.animationClips)
            {
                if (!clip.name.Contains("Reload")) continue;

                if (clip.name.Contains("Start"))
                {
                    _startDelay = clip.length;
                    continue;
                }
                
                if (clip.name.Contains("Loop")) _loopDelay = clip.length;
            }
        }

        public override void OnReload()
        {
            characterAnimator.Play(RELOAD_START, -1, 0f);
            weaponAnimator.Play(RELOAD_START, -1, 0f);
            
            Invoke(nameof(OnReloadLoop), _startDelay);
        }

        public void OnReloadLoop()
        {
            if (_activeAmmo == weaponSettings.ammo)
            {
                OnReloadEnd();
                return;
            }

            _activeAmmo++;
            
            characterAnimator.CrossFade(RELOAD_LOOP, 0.1f, -1, 0f, 0f);
            weaponAnimator.Play(RELOAD_LOOP, -1, 0f);
            
            Invoke(nameof(OnReloadLoop), _loopDelay);
        }

        public void OnReloadEnd()
        {
            characterAnimator.CrossFade(RELOAD_END, 0.1f, -1, 0f, 0f);
            weaponAnimator.Play(RELOAD_END, -1, 0f);
        }
    }
}