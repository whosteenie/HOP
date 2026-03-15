using Network.Events;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Runtime grapple clavicle offset for the KIN viewmodel. Handles prepare/apply/clear and static AK anchor.</summary>
    internal sealed class KinGrappleClavicle {
        private const float RuntimeGrappleClavicleOffsetScale = 1f;
        private const float GrappleOffsetBlendInNormalized = 0.06f;
        private const float GrappleOffsetBlendOutStartNormalized = 0.82f;
        private const float GrappleOffsetBlendOutEndNormalized = 0.98f;
        private const int GrappleLayerIndex = 8;
        private static readonly Vector3 DefaultAkViewmodelLocalPosition = new(0.1699999f, -1.750005f, 0f);
        private static readonly int GrappleHash = Animator.StringToHash("Grapple");
        private static readonly int GrappleWeaponIndexHash = Animator.StringToHash("GrappleWeaponIndex");

        private static bool sHasAkViewmodelReference;
        private static Vector3 sAkViewmodelLocalPosition = DefaultAkViewmodelLocalPosition;
        private static bool sHasAkAnchorFrame1CameraReference;
        private static Vector3 sAkAnchorFrame1CameraLocal;

        private readonly IKinDriverResolverContext _context;
        private readonly KinActiveWeaponResolver _resolver;
        private readonly KinDriverWristBones _wristBones;
        private readonly bool _enableRuntimeGrappleClavicleOffset;

        private Vector3 _runtimeGrappleClavicleOffset;
        private bool _isRuntimeGrappleClavicleOffsetActive;
        private int _runtimeGrappleOffsetWeaponIndex;

        public KinGrappleClavicle(IKinDriverResolverContext context, KinActiveWeaponResolver resolver,
            KinDriverWristBones wristBones, bool enableRuntimeGrappleClavicleOffset) {
            _context = context;
            _resolver = resolver;
            _wristBones = wristBones;
            _enableRuntimeGrappleClavicleOffset = enableRuntimeGrappleClavicleOffset;
        }

        public void ApplyGrappleWeaponIndex() {
            var animator = _context.FpsAnimator;
            if(animator == null) return;
            var idx = _resolver.GetGrappleWeaponIndex();
            if(idx < 0) return;
            animator.SetFloat(GrappleWeaponIndexHash, idx);
        }

        public void OnGrappleStarted(GrappleStartedEvent evt) {
            if(evt is { UseFirstPersonAnimation: false }) {
                Clear();
                return;
            }
            ApplyGrappleWeaponIndex();
            if(_enableRuntimeGrappleClavicleOffset) {
                Prepare();
                _isRuntimeGrappleClavicleOffsetActive = false;
            } else {
                _isRuntimeGrappleClavicleOffsetActive = false;
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _runtimeGrappleOffsetWeaponIndex = 0;
            }
            if(_context.FpsAnimator != null) _context.FpsAnimator.SetTrigger(GrappleHash);
        }

        public void OnGrappleAnimFirstFrame(GrappleAnimFirstFrameEvent _) {
            if(!_enableRuntimeGrappleClavicleOffset) return;
            var playerInstance = _context.PlayerInstance;
            if(playerInstance == null || !playerInstance.activeInHierarchy) return;
            _wristBones.CacheIfNeeded();
            if(!_wristBones.TryGetClavicleLeft(out var _) && !_wristBones.EnsureClavicleLeft(playerInstance))
                return;
            var clavicle = _wristBones.ClavicleLeft;
            if(clavicle == null) return;
            var anchor = GetGrappleCalibrationAnchor();
            if(anchor == null) return;

            var idx = _resolver.GetGrappleWeaponIndex();
            if(idx == 0) {
                if(!_context.TryGetWeaponCameraTransform(out var cameraTransform)) return;
                sAkAnchorFrame1CameraLocal = cameraTransform.InverseTransformPoint(anchor.position);
                sHasAkAnchorFrame1CameraReference = true;
                return;
            }

            Vector3 resolvedLocalOffset;
            if(sHasAkAnchorFrame1CameraReference && _context.TryGetWeaponCameraTransform(out var frameCameraTransform)) {
                var currentCameraLocal = frameCameraTransform.InverseTransformPoint(anchor.position);
                var cameraLocalOffset = sAkAnchorFrame1CameraLocal - currentCameraLocal;
                var worldOffset = frameCameraTransform.TransformDirection(cameraLocalOffset);
                resolvedLocalOffset = clavicle.parent != null
                    ? clavicle.parent.InverseTransformDirection(worldOffset)
                    : worldOffset;
            } else {
                var driverTransform = _context.DriverTransform;
                var worldOffset = driverTransform != null && driverTransform.parent != null
                    ? driverTransform.parent.TransformDirection(_runtimeGrappleClavicleOffset)
                    : _runtimeGrappleClavicleOffset;
                resolvedLocalOffset = clavicle.parent != null
                    ? clavicle.parent.InverseTransformDirection(worldOffset)
                    : worldOffset;
            }

            _runtimeGrappleClavicleOffset = resolvedLocalOffset;
            _isRuntimeGrappleClavicleOffsetActive = _runtimeGrappleClavicleOffset.sqrMagnitude > 1e-8f;
        }

        public void OnGrappleAnimHide(GrappleAnimHideEvent _) => Clear();
        public void OnGrappleEnded(GrappleEndedEvent _) => Clear();

        public void Clear() {
            _isRuntimeGrappleClavicleOffsetActive = false;
            _runtimeGrappleClavicleOffset = Vector3.zero;
            _runtimeGrappleOffsetWeaponIndex = 0;
        }

        public void ApplyRuntimeGrappleClavicleOffset() {
            if(!_enableRuntimeGrappleClavicleOffset) return;
            if(!_isRuntimeGrappleClavicleOffsetActive || _runtimeGrappleClavicleOffset.sqrMagnitude <= 1e-8f) return;
            var playerInstance = _context.PlayerInstance;
            if(playerInstance == null || !playerInstance.activeInHierarchy) return;
            _wristBones.CacheIfNeeded();
            if(!_wristBones.TryGetClavicleLeft(out var clavicle) && !_wristBones.EnsureClavicleLeft(playerInstance))
                return;
            clavicle = _wristBones.ClavicleLeft;
            if(clavicle == null) return;

            var weight = ComputeRuntimeGrappleOffsetWeight();
            if(weight <= 0.0001f) return;
            var applied = _runtimeGrappleClavicleOffset * (RuntimeGrappleClavicleOffsetScale * weight);
            clavicle.localPosition += applied;
        }

        private void Prepare() {
            if(!_enableRuntimeGrappleClavicleOffset) {
                _runtimeGrappleClavicleOffset = Vector3.zero;
                _runtimeGrappleOffsetWeaponIndex = 0;
                _isRuntimeGrappleClavicleOffsetActive = false;
                return;
            }
            _runtimeGrappleOffsetWeaponIndex = _resolver.GetGrappleWeaponIndex();
            switch(_runtimeGrappleOffsetWeaponIndex) {
                case < 0:
                    _runtimeGrappleClavicleOffset = Vector3.zero;
                    _isRuntimeGrappleClavicleOffsetActive = false;
                    return;
                case 0:
                    sAkViewmodelLocalPosition = _context.DriverTransform.localPosition;
                    sHasAkViewmodelReference = true;
                    _runtimeGrappleClavicleOffset = Vector3.zero;
                    _isRuntimeGrappleClavicleOffsetActive = false;
                    return;
            }
            var akRef = sHasAkViewmodelReference ? sAkViewmodelLocalPosition : DefaultAkViewmodelLocalPosition;
            _runtimeGrappleClavicleOffset = akRef - _context.DriverTransform.localPosition;
            _isRuntimeGrappleClavicleOffsetActive = false;
        }

        private float ComputeRuntimeGrappleOffsetWeight() {
            var animator = _context.FpsAnimator;
            if(animator == null || GrappleLayerIndex >= animator.layerCount) return 1f;
            var clipInfos = animator.GetCurrentAnimatorClipInfo(GrappleLayerIndex);
            if(clipInfos == null || clipInfos.Length == 0) return 0f;
            var clipWeight = 0f;
            foreach(var c in clipInfos) {
                if(c.clip == null || c.clip.name.IndexOf("Grapple", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                clipWeight = Mathf.Max(clipWeight, c.weight);
            }
            if(clipWeight <= 0.0001f) return 0f;
            var state = animator.GetCurrentAnimatorStateInfo(GrappleLayerIndex);
            var normalized = Mathf.Repeat(state.normalizedTime, 1f);
            var inWeight = Mathf.Clamp01(normalized / GrappleOffsetBlendInNormalized);
            var outWeight = normalized <= GrappleOffsetBlendOutStartNormalized
                ? 1f
                : 1f - Mathf.Clamp01((normalized - GrappleOffsetBlendOutStartNormalized) /
                    Mathf.Max(0.0001f, GrappleOffsetBlendOutEndNormalized - GrappleOffsetBlendOutStartNormalized));
            return clipWeight * inWeight * outWeight;
        }

        private Transform GetGrappleCalibrationAnchor() {
            if(_wristBones.IkHandLeft != null) return _wristBones.IkHandLeft;
            if(_wristBones.WristDebugHandLeft != null) return _wristBones.WristDebugHandLeft;
            return _wristBones.GrappleOrigin != null ? _wristBones.GrappleOrigin : _wristBones.ClavicleLeft;
        }
    }
}
