using KINEMATION.KAnimationCore.Runtime.Core;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Weapon/PDW90 Animation")]
    public class Pdw90Animation : MonoBehaviour
    {
        [SerializeField] protected AnimationClip beltAnimation;
        [SerializeField] protected AvatarMask beltMask;
        
        [SerializeField] protected Transform magazine;
        [SerializeField] protected Transform bulletTop;
        [SerializeField] protected Transform bulletBottom;
        [SerializeField] protected Transform bullet;

        [SerializeField] protected Transform mechanism;
        [SerializeField] protected Transform spring;
        [SerializeField] protected AnimationCurve mechanismCurve;

        protected Animator _animator;
        protected IAmmoProvider _weapon;
        
        protected AnimationClipPlayable _beltPlayable;
        protected float _smoothAmmoWeight = 0f;

        protected KTransform _magIdle;
    
        private void Start()
        {
            _animator = GetComponent<Animator>();
            _weapon = transform.parent.GetComponentInChildren<IAmmoProvider>();
            
            _beltPlayable = AnimationClipPlayable.Create(_animator.playableGraph, beltAnimation);
            _beltPlayable.SetSpeed(0f);
            _beltPlayable.SetDuration(beltAnimation.length);
            
            var beltMixer = AnimationLayerMixerPlayable.Create(_animator.playableGraph, 1);
            beltMixer.ConnectInput(0, _beltPlayable, 0, 1f);
            beltMixer.SetLayerMaskFromAvatarMask(0, beltMask);

            var output = AnimationPlayableOutput.Create(_animator.playableGraph, "GunBelt", _animator);
            output.SetSourcePlayable(beltMixer);
            
            beltAnimation.SampleAnimation(gameObject, 0f);

            KTransform magazineTransform = new KTransform(magazine);
            _magIdle = new KTransform(magazine.parent).GetRelativeTransform(magazineTransform, false);
        }
    
        private void Update()
        {
            float target = (float) _weapon.GetActiveAmmo() / _weapon.GetMaxAmmo();
            _smoothAmmoWeight = Mathf.Lerp(_smoothAmmoWeight, 1f - target,
                KMath.ExpDecayAlpha(45f, Time.deltaTime));
            _beltPlayable.SetTime(Mathf.Lerp(0f, beltAnimation.length * 0.7f, _smoothAmmoWeight));
        }

        private void LateUpdate()
        {
            // Animate the bullets relatively to the magazine bone.
            // We do this because the bullets are not parented to the mag.
            var magT = new KTransform(magazine);
            var magIdleWorld = new KTransform(magazine.parent).GetWorldTransform(_magIdle, false);

            // Convert current motion to the idle magazine transform.
            var top = magIdleWorld.GetRelativeTransform(new KTransform(bulletTop), false);
            var bottom = magIdleWorld.GetRelativeTransform(new KTransform(bulletBottom), false);

            // Convert animation back to the world space as if it was parented to the mag.
            top = magT.GetWorldTransform(top, false);
            bottom = magT.GetWorldTransform(bottom, false);

            bulletTop.position = top.position;
            bulletTop.rotation = top.rotation;
            
            bulletBottom.position = bottom.position;
            bulletBottom.rotation = bottom.rotation;

            bullet.localScale = _smoothAmmoWeight > 0.17f ? Vector3.zero : Vector3.one;

            float curveValue = mechanismCurve.Evaluate(_smoothAmmoWeight);
            mechanism.localPosition += new Vector3(0f, -curveValue * 0.03f, 0f);
            spring.localScale += new Vector3(0f, curveValue * 1f, 0f);
        }
    }
}
