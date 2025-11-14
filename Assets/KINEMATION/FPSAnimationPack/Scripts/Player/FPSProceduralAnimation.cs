using KINEMATION.KAnimationCore.Runtime.Core;
using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using UnityEngine;

namespace KINEMATION.FPSAnimationPack.Scripts.Player
{
    [ExecuteInEditMode]
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Character/FPS Procedural Animation")]
    public class FPSProceduralAnimation : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] private float ikWeight = 1f;
        
        [Header("Skeleton")]
        [SerializeField] private Transform skeletonRoot;
        [SerializeField] private Transform weaponBone;
        [SerializeField] private Transform weaponBoneAdditive;
        [SerializeField] private IKTransforms rightHand;
        [SerializeField] private IKTransforms leftHand;
        
        private KTwoBoneIkData _rightHandIk;
        private KTwoBoneIkData _leftHandIk;
        
        private RecoilAnimation _recoilAnimation;
        private static Quaternion ANIMATED_OFFSET = Quaternion.Euler(90f, 0f, 0f);

        private int _tacSprintLayerIndex;
        private int _triggerDisciplineLayerIndex;
        private int _rightHandLayerIndex;
        
        private bool _isAiming;

        private Vector2 _moveInput;
        private float _smoothGait;

        private bool _bSprinting;
        private bool _bTacSprinting;

        private void FindBoneByName(Transform search, ref Transform bone, string boneName)
        {
            if (search.name.Equals(boneName))
            {
                bone = search;
                return;
            }

            for (int i = 0; i < search.childCount; i++)
            {
                FindBoneByName(search.GetChild(i), ref bone, boneName);
            }
        }

        private void OnEnable()
        {
            if (Application.isPlaying) return;

            if (ReferenceEquals(skeletonRoot, null))
            {
                FindBoneByName(transform, ref skeletonRoot, "root");
            }

            if (ReferenceEquals(weaponBone, null))
            {
                FindBoneByName(transform, ref weaponBone, "ik_hand_gun");
            }

            if (ReferenceEquals(weaponBoneAdditive, null))
            {
                FindBoneByName(transform, ref weaponBoneAdditive, "ik_hand_gun_additive");
            }
            
            if (ReferenceEquals(rightHand.tip, null))
            {
                FindBoneByName(transform, ref rightHand.tip, "hand_r");
                rightHand.mid = rightHand.tip.parent;
                rightHand.root = rightHand.mid.parent;
            }
            
            if (ReferenceEquals(leftHand.tip, null))
            {
                FindBoneByName(transform, ref leftHand.tip, "hand_l");
                leftHand.mid = leftHand.tip.parent;
                leftHand.root = leftHand.mid.parent;
            }
        }

        private void ProcessAdditives(ref KTransform weaponT)
        {
            KTransform rootT = new KTransform(skeletonRoot);
            KTransform additive = rootT.GetRelativeTransform(new KTransform(weaponBoneAdditive), false);
            
            weaponT.position = KAnimationMath.MoveInSpace(rootT, weaponT, additive.position, 1f);
            weaponT.rotation = KAnimationMath.RotateInSpace(rootT, weaponT, additive.rotation, 1f);
        }

        private void ProcessRecoil(ref KTransform weaponT)
        {
            if (_recoilAnimation == null) return;
            
            KTransform recoil = new KTransform()
            {
                rotation = _recoilAnimation.OutRot,
                position = _recoilAnimation.OutLoc,
            };

            KTransform root = new KTransform(transform);
            weaponT.position = KAnimationMath.MoveInSpace(root, weaponT, recoil.position, 1f);
            weaponT.rotation = KAnimationMath.RotateInSpace(root, weaponT, recoil.rotation, 1f);
        }
        
        private void SetupIkData(ref KTwoBoneIkData ikData, in KTransform target, in IKTransforms transforms, 
            float weight = 1f)
        {
            ikData.target = target;
            
            ikData.tip = new KTransform(transforms.tip);
            ikData.mid = ikData.hint = new KTransform(transforms.mid);
            ikData.root = new KTransform(transforms.root);

            ikData.hintWeight = weight;
            ikData.posWeight = weight;
            ikData.rotWeight = weight;
        }
        
        private void ApplyIkData(in KTwoBoneIkData ikData, in IKTransforms transforms)
        {
            transforms.root.rotation = ikData.root.rotation;
            transforms.mid.rotation = ikData.mid.rotation;
            transforms.tip.rotation = ikData.tip.rotation;
        }
        
        private void LateUpdate()
        {
            if (Mathf.Approximately(ikWeight, 0f) || !Application.isPlaying) return;
            
            KTransform weaponTransform = new KTransform(weaponBone);
            
            weaponTransform.rotation = KAnimationMath.RotateInSpace(weaponTransform, weaponTransform,
                ANIMATED_OFFSET, 1f);
            
            KTransform rightHandTarget = weaponTransform.GetRelativeTransform(new KTransform(rightHand.tip), false);
            KTransform leftHandTarget = weaponTransform.GetRelativeTransform(new KTransform(leftHand.tip), false);
            
            ProcessAdditives(ref weaponTransform);
            ProcessRecoil(ref weaponTransform);
            
            weaponBone.position = weaponTransform.position;
            weaponBone.rotation = weaponTransform.rotation;
            
            rightHandTarget = weaponTransform.GetWorldTransform(rightHandTarget, false);
            leftHandTarget = weaponTransform.GetWorldTransform(leftHandTarget, false);
            
            SetupIkData(ref _rightHandIk, rightHandTarget, rightHand, ikWeight);
            SetupIkData(ref _leftHandIk, leftHandTarget, leftHand, ikWeight);
            
            KTwoBoneIK.Solve(ref _rightHandIk);
            KTwoBoneIK.Solve(ref _leftHandIk);

            ApplyIkData(_rightHandIk, rightHand);
            ApplyIkData(_leftHandIk, leftHand);
        }
    }
}
