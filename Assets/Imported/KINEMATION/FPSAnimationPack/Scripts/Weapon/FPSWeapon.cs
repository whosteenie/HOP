// Designed by KINEMATION, 2025.

using KINEMATION.FPSAnimationPack.Scripts.Camera;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.KAnimationCore.Runtime.Core;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/FPS Weapon")]
    public class FPSWeapon : MonoBehaviour, IAmmoProvider
    {
        public float UnEquipDelay => unEquipDelay;
        public FireMode ActiveFireMode => fireMode;
        
        public FPSWeaponSettings weaponSettings;
        public Transform aimPoint;
        
        [SerializeField] protected FireMode fireMode = FireMode.Semi;

        [HideInInspector] public KTransform rightHandPose;
        [HideInInspector] public KTransform adsPose;

        protected GameObject ownerPlayer;
        protected RecoilAnimation recoilAnimation;
        protected FPSWeaponSound weaponSound;

        protected Animator characterAnimator;
        protected Animator weaponAnimator;
        
        protected static int RELOAD_EMPTY = Animator.StringToHash("Reload_Empty");
        protected static int RELOAD_TAC = Animator.StringToHash("Reload_Tac");
        protected static int FIRE = Animator.StringToHash("Fire");
        protected static int FIREOUT = Animator.StringToHash("FireOut");
        
        protected static int EQUIP = Animator.StringToHash("Equip");
        protected static int EQUIP_OVERRIDE = Animator.StringToHash("Equip_Override");
        protected static int UNEQUIP = Animator.StringToHash("UnEquip");
        protected static int IDLE = Animator.StringToHash("Idle");
        
        protected float unEquipDelay;
        protected float emptyReloadDelay;
        protected float tacReloadDelay;

        protected int _activeAmmo;
        
        protected bool _isReloading;
        protected bool _isFiring;

        protected FPSCameraAnimator cameraAnimator;

        public virtual void Initialize(GameObject owner)
        {
            ownerPlayer = owner;
            recoilAnimation = owner.GetComponent<RecoilAnimation>();
            characterAnimator = owner.GetComponent<Animator>();

            _activeAmmo = weaponSettings.ammo;

            weaponAnimator = GetComponentInChildren<Animator>();
            if (weaponAnimator == null)
            {
                Debug.LogWarning("FPSWeapon: Animator not found!");
            }

            weaponSound = GetComponentInChildren<FPSWeaponSound>();
            if (weaponSound == null)
            {
                Debug.LogWarning("FPSWeapon: FPS Weapon Sound not found!");
            }

            if (Mathf.Approximately(weaponSettings.fireRate, 0f))
            {
                Debug.LogWarning("FPSWeapon: Fire Rate is ZERO, setting it to default 600.");
                weaponSettings.fireRate = 600f;
            }

            AnimationClip idlePose = null;

            foreach (var clip in weaponSettings.characterController.animationClips)
            {
                if (clip.name.Contains("Reload"))
                {
                    if (clip.name.Contains("Tac")) tacReloadDelay = clip.length;
                    if (clip.name.Contains("Empty")) emptyReloadDelay = clip.length;
                    continue;
                }
                
                if (clip.name.ToLower().Contains("unequip"))
                {
                    unEquipDelay = clip.length;
                    continue;
                }
                
                if(idlePose != null) continue;
                if (clip.name.Contains("Idle") || clip.name.Contains("Pose")) idlePose = clip;
            }

            if (idlePose != null)
            {
                idlePose.SampleAnimation(ownerPlayer, 0f);
            }

            cameraAnimator = owner.transform.parent.GetComponentInChildren<FPSCameraAnimator>();
        }

        public virtual void OnReload()
        {
            if (_activeAmmo == weaponSettings.ammo) return;
            
            var reloadHash = _activeAmmo == 0 ? RELOAD_EMPTY : RELOAD_TAC;
            characterAnimator.Play(reloadHash, -1, 0f);
            weaponAnimator.Play(reloadHash, -1, 0f);

            float delay = _activeAmmo == 0 ? emptyReloadDelay : tacReloadDelay;
            Invoke(nameof(ResetActiveAmmo), delay * weaponSettings.ammoResetTimeScale);
            _isReloading = true;
        }

        public void OnFireModeChange()
        {
            fireMode = fireMode == FireMode.Auto ? FireMode.Semi : weaponSettings.fullAuto ? FireMode.Auto : FireMode.Semi;
            recoilAnimation.fireMode = fireMode;
        }

        public void OnEquipped_Immediate()
        {
            characterAnimator.runtimeAnimatorController = weaponSettings.characterController;
            weaponAnimator.Play(IDLE, -1, 0f);
            recoilAnimation.Init(weaponSettings.recoilAnimData, weaponSettings.fireRate, fireMode);
        }

        public void OnEquipped(bool fastEquip = false)
        {
            characterAnimator.runtimeAnimatorController = weaponSettings.characterController;
            recoilAnimation.Init(weaponSettings.recoilAnimData, weaponSettings.fireRate, fireMode);
            
            // Reset the default pose to idle.
            characterAnimator.Play(IDLE, -1, 0f);

            // Play the equip animation.
            if (weaponSettings.hasEquipOverride)
            {
                characterAnimator.Play("IKMovement", -1, 0f);
                characterAnimator.Play(fastEquip ? EQUIP : EQUIP_OVERRIDE, -1, 0f);
                return;
            }
            
            // Play the curve-based equipping animation.
            characterAnimator.Play(EQUIP, -1, 0f);
        }

        public float OnUnEquipped()
        {
            characterAnimator.SetTrigger(UNEQUIP);
            return unEquipDelay + 0.05f;
        }
        
        public void OnFirePressed()
        {
            _isFiring = true;
            OnFire();
        }

        public void OnFireReleased()
        {
            _isFiring = false;
            recoilAnimation.Stop();
        }

        private void OnFire()
        {
            if (!_isFiring || _isReloading) return;

            if (_activeAmmo == 0)
            {
                OnFireReleased();
                return;
            }
            
            recoilAnimation.Play();
            if (weaponSound != null) weaponSound.PlayFireSound();
            if (cameraAnimator != null) cameraAnimator.PlayCameraShake(weaponSettings.cameraShake);

            if (weaponSettings.useFireClip) characterAnimator.Play(FIRE, -1, 0f);
            weaponAnimator.Play(weaponSettings.hasFireOut && _activeAmmo == 1
                ? FIREOUT
                : FIRE, -1, 0f);

            _activeAmmo--;
            
            if (fireMode == FireMode.Semi) return;
            Invoke(nameof(OnFire), 60f / weaponSettings.fireRate);
        }

        protected void ResetActiveAmmo()
        {
            _activeAmmo = weaponSettings.ammo;
            _isReloading = false;
        }

        public int GetActiveAmmo()
        {
            return _activeAmmo;
        }

        public int GetMaxAmmo()
        {
            return weaponSettings.ammo;
        }
    }
}