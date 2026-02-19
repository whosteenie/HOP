using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.VFX;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Game.Player {
    /// <summary>
    /// Authoring helper for menu mannequins. Supports frozen animation poses and trail snapshots in edit mode.
    /// Place this on the mannequin root.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(5000)]
    [DisallowMultipleComponent]
    public class PlayerMannequinConfig : MonoBehaviour {
        private enum PoseSourceMode {
            AnimatorController
        }

        [Serializable]
        public class WeaponVisualOption {
            public string displayName = "Weapon";
            public GameObject handObject;
            public GameObject backObject;
            [Header("Shot Simulation")]
            public bool autoFindShotParticleSystems = true;
            public bool autoFindShotVisualEffects = true;
            public bool autoFindShotTrailRenderers = true;
            public Transform shotOrigin;
            public float simulatedShotSpeed = 140f;
            public ParticleSystem[] shotParticleSystems = Array.Empty<ParticleSystem>();
            public VisualEffect[] shotVisualEffects = Array.Empty<VisualEffect>();
            public TrailRenderer[] shotTrailRenderers = Array.Empty<TrailRenderer>();
            public GameObject projectileVisual;
        }

        private enum HandWeaponSlot {
            Primary,
            Secondary
        }

        [Header("Animation - Core")]
        [SerializeField] private Animator animator;
        [SerializeField, HideInInspector] private PoseSourceMode poseSourceMode = PoseSourceMode.AnimatorController;

        [Header("Animation - Layers")]
        [SerializeField, Range(0f, 1f)] private float weaponHoldLayerWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float rightHandHoldLayerWeight;
        [SerializeField, Range(0f, 1f)] private float weaponHoldStateNormalizedTime;
        [SerializeField] private string baseLayerStateName;
        [SerializeField, Range(0f, 1f)] private float baseLayerNormalizedTime;

        [Header("Animation - Aim")]
        [SerializeField] private Transform lookPitchTarget;
        [SerializeField, Range(0f, 1f)] private float lookPitchWeight = 1f;
        [SerializeField, Range(0f, 1f)] private float lookYawWeight = 1f;
        [SerializeField, Range(-90f, 90f)] private float lookPitchDegrees;
        [SerializeField, Range(-90f, 90f)] private float lookYawDegrees;
        [SerializeField] private bool logLookDebug;
        [SerializeField] private bool logLookDebugEveryApply;
        [SerializeField, HideInInspector] private bool hasCapturedLookPitchSpineProxyOffset;
        [SerializeField, HideInInspector] private Quaternion capturedLookPitchSpineProxyOffset = Quaternion.identity;

        [Header("Mannequin Surface")]
        [SerializeField] private bool autoFindMannequinBodyRenderer = true;
        [SerializeField] private SkinnedMeshRenderer mannequinBodyRenderer;
        [SerializeField] private Color mannequinColor = Color.white;
        [SerializeField, Range(0f, 1f)] private float mannequinMetallic;
        [SerializeField, Range(0f, 1f)] private float mannequinSmoothness = 0.5f;

        [Header("Weapon Visuals")]
        [SerializeField] private WeaponVisualOption[] primaryOptions = Array.Empty<WeaponVisualOption>();
        [SerializeField] private WeaponVisualOption[] secondaryOptions = Array.Empty<WeaponVisualOption>();
        [SerializeField, Min(0)] private int selectedPrimaryIndex;
        [SerializeField, Min(0)] private int selectedSecondaryIndex;
        [SerializeField] private HandWeaponSlot handWeaponSlot = HandWeaponSlot.Primary;
        
        [Header("Weapon Shot Preview")]
        [SerializeField] private bool simulateShot;
        [SerializeField, Range(0f, 1.5f)] private float shotLifetime = 0.08f;

        [Header("Trail Preview")]
        [SerializeField] private bool previewTrail = true;
        [SerializeField] private ParticleSystem[] trailSystems = Array.Empty<ParticleSystem>();
        [SerializeField, Range(0f, 1f)] private float trailLifecycle;
        [SerializeField, Range(-180f, 180f)] private float fakeVelocityYawDegrees;
        [SerializeField, Range(-80f, 80f)] private float fakeVelocityPitchDegrees;
        [SerializeField, Range(0f, 50f)] private float fakeVelocityMagnitude = 8f;
        [FormerlySerializedAs("fakeVelocity")]
        [SerializeField, HideInInspector] private Vector3 legacyFakeVelocity = new(0f, 0f, 8f);
        [SerializeField, HideInInspector] private bool legacyVelocityMigrated;
        [SerializeField, Range(0f, 1f)] private float minEmissionMultiplier;
        [SerializeField, Range(0f, 2f)] private float maxEmissionMultiplier = 1f;
        [SerializeField, Min(1f)] private float trailTailEmissionBoost = 30f;
        [FormerlySerializedAs("frozenTailDistanceMultiplier")]
        [SerializeField, Min(0f)] private float tailSyntheticVelocityScale = 1f;
        [SerializeField] private bool useMannequinColorForTrail = true;
        [SerializeField] private Color trailColor = Color.white;
        [SerializeField, Min(0f)] private float trailColorIntensity = 1f;
        [SerializeField] private bool logTrailDebug;
        [SerializeField] private bool logTrailDebugEveryApply;

        [Header("Editor Preview & Gizmos")]
        [SerializeField] private bool previewInEditMode = true;
        [SerializeField] private bool previewInPlayMode = true;
        [SerializeField] private bool autoApplyEachFrame = true;
        [SerializeField] private bool showVelocityGizmo = true;
        [SerializeField] private Color velocityGizmoColor = new(0.2f, 0.85f, 1f, 0.95f);
        [SerializeField, Min(0.1f)] private float velocityGizmoScale = 0.08f;

        private float _lastAppliedTrailLifecycle = -1f;
        private Vector3 _lastAppliedVelocity = Vector3.positiveInfinity;
        private bool _lastAppliedPreviewTrail;
        private int _lastAppliedPrimaryIndex = -1;
        private int _lastAppliedSecondaryIndex = -1;
        private HandWeaponSlot _lastAppliedHandSlot;
        private PoseSourceMode _lastAppliedPoseSourceMode = (PoseSourceMode)(-1);
        private int _lastAppliedWeaponIndex = int.MinValue;
        private Vector2 _lastAppliedLocomotion = new(float.PositiveInfinity, float.PositiveInfinity);
        private int _lastWeaponHoldLayerIndex = -2;
        private int _lastRightHandLayerIndex = -2;
        private float _lastWeaponHoldLayerWeight = -1f;
        private float _lastRightHandLayerWeight = -1f;
        private float _lastBaseLayerNormalizedTime = float.NaN;
        private string _lastBaseLayerStateName = string.Empty;
        private readonly HashSet<int> _invalidTrailWarningIds = new();
        private int _lastTrailSystemSignature;
        private float _lastAppliedTailSyntheticVelocityScale = float.NaN;
        private Transform _lookPitchBaseTarget;
        private Quaternion _cachedLookPitchBaseLocalRotation = Quaternion.identity;
        private bool _hasCachedLookPitchBaseRotation;
        private Quaternion _lastAppliedLookPitchOffset = Quaternion.identity;
        private bool _animationPoseUpdatedThisApply;
        private bool _lastSimulateShot;
        private float _lastShotLifetime = -1f;
        private int _lastShotOptionId = int.MinValue;
        private readonly HashSet<int> _invalidShotWarningIds = new();
        private bool _deferredApplyQueued;
        private bool _forceAnimationPoseRefreshThisApply;
        private bool _forceTrailResampleThisApply;
        private bool _pendingLookBaseRefresh;
        private double _lastLiveTrailEditorSampleTime = -1d;
        private Transform _cachedLookPitchProxyTarget;
        private KevinIglesias.SpineProxy _cachedLookPitchSpineProxy;
        private bool _runtimeLookProbeQueued;
        private float _nextRuntimeLookProbeAt;
        private MaterialPropertyBlock _mannequinPropertyBlock;
        private MaterialPropertyBlock _trailMaterialPropertyBlock;

        private const float RuntimeLookProbeInterval = 0.25f;
        private const float RuntimeLookProbeErrorThresholdDeg = 0.1f;

        private void Reset() {
            animator = GetComponentInChildren<Animator>(true);
            if(trailSystems == null || trailSystems.Length == 0) {
                trailSystems = GetComponentsInChildren<ParticleSystem>(true);
            }
            ApplyNow(forceAnimationPoseRefresh: true);
        }

        private void OnEnable() {
            _lastLiveTrailEditorSampleTime = -1d;
            CacheLookPitchSpineProxy();
            if(!Application.isPlaying) {
                CaptureLookPitchSpineProxyOffsetFromScene();
                QueueDeferredApplyInEditor();
                return;
            }

            ApplyCapturedLookPitchSpineProxyOffsetIfAvailable();
            ApplyNow(forceAnimationPoseRefresh: true);
        }

        private void OnValidate() {
            poseSourceMode = PoseSourceMode.AnimatorController;
            if(!legacyVelocityMigrated && legacyFakeVelocity.sqrMagnitude > 0.0001f) {
                var dir = legacyFakeVelocity.normalized;
                fakeVelocityYawDegrees = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                fakeVelocityPitchDegrees = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
                fakeVelocityMagnitude = legacyFakeVelocity.magnitude;
                legacyVelocityMigrated = true;
            }
            lookPitchDegrees = Mathf.Clamp(lookPitchDegrees, -90f, 90f);
            lookYawDegrees = Mathf.Clamp(lookYawDegrees, -90f, 90f);
            mannequinMetallic = Mathf.Clamp01(mannequinMetallic);
            mannequinSmoothness = Mathf.Clamp01(mannequinSmoothness);
            trailColorIntensity = Mathf.Max(0f, trailColorIntensity);

            fakeVelocityMagnitude = Mathf.Max(0f, fakeVelocityMagnitude);
            CacheLookPitchSpineProxy(forceRefresh: true);
            CaptureLookPitchSpineProxyOffsetFromScene();
            QueueDeferredApplyInEditor();
        }

        private void Update() {
            if(Application.isPlaying) {
                if(!previewInPlayMode || !autoApplyEachFrame) return;
            } else {
                if(!previewInEditMode || !autoApplyEachFrame) return;
            }
            ApplyNow();
        }

        private void LateUpdate() {
            if(Application.isPlaying) {
                if(!previewInPlayMode) return;
            } else if(!previewInEditMode) {
                return;
            }

            // Re-apply after animator evaluation so look offsets match final frame pose.
            ApplyCapturedLookPitchSpineProxyOffsetIfAvailable();
            ApplyLookPitch(forceRefreshBaseFromCurrentPose: _pendingLookBaseRefresh);
            ApplyLookPitchProxyToOriginalSpine();
            QueueRuntimeLookProbe();
            _pendingLookBaseRefresh = false;
        }

        private void OnDisable() {
            _lastLiveTrailEditorSampleTime = -1d;
            _runtimeLookProbeQueued = false;
            _nextRuntimeLookProbeAt = 0f;
            if(animator != null) {
                animator.speed = 1f;
            }

            if(_lookPitchBaseTarget != null && _hasCachedLookPitchBaseRotation) {
                _lookPitchBaseTarget.localRotation = _cachedLookPitchBaseLocalRotation;
            }
            _lastAppliedLookPitchOffset = Quaternion.identity;
        }

        [ContextMenu("Apply Mannequin Preview")]
        public void ApplyNow() {
            ApplyNow(forceAnimationPoseRefresh: false);
        }

        [ContextMenu("Capture Spine Proxy Offset")]
        public void CaptureSpineProxyOffsetNow() {
            if(Application.isPlaying) return;
            CaptureLookPitchSpineProxyOffsetFromScene();
            if(!hasCapturedLookPitchSpineProxyOffset) return;

            Debug.Log(
                $"[PlayerMannequinConfig] Captured spine proxy offset for play-mode consistency: {capturedLookPitchSpineProxyOffset.eulerAngles}",
                this);
        }

        [ContextMenu("Repair Trail Materials")]
        public void RepairTrailMaterialsNow() {
#if UNITY_EDITOR
            var systems = CollectTrailParticleSystems();
            foreach(var system in systems) {
                if(system == null) continue;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if(renderer == null) continue;
                ForceRepairTrailRendererMaterials(renderer);
            }
#endif
            ApplyNow(forceAnimationPoseRefresh: false);
        }

        public void ApplyNow(bool forceAnimationPoseRefresh) {
            switch(Application.isPlaying) {
                case true when !previewInPlayMode:
                case false when !previewInEditMode:
                    return;
            }

            _forceAnimationPoseRefreshThisApply = forceAnimationPoseRefresh;
            _animationPoseUpdatedThisApply = false;
            ApplyAnimationPose();
            ApplyMannequinCustomization();
            _pendingLookBaseRefresh |= _animationPoseUpdatedThisApply;
            if(IsSceneReadyForPreview()) {
                ApplyTrailPreview();
            }
            ApplyWeaponVisuals();
            ApplyShotSimulation();
            _animationPoseUpdatedThisApply = false;
            _forceAnimationPoseRefreshThisApply = false;

#if UNITY_EDITOR
            if(Application.isPlaying) return;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
#endif
        }

        private void ApplyAnimationPose() {
            ApplyAnimatorControllerPose();
        }

        private void ApplyMannequinCustomization() {
            ApplyMannequinSurfaceOverrides();
            ApplyTrailColorOverrides();
        }

        private void ApplyMannequinSurfaceOverrides() {
            EnsureMannequinBodyRendererReference();
            if(mannequinBodyRenderer == null) return;

            var sharedMaterials = mannequinBodyRenderer.sharedMaterials;
            if(sharedMaterials == null || sharedMaterials.Length <= MannequinSurfaceMaterialIndex) return;
            var material = sharedMaterials[MannequinSurfaceMaterialIndex];

            _mannequinPropertyBlock ??= new MaterialPropertyBlock();
            mannequinBodyRenderer.GetPropertyBlock(_mannequinPropertyBlock, MannequinSurfaceMaterialIndex);
            _mannequinPropertyBlock.Clear();

            if(material != null) {
                if(material.HasProperty(BaseColorId)) {
                    _mannequinPropertyBlock.SetColor(BaseColorId, mannequinColor);
                } else if(material.HasProperty(ColorId)) {
                    _mannequinPropertyBlock.SetColor(ColorId, mannequinColor);
                }

                if(material.HasProperty(MetallicId)) {
                    _mannequinPropertyBlock.SetFloat(MetallicId, mannequinMetallic);
                }

                if(material.HasProperty(SmoothnessId)) {
                    _mannequinPropertyBlock.SetFloat(SmoothnessId, mannequinSmoothness);
                } else if(material.HasProperty(GlossinessId)) {
                    _mannequinPropertyBlock.SetFloat(GlossinessId, mannequinSmoothness);
                }
            }

            mannequinBodyRenderer.SetPropertyBlock(_mannequinPropertyBlock, MannequinSurfaceMaterialIndex);
        }

        private void ApplyTrailColorOverrides() {
            var tint = GetTrailTintColor();
            var systems = CollectTrailParticleSystems();
            ApplyTrailMaterialColorOverrides(tint, systems);

            foreach(var system in systems) {
                if(system == null) continue;
                if(!CanSimulateTrailParticleSystem(system, logWarnings: false)) continue;

                var main = system.main;
                main.startColor = new ParticleSystem.MinMaxGradient(tint);
            }
        }

        private void ApplyTrailMaterialColorOverrides(Color tint, List<ParticleSystem> systems) {
            if(systems == null || systems.Count == 0) return;
            _trailMaterialPropertyBlock ??= new MaterialPropertyBlock();

            foreach(var system in systems) {
                if(system == null) continue;
                if(!CanSimulateTrailParticleSystem(system, logWarnings: false)) continue;
                var renderer = system.GetComponent<ParticleSystemRenderer>();
                if(renderer == null) continue;

#if UNITY_EDITOR
                if(!Application.isPlaying) {
                    TryRepairBrokenTrailRendererMaterials(renderer);
                }
#endif

                var sharedMaterials = renderer.sharedMaterials;
                if(sharedMaterials == null) continue;
                for(var i = 0; i < sharedMaterials.Length; i++) {
                    ApplyTrailMaterialPropertyBlock(renderer, sharedMaterials[i], i, tint);
                }
            }
        }

        private void ApplyTrailMaterialPropertyBlock(
            ParticleSystemRenderer renderer,
            Material material,
            int materialIndex,
            Color tint) {
            if(renderer == null || material == null) return;

            _trailMaterialPropertyBlock.Clear();
            var wroteAny = false;
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, ColorId, tint);
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, ColorNoUnderscoreId, tint);
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, BaseColorId, tint);
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, MainColorId, tint);
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, TintColorId, tint);
            wroteAny |= TrySetColorProperty(material, _trailMaterialPropertyBlock, EmissionColorId, tint);
            if(!wroteAny) return;

            renderer.SetPropertyBlock(_trailMaterialPropertyBlock, materialIndex);
        }

#if UNITY_EDITOR
        private static void TryRepairBrokenTrailRendererMaterials(ParticleSystemRenderer renderer) {
            if(renderer == null || Application.isPlaying) return;
            var hasBroken = false;

            var sharedMaterials = renderer.sharedMaterials;
            if(sharedMaterials == null || sharedMaterials.Length == 0) {
                hasBroken = true;
            } else {
                foreach(var material in sharedMaterials) {
                    if(!IsBrokenTrailMaterial(material)) continue;
                    hasBroken = true;
                    break;
                }
            }

            if(!hasBroken && IsBrokenTrailMaterial(renderer.trailMaterial)) {
                hasBroken = true;
            }

            if(!hasBroken) return;
            ForceRepairTrailRendererMaterials(renderer);
        }

        private static void ForceRepairTrailRendererMaterials(ParticleSystemRenderer renderer) {
            if(renderer == null) return;
            var objectName = renderer.gameObject != null ? renderer.gameObject.name : string.Empty;

            // Explicitly normalize known systems so material slot ordering does not drift.
            if(string.Equals(objectName, "trail", StringComparison.OrdinalIgnoreCase)) {
                // Original prefab mapping:
                // Material = glow
                // Trail Material = pz_27b2_lgt
                var material = AssetDatabase.LoadAssetAtPath<Material>(DefaultTrailRibbonMaterialPath);
                var trailMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultTrailMainMaterialPath);
                if(material != null) {
                    renderer.sharedMaterial = material;
                }
                if(trailMaterial != null) {
                    renderer.trailMaterial = trailMaterial;
                }
                return;
            }

            if(objectName.StartsWith("electric", StringComparison.OrdinalIgnoreCase)) {
                var electric = AssetDatabase.LoadAssetAtPath<Material>(DefaultElectricMainMaterialPath);
                if(electric != null) {
                    renderer.sharedMaterial = electric;
                }
                return;
            }

            if(string.Equals(objectName, "lightning_center", StringComparison.OrdinalIgnoreCase)) {
                var lightningBlue = AssetDatabase.LoadAssetAtPath<Material>(DefaultLightningBlueMaterialPath);
                if(lightningBlue != null) {
                    renderer.sharedMaterial = lightningBlue;
                }
                return;
            }

            var sourceRenderer = PrefabUtility.GetCorrespondingObjectFromSource(renderer) as ParticleSystemRenderer;
            if(sourceRenderer != null) {
                var sourceSharedMaterials = sourceRenderer.sharedMaterials;
                if(sourceSharedMaterials is { Length: > 0 }) {
                    renderer.sharedMaterials = sourceSharedMaterials;
                }

                if(sourceRenderer.trailMaterial != null) {
                    renderer.trailMaterial = sourceRenderer.trailMaterial;
                }
                return;
            }

            var fallbackSharedMaterials = BuildFallbackSharedMaterials(objectName);
            if(fallbackSharedMaterials is { Length: > 0 }) {
                renderer.sharedMaterials = fallbackSharedMaterials;
            }

            var fallbackTrailMaterial = GetFallbackTrailRibbonMaterial(objectName);
            if(fallbackTrailMaterial != null) {
                renderer.trailMaterial = fallbackTrailMaterial;
            }
        }

        private static Material[] BuildFallbackSharedMaterials(string objectName) {
            if(string.IsNullOrWhiteSpace(objectName)) return null;

            if(string.Equals(objectName, "trail", StringComparison.OrdinalIgnoreCase)) {
                var glow = AssetDatabase.LoadAssetAtPath<Material>(DefaultTrailRibbonMaterialPath);
                var pz = AssetDatabase.LoadAssetAtPath<Material>(DefaultTrailMainMaterialPath);
                if(glow != null && pz != null) return new[] { glow, pz };
                if(glow != null) return new[] { glow };
                if(pz != null) return new[] { pz };
                return null;
            }

            if(objectName.StartsWith("electric", StringComparison.OrdinalIgnoreCase)) {
                var electric = AssetDatabase.LoadAssetAtPath<Material>(DefaultElectricMainMaterialPath);
                if(electric != null) return new[] { electric };
                return null;
            }

            if(string.Equals(objectName, "lightning_center", StringComparison.OrdinalIgnoreCase)) {
                var lightningBlue = AssetDatabase.LoadAssetAtPath<Material>(DefaultLightningBlueMaterialPath);
                if(lightningBlue != null) return new[] { lightningBlue };
                return null;
            }

            return null;
        }

        private static Material GetFallbackTrailRibbonMaterial(string objectName) {
            if(string.IsNullOrWhiteSpace(objectName)) return null;
            if(string.Equals(objectName, "trail", StringComparison.OrdinalIgnoreCase)) {
                // Original prefab uses pz_27b2_lgt as trail material.
                return AssetDatabase.LoadAssetAtPath<Material>(DefaultTrailMainMaterialPath);
            }
            return null;
        }
#endif

        private static bool IsBrokenTrailMaterial(Material material) {
            if(material == null) return true;
            if(material.shader == null || string.Equals(material.shader.name, "Hidden/InternalErrorShader", StringComparison.Ordinal)) {
                return true;
            }
            return material.name.EndsWith("(Mannequin Instance)", StringComparison.Ordinal);
        }

        private static bool TrySetColorProperty(Material material, MaterialPropertyBlock block, int propertyId, Color value) {
            if(material == null || block == null) return false;
            if(!material.HasProperty(propertyId)) return false;
            block.SetColor(propertyId, value);
            return true;
        }

        private Color GetTrailTintColor() {
            var baseColor = useMannequinColorForTrail ? mannequinColor : trailColor;
            var intensity = Mathf.Max(0f, trailColorIntensity);
            var tint = baseColor * intensity;
            tint.a = baseColor.a;
            return tint;
        }

        private List<ParticleSystem> CollectTrailParticleSystems() {
            var result = new List<ParticleSystem>();
            if(trailSystems == null || trailSystems.Length == 0) {
                return result;
            }

            var seen = new HashSet<int>();
            foreach(var rootSystem in trailSystems) {
                if(rootSystem == null) continue;
                var hierarchySystems = rootSystem.GetComponentsInChildren<ParticleSystem>(true);
                foreach(var system in hierarchySystems) {
                    if(system == null) continue;
                    var id = system.GetInstanceID();
                    if(!seen.Add(id)) continue;
                    result.Add(system);
                }
            }

            return result;
        }

        private void ApplyAnimatorControllerPose() {
            if(!TryPrepareAnimatorForPreview()) return;

            var locomotion = GetLocomotionVector();
            var weaponIndex = GetAnimatorWeaponIndex();

            var shouldApply = !Application.isPlaying
                              || _forceAnimationPoseRefreshThisApply
                              || _lastAppliedPoseSourceMode != poseSourceMode
                              || _lastAppliedWeaponIndex != weaponIndex
                              || _lastAppliedLocomotion != locomotion
                              || !Mathf.Approximately(_lastBaseLayerNormalizedTime, baseLayerNormalizedTime)
                              || !string.Equals(_lastBaseLayerStateName, baseLayerStateName, StringComparison.Ordinal)
                              || HaveLayerWeightSettingsChanged();

            if(shouldApply) {
                animator.SetInteger(WeaponIndexHash, weaponIndex);
                animator.SetFloat(MoveXHash, locomotion.x);
                animator.SetFloat(MoveYHash, locomotion.y);
                animator.SetBool(IsGroundedHash, true);
                animator.SetBool(IsFallingHash, false);
                animator.SetBool(IsCrouchingHash, false);
                animator.SetBool(IsSlidingHash, false);
                animator.SetBool(IsWallRunningHash, false);
                animator.SetBool(RightWallRunHash, false);
                animator.SetFloat(WallRunDirectionHash, 1f);
                animator.SetBool(IsSprintingHash, locomotion.y > 0.6f);

                if(!string.IsNullOrWhiteSpace(baseLayerStateName)) {
                    animator.Play(Animator.StringToHash(baseLayerStateName), 0, Mathf.Clamp01(baseLayerNormalizedTime));
                }

                var weaponLayerIndex = ApplyUpperBodyLayerWeights();
                ApplyWeaponHoldLayerState(weaponLayerIndex);
                animator.Update(0f);
                _animationPoseUpdatedThisApply = true;
                _lastAppliedWeaponIndex = weaponIndex;
                _lastAppliedLocomotion = locomotion;
                _lastBaseLayerNormalizedTime = baseLayerNormalizedTime;
                _lastBaseLayerStateName = baseLayerStateName ?? string.Empty;
                CacheCurrentLayerWeightState();
            }

            animator.speed = 0f;
            _lastAppliedPoseSourceMode = poseSourceMode;
        }

        private void ApplyTrailPreview() {
            EnsureTrailSystemReferences();
            if(trailSystems == null || trailSystems.Length == 0) return;

            var fakeVelocity = GetConfiguredFakeVelocity();
            var tailSyntheticVelocity = fakeVelocity * Mathf.Max(0f, tailSyntheticVelocityScale);
            var velocityMag = fakeVelocity.magnitude;
            var velocityRatio = Mathf.Clamp01(velocityMag / Mathf.Max(0.01f, PlayerPreviewMaxSpeed));
            var emissionScale = Mathf.Lerp(minEmissionMultiplier, maxEmissionMultiplier, velocityRatio);
            var trailSignature = ComputeTrailSystemSignature();
            var needsResample = !Mathf.Approximately(_lastAppliedTrailLifecycle, trailLifecycle)
                                || _lastAppliedPreviewTrail != previewTrail
                                || _lastAppliedVelocity != fakeVelocity
                                || trailSignature != _lastTrailSystemSignature
                                || !Mathf.Approximately(_lastAppliedTailSyntheticVelocityScale, tailSyntheticVelocityScale)
                                || _forceTrailResampleThisApply;
            var anyValidTrailProcessed = false;
            var validTrailSystems = new List<ParticleSystem>(trailSystems.Length);
            var liveTrailSystems = new List<ParticleSystem>(trailSystems.Length);

            foreach(var ps in trailSystems) {
                if(ps == null) continue;
                if(!CanSimulateTrailParticleSystem(ps)) continue;
                anyValidTrailProcessed = true;
                validTrailSystems.Add(ps);

                if(previewTrail) {
                    EnsureGameObjectHierarchyActive(ps.transform);
                }

                var isFrozenTail = ShouldFreezeTrailSystem(ps);
                var syntheticVelocityForSystem = previewTrail ? tailSyntheticVelocity : Vector3.zero;
                ApplyTailSyntheticVelocity(ps, isFrozenTail, syntheticVelocityForSystem);
                var emission = ps.emission;
                emission.enabled = previewTrail;
                var main = ps.main;
                if(isFrozenTail) {
                    emission.rateOverTimeMultiplier = emissionScale;
                    emission.rateOverDistanceMultiplier = emissionScale;
                    var trails = ps.trails;
                    if(trails.enabled) {
                        var tailBoost = Mathf.Max(1f, trailTailEmissionBoost);
                        emission.rateOverTimeMultiplier = emissionScale * tailBoost;
                        emission.rateOverDistanceMultiplier = emissionScale * tailBoost;
                    }

                    main.simulationSpeed = Mathf.Max(0.01f, Mathf.Lerp(0.15f, 1.5f, velocityRatio));
                }

                if(!previewTrail) {
                    ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(false);
                    continue;
                }

                if(!ps.gameObject.activeSelf) {
                    ps.gameObject.SetActive(true);
                }
                liveTrailSystems.Add(ps);
            }

            if(!anyValidTrailProcessed) {
                _lastLiveTrailEditorSampleTime = -1d;
                return;
            }

            if(previewTrail && needsResample && liveTrailSystems.Count > 0) {
                PrepareLiveTrailSystems(liveTrailSystems);
            }

            if(previewTrail && liveTrailSystems.Count > 0) {
                EnsureLiveTrailPlayback(liveTrailSystems);
            }

            if(logTrailDebug && (logTrailDebugEveryApply || needsResample)) {
                LogTrailDebug(validTrailSystems, needsResample, fakeVelocity, velocityRatio, emissionScale);
            }

            _lastAppliedTrailLifecycle = trailLifecycle;
            _lastAppliedPreviewTrail = previewTrail;
            _lastAppliedVelocity = fakeVelocity;
            _lastTrailSystemSignature = trailSignature;
            _lastAppliedTailSyntheticVelocityScale = tailSyntheticVelocityScale;
            if(!previewTrail) {
                _lastLiveTrailEditorSampleTime = -1d;
            }
        }

        private static bool ShouldFreezeTrailSystem(ParticleSystem ps) {
            return IsTailTrailSystem(ps);
        }

        private static bool IsTailTrailSystem(ParticleSystem ps) {
            if(ps == null) return false;

            var trails = ps.trails;
            return trails.enabled || string.Equals(ps.name, "trail", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyTailSyntheticVelocity(ParticleSystem ps, bool isTailSystem, Vector3 syntheticVelocity) {
            if(ps == null) return;

            var velocityOverLifetime = ps.velocityOverLifetime;
            if(!isTailSystem) {
                if(velocityOverLifetime.enabled) {
                    velocityOverLifetime.enabled = false;
                }
                return;
            }

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.emitterVelocityMode = ParticleSystemEmitterVelocityMode.Transform;

            var trails = ps.trails;
            if(trails is { enabled: true, worldSpace: false }) {
                trails.worldSpace = true;
            }

            if(syntheticVelocity.sqrMagnitude < 0.0001f) {
                if(velocityOverLifetime.enabled) {
                    velocityOverLifetime.enabled = false;
                }
                return;
            }

            velocityOverLifetime.enabled = true;
            velocityOverLifetime.space = ParticleSystemSimulationSpace.World;

            // Move particles opposite fake movement so the tail stretches behind the mannequin.
            velocityOverLifetime.x = new ParticleSystem.MinMaxCurve(-syntheticVelocity.x);
            velocityOverLifetime.y = new ParticleSystem.MinMaxCurve(-syntheticVelocity.y);
            velocityOverLifetime.z = new ParticleSystem.MinMaxCurve(-syntheticVelocity.z);
        }

        private static void PrepareLiveTrailSystems(List<ParticleSystem> liveTrailSystems) {
            if(liveTrailSystems == null || liveTrailSystems.Count == 0) return;

            foreach(var ps in liveTrailSystems) {
                if(ps == null) continue;
                if(ps.useAutoRandomSeed) continue;
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(false);
                ps.useAutoRandomSeed = true;
            }
        }

        private void EnsureLiveTrailPlayback(List<ParticleSystem> liveTrailSystems) {
            if(liveTrailSystems == null || liveTrailSystems.Count == 0) return;

            foreach(var ps in liveTrailSystems) {
                if(ps == null) continue;

                EnsureGameObjectHierarchyActive(ps.transform);

                if(ps.isPaused || !ps.isPlaying) {
                    ps.Play(false);
                }
            }

#if UNITY_EDITOR
            if(!Application.isPlaying) {
                StepLiveTrailSystemsInEditor(liveTrailSystems);
            }
#endif
        }

#if UNITY_EDITOR
        private void StepLiveTrailSystemsInEditor(List<ParticleSystem> liveTrailSystems) {
            if(liveTrailSystems == null || liveTrailSystems.Count == 0) return;

            var now = EditorApplication.timeSinceStartup;
            if(_lastLiveTrailEditorSampleTime < 0d) {
                _lastLiveTrailEditorSampleTime = now;
                return;
            }

            var dt = Mathf.Clamp((float)(now - _lastLiveTrailEditorSampleTime), 0f, 0.05f);
            _lastLiveTrailEditorSampleTime = now;
            if(dt <= 0f) return;

            foreach(var ps in liveTrailSystems) {
                if(ps == null) continue;
                ps.Simulate(dt, false, false, true);
            }
        }
#endif

        private void LogTrailDebug(
            List<ParticleSystem> validTrailSystems,
            bool needsResample,
            Vector3 fakeVelocity,
            float velocityRatio,
            float emissionScale) {
            if(validTrailSystems == null || validTrailSystems.Count == 0) {
                Debug.Log("[PlayerMannequinConfig][TrailDebug] No valid trail systems found after filtering.", this);
                return;
            }

            var allZero = true;
            var lines = new List<string>(validTrailSystems.Count);
            foreach(var ps in validTrailSystems) {
                if(ps == null) continue;
                var main = ps.main;
                var emission = ps.emission;
                var trails = ps.trails;
                var velocity = ps.velocityOverLifetime;
                var count = ps.particleCount;
                var isTail = IsTailTrailSystem(ps);
                if(count > 0) allZero = false;

                lines.Add(
                    $"'{ps.name}': count={count}, paused={ps.isPaused}, playing={ps.isPlaying}, " +
                    $"simSpace={main.simulationSpace}, emitEnabled={emission.enabled}, " +
                    $"rateTime={emission.rateOverTimeMultiplier:0.###}, rateDistance={emission.rateOverDistanceMultiplier:0.###}, " +
                    $"trailsEnabled={trails.enabled}, tailClassified={isTail}, autoSeed={ps.useAutoRandomSeed}, " +
                    $"velOL={velocity.enabled}");
            }

            Debug.Log(
                $"[PlayerMannequinConfig][TrailDebug] resample={needsResample}, lifecycle={trailLifecycle:0.###}, " +
                $"fakeVelocity={fakeVelocity}, velocityRatio={velocityRatio:0.###}, emissionScale={emissionScale:0.###}, " +
                $"mode=LiveAuraSyntheticTail, tailSyntheticVelocityScale={tailSyntheticVelocityScale:0.###}, " +
                $"systems={validTrailSystems.Count}, allParticleCountsZero={allZero}\n" +
                string.Join("\n", lines),
                this);
        }

        private void ApplyWeaponVisuals() {
            ClampWeaponSelectionIndices();

            var shouldApply = selectedPrimaryIndex != _lastAppliedPrimaryIndex
                              || selectedSecondaryIndex != _lastAppliedSecondaryIndex
                              || handWeaponSlot != _lastAppliedHandSlot;
            if(!shouldApply) return;

            SetAllWeaponObjectsActive(primaryOptions, false);
            SetAllWeaponObjectsActive(secondaryOptions, false);

            var primary = GetOption(primaryOptions, selectedPrimaryIndex);
            var secondary = GetOption(secondaryOptions, selectedSecondaryIndex);

            if(handWeaponSlot == HandWeaponSlot.Primary) {
                SetOptionVisual(primary, true, false);
                SetOptionVisual(secondary, false, true);
            } else {
                SetOptionVisual(primary, false, true);
                SetOptionVisual(secondary, true, false);
            }

            _lastAppliedPrimaryIndex = selectedPrimaryIndex;
            _lastAppliedSecondaryIndex = selectedSecondaryIndex;
            _lastAppliedHandSlot = handWeaponSlot;
        }

        private bool CanSimulateTrailParticleSystem(ParticleSystem ps, bool logWarnings = true) {
            if(ps == null) return false;

            var go = ps.gameObject;
            if(go == null) return false;

#if UNITY_EDITOR
            if(EditorUtility.IsPersistent(ps) || EditorUtility.IsPersistent(go)) {
                if(logWarnings) WarnInvalidTrailReference(ps, "reference points to an asset, not a scene instance");
                return false;
            }

            if(PrefabUtility.IsPartOfPrefabAsset(ps) || PrefabUtility.IsPartOfPrefabAsset(go)) {
                if(logWarnings) WarnInvalidTrailReference(ps, "reference points to a prefab asset");
                return false;
            }

            // Prefab Mode contents are not scene instances and can trigger Unity's
            // "Instantiate Particle System Prefabs..." warning when simulated.
            if(PrefabStageUtility.GetPrefabStage(go) != null &&
               !PrefabUtility.IsPartOfPrefabInstance(go)) {
                if(logWarnings) WarnInvalidTrailReference(ps, "reference is from Prefab Mode contents (not an instantiated scene object)");
                return false;
            }
#endif

            var scene = go.scene;
            if(scene.IsValid() && scene.isLoaded) return true;
            // This can happen transiently during editor activation/order-of-execution.
            if(logWarnings && Application.isPlaying) {
                WarnInvalidTrailReference(ps, "reference is not a loaded scene instance");
            }
            return false;

        }

        private void WarnInvalidTrailReference(ParticleSystem ps, string reason) {
            if(ps == null) return;
            var id = ps.GetInstanceID();
            if(!_invalidTrailWarningIds.Add(id)) return;
            var go = ps.gameObject;
            var sceneName = go != null && go.scene.IsValid() ? go.scene.name : "(invalid scene)";
#if UNITY_EDITOR
            var persistent = go != null && EditorUtility.IsPersistent(go);
            var prefabAsset = go != null && PrefabUtility.IsPartOfPrefabAsset(go);
            Debug.LogWarning(
                $"[PlayerMannequinConfig] Skipping trail system '{ps.name}' because {reason}. " +
                $"scene='{sceneName}', persistent={persistent}, prefabAsset={prefabAsset}. " +
                "Assign scene instance ParticleSystems under the mannequin.",
                this);
#else
            Debug.LogWarning($"[PlayerMannequinConfig] Skipping trail system '{ps.name}' because {reason}. scene='{sceneName}'.", this);
#endif
        }

        private int GetAnimatorWeaponIndex() {
            return handWeaponSlot == HandWeaponSlot.Primary ? 0 : 1;
        }

        private Vector2 GetLocomotionVector() {
            var fakeVelocity = GetConfiguredFakeVelocity();
            var localVelocity = transform.InverseTransformDirection(fakeVelocity);
            return new Vector2(
                Mathf.Clamp(localVelocity.x / PlayerPreviewMaxSpeed, -1f, 1f),
                Mathf.Clamp(localVelocity.z / PlayerPreviewMaxSpeed, -1f, 1f)
            );
        }

        private void ClampWeaponSelectionIndices() {
            if(primaryOptions == null || primaryOptions.Length == 0) {
                selectedPrimaryIndex = 0;
            } else {
                selectedPrimaryIndex = Mathf.Clamp(selectedPrimaryIndex, 0, primaryOptions.Length - 1);
            }

            if(secondaryOptions == null || secondaryOptions.Length == 0) {
                selectedSecondaryIndex = 0;
            } else {
                selectedSecondaryIndex = Mathf.Clamp(selectedSecondaryIndex, 0, secondaryOptions.Length - 1);
            }
        }

        private static WeaponVisualOption GetOption(WeaponVisualOption[] options, int index) {
            if(options == null || options.Length == 0) return null;
            if(index < 0 || index >= options.Length) return null;
            return options[index];
        }

        private static void SetAllWeaponObjectsActive(WeaponVisualOption[] options, bool active) {
            if(options == null) return;
            foreach(var t in options) {
                SetOptionVisual(t, active, active);
            }
        }

        private static void SetOptionVisual(WeaponVisualOption option, bool handActive, bool backActive) {
            if(option == null) return;
            if(option.handObject != null) {
                option.handObject.SetActive(handActive);
            }

            if(option.backObject != null) {
                option.backObject.SetActive(backActive);
            }
        }

        private void ApplyShotSimulation() {
            var activeOption = GetActiveHandWeaponOption();
            var activeOptionId = GetActiveShotOptionId();

            if(!simulateShot) {
                if(_lastSimulateShot) {
                    ResetShotSimulationForAllOptions();
                }

                _lastSimulateShot = false;
                _lastShotLifetime = shotLifetime;
                _lastShotOptionId = activeOptionId;
                return;
            }

            if(activeOption == null) {
                _lastSimulateShot = true;
                _lastShotLifetime = shotLifetime;
                _lastShotOptionId = 0;
                return;
            }

            EnsureShotReferences(activeOption);

            var shouldResimulate = !_lastSimulateShot
                                   || !Mathf.Approximately(_lastShotLifetime, shotLifetime)
                                   || _lastShotOptionId != activeOptionId;
            if(!shouldResimulate) return;

            ApplyShotSimulationToOption(activeOption, Mathf.Max(0f, shotLifetime));

            _lastSimulateShot = true;
            _lastShotLifetime = shotLifetime;
            _lastShotOptionId = activeOptionId;
        }

        private void ApplyShotSimulationToOption(WeaponVisualOption option, float lifetime) {
            if(option == null) return;

            var origin = option.shotOrigin != null ? option.shotOrigin : option.handObject != null ? option.handObject.transform : transform;
            var speed = Mathf.Max(0f, option.simulatedShotSpeed);
            var distance = speed * lifetime;
            var end = origin.position + origin.forward * distance;

            if(option.projectileVisual != null) {
                EnsureGameObjectHierarchyActive(option.projectileVisual.transform);
                option.projectileVisual.SetActive(true);
                option.projectileVisual.transform.position = end;
                option.projectileVisual.transform.rotation = origin.rotation;
            }

            if(option.shotParticleSystems != null) {
                foreach(var ps in option.shotParticleSystems) {
                    if(ps == null) continue;
                    if(!CanSimulateTrailParticleSystem(ps)) continue;
                    EnsureGameObjectHierarchyActive(ps.transform);
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Simulate(lifetime, true, true, true);
                    ps.Pause(true);
                }
            }

            if(option.shotVisualEffects != null) {
                foreach(var vfx in option.shotVisualEffects) {
                    if(vfx == null) continue;
                    EnsureGameObjectHierarchyActive(vfx.transform);
                    vfx.Reinit();
                    vfx.Play();
                }
            }

            if(option.shotTrailRenderers != null) {
                foreach(var trail in option.shotTrailRenderers) {
                    if(trail == null) continue;
                    EnsureGameObjectHierarchyActive(trail.transform);
                    trail.Clear();
                    trail.time = Mathf.Max(0.01f, lifetime);
                    trail.emitting = true;
                    var position = origin.position;
                    trail.transform.position = position;
                    trail.AddPosition(position);
                    trail.AddPosition(end);
                }
            }

            if(!HasAnyShotOutputConfigured(option)) {
                WarnInvalidShotSetup(option, "no shot outputs configured (VFX, ParticleSystem, TrailRenderer, or projectileVisual)");
            }
        }

        private void ResetShotSimulationForAllOptions() {
            ResetShotOptionArray(primaryOptions);
            ResetShotOptionArray(secondaryOptions);
        }

        private static void ResetShotOptionArray(WeaponVisualOption[] options) {
            if(options == null) return;
            foreach(var option in options) {
                if(option == null) continue;

                if(option.projectileVisual != null && option.projectileVisual.activeSelf) {
                    option.projectileVisual.SetActive(false);
                }

                if(option.shotParticleSystems != null) {
                    foreach(var ps in option.shotParticleSystems) {
                        if(ps == null) continue;
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    }
                }

                if(option.shotVisualEffects != null) {
                    foreach(var vfx in option.shotVisualEffects) {
                        if(vfx == null) continue;
                        vfx.Stop();
                    }
                }

                if(option.shotTrailRenderers == null) continue;
                foreach(var trail in option.shotTrailRenderers) {
                    if(trail == null) continue;
                    trail.Clear();
                    trail.emitting = false;
                }
            }
        }

        private WeaponVisualOption GetActiveHandWeaponOption() {
            return handWeaponSlot == HandWeaponSlot.Primary
                ? GetOption(primaryOptions, selectedPrimaryIndex)
                : GetOption(secondaryOptions, selectedSecondaryIndex);
        }

        private static void EnsureShotReferences(WeaponVisualOption option) {
            if(option == null || option.handObject == null) return;

            if(option.autoFindShotParticleSystems &&
               (option.shotParticleSystems == null || option.shotParticleSystems.Length == 0)) {
                option.shotParticleSystems = option.handObject.GetComponentsInChildren<ParticleSystem>(true);
            }

            if(option.autoFindShotVisualEffects &&
               (option.shotVisualEffects == null || option.shotVisualEffects.Length == 0)) {
                option.shotVisualEffects = option.handObject.GetComponentsInChildren<VisualEffect>(true);
            }

            if(option.autoFindShotTrailRenderers &&
               (option.shotTrailRenderers == null || option.shotTrailRenderers.Length == 0)) {
                option.shotTrailRenderers = option.handObject.GetComponentsInChildren<TrailRenderer>(true);
            }
        }

        private int GetActiveShotOptionId() {
            var selectedIndex = handWeaponSlot == HandWeaponSlot.Primary
                ? selectedPrimaryIndex
                : selectedSecondaryIndex;
            return ((int)handWeaponSlot * 1000) + Mathf.Max(0, selectedIndex) + 1;
        }

        private static bool HasAnyShotOutputConfigured(WeaponVisualOption option) {
            if(option == null) return false;
            return option.projectileVisual != null
                   || option.shotParticleSystems is { Length: > 0 }
                   || option.shotVisualEffects is { Length: > 0 }
                   || option.shotTrailRenderers is { Length: > 0 };
        }

        private void WarnInvalidShotSetup(WeaponVisualOption option, string reason) {
            if(option == null) return;
            var id = option.GetHashCode();
            if(!_invalidShotWarningIds.Add(id)) return;
            Debug.LogWarning($"[PlayerMannequinConfig] Shot simulation for '{option.displayName}' has {reason}.", this);
        }

        public string[] GetPrimaryOptionNames() {
            return GetOptionNames(primaryOptions, "Primary");
        }

        public string[] GetSecondaryOptionNames() {
            return GetOptionNames(secondaryOptions, "Secondary");
        }

        public int SelectedPrimaryIndex {
            get => selectedPrimaryIndex;
            set => selectedPrimaryIndex = value;
        }

        public int SelectedSecondaryIndex {
            get => selectedSecondaryIndex;
            set => selectedSecondaryIndex = value;
        }

        private static string[] GetOptionNames(WeaponVisualOption[] options, string fallbackPrefix) {
            if(options == null || options.Length == 0) {
                return new[] { $"No {fallbackPrefix} Options" };
            }

            var names = new string[options.Length];
            for(var i = 0; i < options.Length; i++) {
                var name = options[i]?.displayName;
                names[i] = string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix} {i + 1}" : name;
            }

            return names;
        }

        private void OnDrawGizmosSelected() {
            if(!showVelocityGizmo) return;

            var velocity = GetConfiguredFakeVelocity();
            if(velocity.sqrMagnitude < 0.0001f) return;

            var origin = transform.position + Vector3.up * 1.2f;
            var end = origin + velocity * velocityGizmoScale;

            Gizmos.color = velocityGizmoColor;
            Gizmos.DrawLine(origin, end);
            Gizmos.DrawSphere(end, 0.06f);
        }

        private Vector3 GetConfiguredFakeVelocity() {
            var direction = Quaternion.Euler(fakeVelocityPitchDegrees, fakeVelocityYawDegrees, 0f) * Vector3.forward;
            return direction * Mathf.Max(0f, fakeVelocityMagnitude);
        }

        private int ApplyUpperBodyLayerWeights() {
            if(animator == null) return -1;

            var weaponLayerIndex = ResolveLayerIndex(WeaponHoldLayerName);
            if(weaponLayerIndex >= 0) {
                animator.SetLayerWeight(weaponLayerIndex, Mathf.Clamp01(weaponHoldLayerWeight));
            }

            var rightHandLayerIndex = ResolveLayerIndex(RightHandHoldLayerName);
            if(rightHandLayerIndex >= 0) {
                animator.SetLayerWeight(rightHandLayerIndex, Mathf.Clamp01(rightHandHoldLayerWeight));
            }

            return weaponLayerIndex;
        }

        private void ApplyWeaponHoldLayerState(int weaponHoldLayerIndex) {
            if(animator == null || weaponHoldLayerIndex < 0) return;

            var stateName = handWeaponSlot == HandWeaponSlot.Primary
                ? PrimaryWeaponHoldStateName
                : SecondaryWeaponHoldStateName;
            if(string.IsNullOrWhiteSpace(stateName)) return;

            var t = Mathf.Clamp01(weaponHoldStateNormalizedTime);

            // Prefer direct state name (easier authoring in inspector).
            animator.Play(stateName, weaponHoldLayerIndex, t);

            // Fallback to layer-prefixed path if controller expects full path.
            animator.Play($"{WeaponHoldLayerName}.{stateName}", weaponHoldLayerIndex, t);
        }

        private int ResolveLayerIndex(string layerName) {
            if(string.IsNullOrWhiteSpace(layerName)) return -1;
            if(!IsAnimatorInitializedForLayerQuery()) return -1;
            return animator.GetLayerIndex(layerName);
        }

        private bool HaveLayerWeightSettingsChanged() {
            var weaponLayerIndex = ResolveLayerIndex(WeaponHoldLayerName);
            var rightHandLayerIndex = ResolveLayerIndex(RightHandHoldLayerName);
            var weaponWeight = Mathf.Clamp01(weaponHoldLayerWeight);
            var rightHandWeight = Mathf.Clamp01(rightHandHoldLayerWeight);

            return weaponLayerIndex != _lastWeaponHoldLayerIndex
                   || rightHandLayerIndex != _lastRightHandLayerIndex
                   || !Mathf.Approximately(weaponWeight, _lastWeaponHoldLayerWeight)
                   || !Mathf.Approximately(rightHandWeight, _lastRightHandLayerWeight);
        }

        private void CacheCurrentLayerWeightState() {
            _lastWeaponHoldLayerIndex = ResolveLayerIndex(WeaponHoldLayerName);
            _lastRightHandLayerIndex = ResolveLayerIndex(RightHandHoldLayerName);
            _lastWeaponHoldLayerWeight = Mathf.Clamp01(weaponHoldLayerWeight);
            _lastRightHandLayerWeight = Mathf.Clamp01(rightHandHoldLayerWeight);
        }

        private void ApplyLookPitch(bool forceRefreshBaseFromCurrentPose) {
            if(lookPitchTarget == null) return;

            var effectiveTarget = lookPitchTarget;
            CacheLookPitchSpineProxy();

            if(_lookPitchBaseTarget != effectiveTarget) {
                _lookPitchBaseTarget = effectiveTarget;
                _lastAppliedLookPitchOffset = Quaternion.identity;
                _hasCachedLookPitchBaseRotation = false;
            }

            // Only remove the previous look offset when the current transform still contains it.
            // If animator just refreshed this pose, localRotation is already neutral and should be sampled directly.
            if(!forceRefreshBaseFromCurrentPose && _hasCachedLookPitchBaseRotation) {
                effectiveTarget.localRotation *= Quaternion.Inverse(_lastAppliedLookPitchOffset);
            }

            if(forceRefreshBaseFromCurrentPose || !_hasCachedLookPitchBaseRotation) {
                _cachedLookPitchBaseLocalRotation = effectiveTarget.localRotation;
                _hasCachedLookPitchBaseRotation = true;
            }

            var axis = Vector3.right;
            var yawAxis = Vector3.up;

            var weightedPitch = lookPitchDegrees * Mathf.Clamp01(lookPitchWeight);
            var weightedYaw = lookYawDegrees * Mathf.Clamp01(lookYawWeight);
            var pitchOffset = Quaternion.AngleAxis(weightedPitch, axis);
            var yawOffset = Quaternion.AngleAxis(weightedYaw, yawAxis);
            var newOffset = yawOffset * pitchOffset;
            effectiveTarget.localRotation = _cachedLookPitchBaseLocalRotation * newOffset;
            _lastAppliedLookPitchOffset = newOffset;

            if(!logLookDebug || (!logLookDebugEveryApply && !forceRefreshBaseFromCurrentPose)) return;
            var mode = Application.isPlaying ? "Play" : "Edit";
            var proxyDebug = string.Empty;
            if(_cachedLookPitchSpineProxy != null
               && _cachedLookPitchSpineProxy.TryGetDebugState(
                   out var originalSpine,
                   out var proxyWorld,
                   out var expectedSpineWorld,
                   out var actualSpineWorld,
                   out var offset)) {
                var spineErrDeg = Quaternion.Angle(actualSpineWorld, expectedSpineWorld);
                proxyDebug =
                    $", proxyWorldEuler={proxyWorld.eulerAngles}, expectedSpineWorldEuler={expectedSpineWorld.eulerAngles}, " +
                    $"actualSpineWorldEuler={actualSpineWorld.eulerAngles}, spineErrDeg={spineErrDeg:0.###}, " +
                    $"offsetEuler={offset.eulerAngles}, originalLocalEuler={originalSpine.localRotation.eulerAngles}";
            }
            Debug.Log(
                $"[PlayerMannequinConfig][LookDebug][{mode}] target='{effectiveTarget.name}', " +
                $"baseEuler={_cachedLookPitchBaseLocalRotation.eulerAngles}, " +
                $"offsetEuler={newOffset.eulerAngles}, finalEuler={effectiveTarget.localRotation.eulerAngles}, " +
                $"pitch={lookPitchDegrees:0.###}, yaw={lookYawDegrees:0.###}, " +
                $"pitchW={lookPitchWeight:0.###}, yawW={lookYawWeight:0.###}, " +
                $"animUpdated={forceRefreshBaseFromCurrentPose}, forceRefresh={forceRefreshBaseFromCurrentPose}" +
                proxyDebug,
                this);
        }

        private void CacheLookPitchSpineProxy(bool forceRefresh = false) {
            if(!forceRefresh && _cachedLookPitchProxyTarget == lookPitchTarget) {
                return;
            }

            _cachedLookPitchProxyTarget = lookPitchTarget;
            _cachedLookPitchSpineProxy = lookPitchTarget != null
                ? lookPitchTarget.GetComponent<KevinIglesias.SpineProxy>()
                : null;
        }

        private void CaptureLookPitchSpineProxyOffsetFromScene() {
            if(Application.isPlaying) return;

            CacheLookPitchSpineProxy();
            if(_cachedLookPitchSpineProxy == null) return;

            if(!_cachedLookPitchSpineProxy.TryGetDebugState(
                   out _,
                   out _,
                   out _,
                   out _,
                   out var offset)) {
                return;
            }

            capturedLookPitchSpineProxyOffset = offset;
            hasCapturedLookPitchSpineProxyOffset = true;
        }

        private void ApplyCapturedLookPitchSpineProxyOffsetIfAvailable() {
            if(!hasCapturedLookPitchSpineProxyOffset) return;

            CacheLookPitchSpineProxy();
            _cachedLookPitchSpineProxy?.SetRotationOffset(capturedLookPitchSpineProxyOffset);
        }

        private void ApplyLookPitchProxyToOriginalSpine() {
            CacheLookPitchSpineProxy();
            _cachedLookPitchSpineProxy?.ApplyProxyToOriginalSpine();
        }

        private void QueueRuntimeLookProbe() {
            if(!Application.isPlaying || !logLookDebug) return;
            if(Time.unscaledTime < _nextRuntimeLookProbeAt) return;
            if(_runtimeLookProbeQueued) return;

            _nextRuntimeLookProbeAt = Time.unscaledTime + RuntimeLookProbeInterval;
            _runtimeLookProbeQueued = true;
            LogRuntimeLookProbe("LateUpdate");
            StartCoroutine(LogRuntimeLookProbeAtEndOfFrame());
        }

        private IEnumerator LogRuntimeLookProbeAtEndOfFrame() {
            yield return new WaitForEndOfFrame();
            LogRuntimeLookProbe("EndOfFrame");
            _runtimeLookProbeQueued = false;
        }

        private void LogRuntimeLookProbe(string stage) {
            if(!Application.isPlaying || !logLookDebug) return;

            CacheLookPitchSpineProxy();
            if(_cachedLookPitchSpineProxy == null) return;
            if(!_cachedLookPitchSpineProxy.TryGetDebugState(
                   out var originalSpine,
                   out var proxyWorld,
                   out var expectedSpineWorld,
                   out var actualSpineWorld,
                   out var offset)) {
                return;
            }

            var errorDeg = Quaternion.Angle(actualSpineWorld, expectedSpineWorld);
            if(!logLookDebugEveryApply && errorDeg <= RuntimeLookProbeErrorThresholdDeg) return;

            var stateHash = 0;
            var stateNorm = 0f;
            var transition = false;
            if(animator != null && animator.isInitialized) {
                var state = animator.GetCurrentAnimatorStateInfo(0);
                stateHash = state.shortNameHash;
                stateNorm = state.normalizedTime;
                transition = animator.IsInTransition(0);
            }

            Debug.Log(
                $"[PlayerMannequinConfig][LookProbe][{stage}] " +
                $"errDeg={errorDeg:0.###}, " +
                $"proxyWorldEuler={proxyWorld.eulerAngles}, expectedSpineWorldEuler={expectedSpineWorld.eulerAngles}, actualSpineWorldEuler={actualSpineWorld.eulerAngles}, " +
                $"offsetEuler={offset.eulerAngles}, " +
                $"targetLocalEuler={(lookPitchTarget != null ? lookPitchTarget.localRotation.eulerAngles.ToString() : "null")}, " +
                $"originalLocalEuler={originalSpine.localRotation.eulerAngles}, " +
                $"pitch={lookPitchDegrees:0.###}, yaw={lookYawDegrees:0.###}, " +
                $"animInit={(animator != null && animator.isInitialized)}, animSpeed={(animator != null ? animator.speed : -1f):0.###}, " +
                $"stateHash={stateHash}, stateNorm={stateNorm:0.###}, inTransition={transition}",
                this);
        }

        private bool TryPrepareAnimatorForPreview() {
            if(animator == null || animator.runtimeAnimatorController == null) return false;
            if(!animator.enabled || !animator.gameObject.activeInHierarchy) return false;

#if UNITY_EDITOR
            if(Application.isPlaying || animator.isInitialized) return animator.isInitialized;
            animator.Rebind();
            animator.Update(0f);
#endif

            return animator.isInitialized;
        }

        private bool IsAnimatorInitializedForLayerQuery() {
            return animator != null
                   && animator.runtimeAnimatorController != null
                   && animator.enabled
                   && animator.gameObject.activeInHierarchy
                   && animator.isInitialized;
        }

        private void EnsureGameObjectHierarchyActive(Transform target) {
            if(target == null) return;

            var current = target;
            while(current != null) {
                if(!current.gameObject.activeSelf) {
                    current.gameObject.SetActive(true);
                }

                if(current == transform) {
                    break;
                }

                current = current.parent;
            }
        }

        private void EnsureTrailSystemReferences() {
            var validExisting = new List<ParticleSystem>();
            if(trailSystems is { Length: > 0 }) {
                foreach(var ps in trailSystems) {
                    if(CanSimulateTrailParticleSystem(ps, logWarnings: false)) {
                        validExisting.Add(ps);
                    }
                }
            }

            if(validExisting.Count != (trailSystems?.Length ?? 0)) {
                trailSystems = validExisting.ToArray();
            }
        }

        private void EnsureMannequinBodyRendererReference() {
            if(!autoFindMannequinBodyRenderer || mannequinBodyRenderer != null) return;
            mannequinBodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
        }

        private bool IsSceneReadyForPreview() {
            if(Application.isPlaying) return true;
            var scene = gameObject.scene;
            return scene.IsValid() && scene.isLoaded;
        }

        private int ComputeTrailSystemSignature() {
            unchecked {
                var hash = 17;
                if(trailSystems == null) return hash;
                foreach(var ps in trailSystems) {
                    hash = hash * 31 + (ps != null ? ps.GetInstanceID() : 0);
                }
                return hash;
            }
        }

        private void QueueDeferredApplyInEditor() {
#if UNITY_EDITOR
            if(Application.isPlaying || _deferredApplyQueued) return;
            _deferredApplyQueued = true;
            EditorApplication.delayCall += ExecuteDeferredApplyInEditor;
#endif
        }

#if UNITY_EDITOR
        private void ExecuteDeferredApplyInEditor() {
            _deferredApplyQueued = false;
            if(this == null) return;
            _forceTrailResampleThisApply = true;
            ApplyNow();
            _forceTrailResampleThisApply = false;
        }
#endif

        private static readonly int WeaponIndexHash = Animator.StringToHash("WeaponIndex");
        private static readonly int MoveXHash = Animator.StringToHash("moveX");
        private static readonly int MoveYHash = Animator.StringToHash("moveY");
        private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");
        private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
        private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
        private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");
        private static readonly int IsSlidingHash = Animator.StringToHash("IsSliding");
        private static readonly int IsWallRunningHash = Animator.StringToHash("IsWallRunning");
        private static readonly int RightWallRunHash = Animator.StringToHash("RightWallRun");
        private static readonly int WallRunDirectionHash = Animator.StringToHash("WallRunDirection");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int ColorNoUnderscoreId = Shader.PropertyToID("Color");
        private static readonly int MainColorId = Shader.PropertyToID("_MainColor");
        private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
        private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");
        // Mirrors PlayerMovementController's sprint speed so mannequin locomotion/trail scaling
        // matches the real player without extra inspector tuning.
        private const float PlayerPreviewMaxSpeed = 10f;
        private const string WeaponHoldLayerName = "Weapon Hold Layer";
        private const string RightHandHoldLayerName = "Right Hand Hold Layer";
        private const string PrimaryWeaponHoldStateName = "AKAim";
        private const string SecondaryWeaponHoldStateName = "PistolAim";
        private const int MannequinSurfaceMaterialIndex = 1;
        private const string DefaultTrailMainMaterialPath = "Assets/Player Assets/Trail/pz_27b2_lgt.mat";
        private const string DefaultElectricMainMaterialPath = "Assets/Player Assets/Trail/Electric_Splat_Hit.mat";
        private const string DefaultTrailRibbonMaterialPath = "Assets/Player Assets/Trail/glow.mat";
        private const string DefaultLightningBlueMaterialPath = "Assets/Imported/Game VFX - Sword Trails/Materials/lightning_blue.mat";
    }
}
