// Designed by KINEMATION, 2025.

using System.Collections.Generic;
using KINEMATION.KAnimationCore.Runtime.Core;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/Machine Gun")]
    public class FPSMachineGun : FPSWeapon
    {
        [SerializeField] private List<Transform> gunTape;
        [SerializeField, Min(0f)] private float tapeResetTime = 0f;
        
        private static int RELOAD_TAPE = Animator.StringToHash("Reload_Extra");
        private static int GAIT = Animator.StringToHash("Gait");

        private void Update()
        {
            weaponAnimator.SetFloat(GAIT, characterAnimator.GetFloat(GAIT));
        }

        private void LateUpdate()
        {
            int count = gunTape.Count;
            if (_activeAmmo > count) return;

            for (int i = 0; i < count; i++)
            {
                if(i > count - _activeAmmo) continue;
                
                KTransform childWorldTransform = KTransform.Identity;
                if (i < count - 1)
                {
                    childWorldTransform = new KTransform(gunTape[i + 1]);
                }

                gunTape[i].localScale /= 100f;
                if (i < count - 1)
                {
                    gunTape[i + 1].localScale *= 100f;
                    gunTape[i + 1].position = childWorldTransform.position;
                    gunTape[i + 1].rotation = childWorldTransform.rotation;
                }
            }
        }

        public override void OnReload()
        {
            if (_activeAmmo == weaponSettings.ammo) return;
            
            var reloadHash = _activeAmmo == 0 ? RELOAD_EMPTY : _activeAmmo > gunTape.Count ? RELOAD_TAC : RELOAD_TAPE;
            characterAnimator.Play(reloadHash, -1, 0f);
            weaponAnimator.Play(reloadHash, -1, 0f);

            float delay = _activeAmmo > gunTape.Count ? tacReloadDelay : tapeResetTime;
            Invoke(nameof(ResetActiveAmmo), delay * weaponSettings.ammoResetTimeScale);
            _isReloading = true;
        }
    }
}