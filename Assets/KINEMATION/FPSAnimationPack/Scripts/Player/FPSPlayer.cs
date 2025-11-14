// Designed by KINEMATION, 2025.

using KINEMATION.ProceduralRecoilAnimationSystem.Runtime;
using KINEMATION.FPSAnimationPack.Scripts.Sounds;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using KINEMATION.KAnimationCore.Runtime.Core;

using System;
using System.Collections.Generic;
using KINEMATION.FPSAnimationPack.Scripts.Camera;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace KINEMATION.FPSAnimationPack.Scripts.Player
{
    [Serializable]
    public struct IKTransforms
    {
        public Transform tip;
        public Transform mid;
        public Transform root;
    }
    
    [AddComponentMenu("KINEMATION/FPS Animation Pack/Character/FPS Player")]
    public class FPSPlayer : MonoBehaviour
    {
        public float AdsWeight => _adsWeight;
        
        public FPSPlayerSettings playerSettings;
        
        [Header("Skeleton")]
        [SerializeField] private Transform skeletonRoot;
        [SerializeField] private Transform weaponBone;
        [SerializeField] private Transform weaponBoneAdditive;
        [SerializeField] private Transform cameraPoint;
        [SerializeField] private IKTransforms rightHand;
        [SerializeField] private IKTransforms leftHand;
        
        private KTwoBoneIkData _rightHandIk;
        private KTwoBoneIkData _leftHandIk;
        
        private RecoilAnimation _recoilAnimation;
        private float _adsWeight;

        private List<FPSWeapon> _weapons = new List<FPSWeapon>();
        private List<FPSWeapon> _prefabComponents = new List<FPSWeapon>();
        private int _activeWeaponIndex = 0;

        private Animator _animator;

        private static int RIGHT_HAND_WEIGHT = Animator.StringToHash("RightHandWeight");
        private static int TAC_SPRINT_WEIGHT = Animator.StringToHash("TacSprintWeight");
        private static int GRENADE_WEIGHT = Animator.StringToHash("GrenadeWeight");
        private static int THROW_GRENADE = Animator.StringToHash("ThrowGrenade");
        private static int GAIT = Animator.StringToHash("Gait");
        private static int IS_IN_AIR = Animator.StringToHash("IsInAir");
        private static int INSPECT = Animator.StringToHash("Inspect");
        
        private int _tacSprintLayerIndex;
        private int _triggerDisciplineLayerIndex;
        private int _rightHandLayerIndex;
        
        private bool _isAiming;

        private Vector2 _moveInput;
        private float _smoothGait;

        private Vector2 _lookInput;

        private bool _bSprinting;
        private bool _bTacSprinting;

        private FPSPlayerSound _playerSound;

        private float _ikMotionPlayBack;
        private KTransform _ikMotion = KTransform.Identity;
        private KTransform _cachedIkMotion = KTransform.Identity;
        private IKMotion _activeMotion;

        private KTransform _localCameraPoint;
        private CharacterController _controller;
        
        private void EquipWeapon_Incremental()
        {
            GetActiveWeapon().gameObject.SetActive(false);
            _activeWeaponIndex = _activeWeaponIndex + 1 > _weapons.Count - 1 ? 0 : _activeWeaponIndex + 1;
            GetActiveWeapon().OnEquipped();
            Invoke(nameof(SetWeaponVisible), 0.05f);
        }

        private void EquipWeapon()
        {
            GetActiveWeapon().gameObject.SetActive(false);
            GetActiveWeapon().OnEquipped(true);
            Invoke(nameof(SetWeaponVisible), 0.05f);
        }

        private void ThrowGrenade()
        {
            GetActiveWeapon().gameObject.SetActive(false);
            Invoke(nameof(EquipWeapon), playerSettings.grenadeDelay);
        }

        private void OnLand()
        {
            _animator.SetBool(IS_IN_AIR, false);
        }

        public void OnThrowGrenade()
        {
            _animator.SetTrigger(THROW_GRENADE);
            Invoke(nameof(ThrowGrenade), GetActiveWeapon().UnEquipDelay);
        }

        public void OnChangeWeapon()
        {
            if (_weapons.Count <= 1) return;
            float delay = GetActiveWeapon().OnUnEquipped();
            Invoke(nameof(EquipWeapon_Incremental), delay);
        }

        public void OnChangeFireMode()
        {
            var prevFireMode = GetActiveWeapon().ActiveFireMode;
            GetActiveWeapon().OnFireModeChange();

            if (prevFireMode != GetActiveWeapon().ActiveFireMode)
            {
                _playerSound.PlayFireModeSwitchSound();
                PlayIkMotion(playerSettings.fireModeMotion);
            }
        }
        
        public void OnReload()
        {
            GetActiveWeapon().OnReload();
        }
        
        public void OnJump()
        {
            _animator.SetBool(IS_IN_AIR, true);
            Invoke(nameof(OnLand), 0.4f);
        }
        
        public void OnInspect()
        {
            _animator.CrossFade(INSPECT, 0.1f);
        }
        
#if ENABLE_INPUT_SYSTEM
        public void OnMouseWheel(InputValue value)
        {
            float mouseWheelValue = value.Get<float>();
            if (mouseWheelValue == 0f) return;
            
            GetActiveWeapon().gameObject.SetActive(false);
            
            _activeWeaponIndex += mouseWheelValue > 0f ? 1 : -1;

            if (_activeWeaponIndex < 0) _activeWeaponIndex = _weapons.Count - 1;
            if(_activeWeaponIndex > _weapons.Count - 1) _activeWeaponIndex = 0;
            
            GetActiveWeapon().gameObject.SetActive(true);
            GetActiveWeapon().OnEquipped_Immediate();
        }
        
        public void OnFire(InputValue value)
        {
            if(value.isPressed)
            {
                GetActiveWeapon().OnFirePressed();
                return;
            }
            
            GetActiveWeapon().OnFireReleased();
        }

        public void OnAim(InputValue value)
        {
            bool wasAiming = _isAiming;
            _isAiming = value.isPressed;
            _recoilAnimation.isAiming = _isAiming;

            if (wasAiming != _isAiming)
            {
                _playerSound.PlayAimSound(_isAiming);
                PlayIkMotion(playerSettings.aimingMotion);
            }
        }

        public void OnMove(InputValue value)
        {
            _moveInput = value.Get<Vector2>();
        }

        public void OnSprint(InputValue value)
        {
            _bSprinting = value.isPressed;
            if(!_bSprinting) _bTacSprinting = false;
        }
        
        public void OnTacSprint(InputValue value)
        {
            if (!_bSprinting) return;
            _bTacSprinting = value.isPressed;
        }

        public void OnLook(InputValue value)
        {
            Vector2 input = value.Get<Vector2>() * playerSettings.sensitivity;
            _lookInput.y = Mathf.Clamp(_lookInput.y - input.y, -90f, 90f);
            _lookInput.x = input.x;
        }
#endif
#if !ENABLE_INPUT_SYSTEM
        private void OnLookLegacy()
        {
            Vector2 input = new Vector2()
            {
                x = Input.GetAxis("Horizontal"),
                y = Input.GetAxis("Vertical")
            };
            
            _lookInput.y = Mathf.Clamp(_lookInput.y + input.y, -90f, 90f);
            _lookInput.x = input.x;
        }

        private void OnMouseWheelLegacy()
        {
            float mouseWheelValue = Input.GetAxis("Mouse ScrollWheel");
            if (mouseWheelValue == 0f) return;
            
            GetActiveWeapon().gameObject.SetActive(false);
            _activeWeaponIndex += mouseWheelValue > 0f ? 1 : -1;

            if (_activeWeaponIndex < 0) _activeWeaponIndex = _weapons.Count - 1;
            if(_activeWeaponIndex > _weapons.Count - 1) _activeWeaponIndex = 0;
            
            GetActiveWeapon().gameObject.SetActive(true);
            GetActiveWeapon().OnEquipped_Immediate();
        }

        private void OnAimLegacy(bool isPressed)
        {
            bool wasAiming = _isAiming;
            _isAiming = isPressed;
            _recoilAnimation.isAiming = _isAiming;
            
            if(wasAiming != _isAiming) 
            {
                _playerSound.PlayAimSound(_isAiming);
                PlayIkMotion(playerSettings.aimingMotion);
            }
        }
        
        private void OnMoveLegacy()
        {
            _moveInput.x = Input.GetAxis("Horizontal");
            _moveInput.y = Input.GetAxis("Vertical");
            _moveInput.Normalize();
        }

        private void OnSprintLegacy(bool isPressed)
        {
            _bSprinting = isPressed;
            if(!_bSprinting) _bTacSprinting = false;
        }

        private void OnTacSprintLegacy(bool isPressed)
        {
            if (!_bSprinting) return;
            _bTacSprinting = isPressed;
        }
        
        private void ProcessLegacyInputs()
        {
            OnMouseWheelLegacy();
            if (Input.GetKeyDown(KeyCode.G)) OnThrowGrenade();
            if (Input.GetKeyDown(KeyCode.F)) OnChangeWeapon();
            if (Input.GetKeyDown(KeyCode.B)) OnChangeFireMode();
            if (Input.GetKeyDown(KeyCode.R)) OnReload();
            if (Input.GetKeyDown(KeyCode.Space)) OnJump();
            if (Input.GetKeyDown(KeyCode.I)) OnInspect();
            
            if (Input.GetKeyDown(KeyCode.Mouse0)) GetActiveWeapon().OnFirePressed();
            if (Input.GetKeyUp(KeyCode.Mouse0)) GetActiveWeapon().OnFireReleased();

            OnAimLegacy(Input.GetKey(KeyCode.Mouse1));
            OnMoveLegacy();
            OnLookLegacy();
            OnSprintLegacy(Input.GetKey(KeyCode.LeftShift));
            OnTacSprintLegacy(Input.GetKey(KeyCode.X));
        }
#endif
        private void SetWeaponVisible()
        {
            GetActiveWeapon().gameObject.SetActive(true);
        }

        public FPSWeapon GetActiveWeapon()
        {
            return _weapons[_activeWeaponIndex];
        }

        public FPSWeapon GetActivePrefab()
        {
            return _prefabComponents[_activeWeaponIndex];
        }

        private void Start()
        {
            _animator = GetComponent<Animator>();
            _controller = transform.root.GetComponent<CharacterController>();
            _recoilAnimation = GetComponent<RecoilAnimation>();
            _playerSound = GetComponent<FPSPlayerSound>();
            
            _triggerDisciplineLayerIndex = _animator.GetLayerIndex("TriggerDiscipline");
            _rightHandLayerIndex = _animator.GetLayerIndex("RightHand");
            _tacSprintLayerIndex = _animator.GetLayerIndex("TacSprint");
            
            KTransform root = new KTransform(transform);
            _localCameraPoint = root.GetRelativeTransform(new KTransform(cameraPoint), false);

            foreach (var prefab in playerSettings.weaponPrefabs)
            {
                var prefabComponent = prefab.GetComponent<FPSWeapon>();
                if(prefabComponent == null) continue;
                
                _prefabComponents.Add(prefabComponent);
                
                var instance = Instantiate(prefab, weaponBone, false);
                instance.SetActive(false);
                
                var component = instance.GetComponent<FPSWeapon>();
                component.Initialize(gameObject);

                KTransform weaponT = new KTransform(weaponBone);
                component.rightHandPose = new KTransform(rightHand.tip).GetRelativeTransform(weaponT, false);
                
                var localWeapon = root.GetRelativeTransform(weaponT, false);

                localWeapon.rotation *= prefabComponent.weaponSettings.rotationOffset;
                
                component.adsPose.position = _localCameraPoint.position - localWeapon.position;
                component.adsPose.rotation = Quaternion.Inverse(localWeapon.rotation);

                _weapons.Add(component);
            }
            
            GetActiveWeapon().gameObject.SetActive(true);
            GetActiveWeapon().OnEquipped();
        }

        private float GetDesiredGait()
        {
            if (_bTacSprinting) return 3f;
            if (_bSprinting) return 2f;
            return _moveInput.magnitude;
        }
        
        private void Update()
        {
#if !ENABLE_INPUT_SYSTEM
            ProcessLegacyInputs();
#endif
            _adsWeight = Mathf.Clamp01(_adsWeight + playerSettings.aimSpeed * Time.deltaTime * (_isAiming ? 1f : -1f));

            _smoothGait = Mathf.Lerp(_smoothGait, GetDesiredGait(), 
                KMath.ExpDecayAlpha(playerSettings.gaitSmoothing, Time.deltaTime));
            
            _animator.SetFloat(GAIT, _smoothGait);
            _animator.SetLayerWeight(_tacSprintLayerIndex, Mathf.Clamp01(_smoothGait - 2f));

            bool triggerAllowed = GetActiveWeapon().weaponSettings.useSprintTriggerDiscipline;

            _animator.SetLayerWeight(_triggerDisciplineLayerIndex,
                triggerAllowed ? _animator.GetFloat(TAC_SPRINT_WEIGHT) : 0f);

            _animator.SetLayerWeight(_rightHandLayerIndex, _animator.GetFloat(RIGHT_HAND_WEIGHT));
            
            Vector3 cameraPosition = -_localCameraPoint.position;
            
            transform.localRotation = Quaternion.Euler(_lookInput.y, 0f, 0f);
            transform.localPosition = transform.localRotation * cameraPosition - cameraPosition;

            if (_controller != null)
            {
                Transform root = _controller.transform;
                root.rotation *= Quaternion.Euler(0f, _lookInput.x, 0f);
                Vector3 movement = root.forward * _moveInput.y + root.right * _moveInput.x;
                movement *= _smoothGait * 1.5f;
                _controller.Move(movement * Time.deltaTime);
            }
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
        
        private void ProcessOffsets(ref KTransform weaponT)
        {
            var root = transform;
            KTransform rootT = new KTransform(root);
            var weaponOffset = GetActiveWeapon().weaponSettings.ikOffset;

            float mask = 1f - _animator.GetFloat(TAC_SPRINT_WEIGHT);
            weaponT.position = KAnimationMath.MoveInSpace(rootT, weaponT, weaponOffset, mask);
            
            var settings = GetActiveWeapon().weaponSettings;
            KAnimationMath.MoveInSpace(root, rightHand.root, settings.rightClavicleOffset, mask);
            KAnimationMath.MoveInSpace(root, leftHand.root, settings.leftClavicleOffset, mask);
        }

        private void ProcessAdditives(ref KTransform weaponT)
        {
            KTransform rootT = new KTransform(skeletonRoot);
            KTransform additive = rootT.GetRelativeTransform(new KTransform(weaponBoneAdditive), false);
            
            float weight = Mathf.Lerp(1f, 0.3f, _adsWeight) * (1f - _animator.GetFloat(GRENADE_WEIGHT));
            
            weaponT.position = KAnimationMath.MoveInSpace(rootT, weaponT, additive.position, weight);
            weaponT.rotation = KAnimationMath.RotateInSpace(rootT, weaponT, additive.rotation, weight);
        }

        private void ProcessRecoil(ref KTransform weaponT)
        {
            KTransform recoil = new KTransform()
            {
                rotation = _recoilAnimation.OutRot,
                position = _recoilAnimation.OutLoc,
            };

            KTransform root = new KTransform(transform);
            weaponT.position = KAnimationMath.MoveInSpace(root, weaponT, recoil.position, 1f);
            weaponT.rotation = KAnimationMath.RotateInSpace(root, weaponT, recoil.rotation, 1f);
        }

        private void ProcessAds(ref KTransform weaponT)
        {
            var weaponOffset = GetActiveWeapon().weaponSettings.ikOffset;
            var adsPose = weaponT;
            
            KTransform aimPoint = KTransform.Identity;
            
            aimPoint.position = -weaponBone.InverseTransformPoint(GetActiveWeapon().aimPoint.position);
            aimPoint.position -= GetActiveWeapon().weaponSettings.aimPointOffset;
            aimPoint.rotation = Quaternion.Inverse(weaponBone.rotation) * GetActiveWeapon().aimPoint.rotation;
            
            KTransform root = new KTransform(transform);
            adsPose.position = KAnimationMath.MoveInSpace(root, adsPose,
                GetActiveWeapon().adsPose.position - weaponOffset, 1f);
            adsPose.rotation =
                KAnimationMath.RotateInSpace(root, adsPose, 
                    GetActiveWeapon().adsPose.rotation, 1f);

            KTransform cameraPose = root.GetWorldTransform(_localCameraPoint, false);

            float adsBlendWeight = GetActiveWeapon().weaponSettings.adsBlend;
            adsPose.position = Vector3.Lerp(cameraPose.position, adsPose.position, adsBlendWeight);
            adsPose.rotation = Quaternion.Slerp(cameraPose.rotation, adsPose.rotation, adsBlendWeight);

            adsPose.position = KAnimationMath.MoveInSpace(root, adsPose, aimPoint.rotation * aimPoint.position, 1f);
            adsPose.rotation = KAnimationMath.RotateInSpace(root, adsPose, aimPoint.rotation, 1f);

            float weight = KCurves.EaseSine(0f, 1f, _adsWeight);
            
            weaponT.position = Vector3.Lerp(weaponT.position, adsPose.position, weight);
            weaponT.rotation = Quaternion.Slerp(weaponT.rotation, adsPose.rotation, weight);
        }

        private KTransform GetWeaponPose()
        {
            KTransform defaultWorldPose =
                new KTransform(rightHand.tip).GetWorldTransform(GetActiveWeapon().rightHandPose, false);
            float weight = _animator.GetFloat(RIGHT_HAND_WEIGHT);
            
            return KTransform.Lerp(new KTransform(weaponBone), defaultWorldPose, weight);
        }
        
        private void PlayIkMotion(IKMotion newMotion)
        {
            _ikMotionPlayBack = 0f;
            _cachedIkMotion = _ikMotion;
            _activeMotion = newMotion;
        }

        private void ProcessIkMotion(ref KTransform weaponT)
        {
            if (_activeMotion == null) return;
            
            _ikMotionPlayBack = Mathf.Clamp(_ikMotionPlayBack + _activeMotion.playRate * Time.deltaTime, 0f, 
                _activeMotion.GetLength());

            Vector3 positionTarget = _activeMotion.translationCurves.GetValue(_ikMotionPlayBack);
            positionTarget.x *= _activeMotion.translationScale.x;
            positionTarget.y *= _activeMotion.translationScale.y;
            positionTarget.z *= _activeMotion.translationScale.z;

            Vector3 rotationTarget = _activeMotion.rotationCurves.GetValue(_ikMotionPlayBack);
            rotationTarget.x *= _activeMotion.rotationScale.x;
            rotationTarget.y *= _activeMotion.rotationScale.y;
            rotationTarget.z *= _activeMotion.rotationScale.z;

            _ikMotion.position = positionTarget;
            _ikMotion.rotation = Quaternion.Euler(rotationTarget);

            if (!Mathf.Approximately(_activeMotion.blendTime, 0f))
            {
                _ikMotion = KTransform.Lerp(_cachedIkMotion, _ikMotion,
                    _ikMotionPlayBack / _activeMotion.blendTime);
            }

            var root = new KTransform(transform);
            weaponT.position = KAnimationMath.MoveInSpace(root, weaponT, _ikMotion.position, 1f);
            weaponT.rotation = KAnimationMath.RotateInSpace(root, weaponT, _ikMotion.rotation, 1f);
        }

        private void LateUpdate()
        {
            KAnimationMath.RotateInSpace(transform, rightHand.tip,
                GetActiveWeapon().weaponSettings.rightHandSprintOffset, _animator.GetFloat(TAC_SPRINT_WEIGHT));
            
            KTransform weaponTransform = GetWeaponPose();
            
            weaponTransform.rotation = KAnimationMath.RotateInSpace(weaponTransform, weaponTransform,
                GetActiveWeapon().weaponSettings.rotationOffset, 1f);
            
            KTransform rightHandTarget = weaponTransform.GetRelativeTransform(new KTransform(rightHand.tip), false);
            KTransform leftHandTarget = weaponTransform.GetRelativeTransform(new KTransform(leftHand.tip), false);

            ProcessOffsets(ref weaponTransform);
            ProcessAds(ref weaponTransform);
            ProcessAdditives(ref weaponTransform);
            ProcessIkMotion(ref weaponTransform);
            ProcessRecoil(ref weaponTransform);
            
            weaponBone.position = weaponTransform.position;
            weaponBone.rotation = weaponTransform.rotation;
            
            rightHandTarget = weaponTransform.GetWorldTransform(rightHandTarget, false);
            leftHandTarget = weaponTransform.GetWorldTransform(leftHandTarget, false);
            
            SetupIkData(ref _rightHandIk, rightHandTarget, rightHand, playerSettings.ikWeight);
            SetupIkData(ref _leftHandIk, leftHandTarget, leftHand, playerSettings.ikWeight);
            
            KTwoBoneIK.Solve(ref _rightHandIk);
            KTwoBoneIK.Solve(ref _leftHandIk);

            ApplyIkData(_rightHandIk, rightHand);
            ApplyIkData(_leftHandIk, leftHand);
        }

        private void OnFire()
        {
            _recoilAnimation.Play();
        }
    }
}