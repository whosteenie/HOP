using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Editor.Game {
    /// <summary>
    /// Deterministic baker for per-weapon grapple clips.
    /// Each target clip gets AK clavicle_l local-position curves plus:
    /// offset = AK viewmodel local position - weapon viewmodel local position.
    /// </summary>
    public static class GrappleAnimViewmodelOffsetApplier {
        private const string GrappleAnimFolder = "Assets/Player Assets/Animation/Grapple";
        private const string AkClipName = "A_FP_AKX_Grapple";
        private const string ClavicleBoneName = "clavicle_l";
        private const float TimeEpsilon = 0.0001f;
        // Copy only AK-keyed left-arm curves (excluding clavicle position, which gets its own baked offset).
        private static readonly string[] ArmPoseBones = {
            "clavicle_l",
            "hand_l",
            "index_01_l",
            "index_02_l",
            "index_03_l",
            "middle_01_l",
            "middle_02_l",
            "middle_03_l",
            "pinky_01_l",
            "pinky_02_l",
            "pinky_03_l",
            "ring_01_l",
            "ring_02_l",
            "ring_03_l",
            "thumb_01_l",
            "thumb_02_l",
            "thumb_03_l",
            "lowerarm_twist_01_l",
            "upperarm_twist_01_l",
        };
        private static readonly string[] RigModelCandidatePaths = {
            "Assets/Imported/KINEMATION/FPSAnimationPack/Character/SK_Arms_Mono.fbx",
            "Assets/Player Assets/BLENDER/SK_Arms_Mono.fbx",
        };
        private static readonly string[] ForcedEulerProperties = {
            "localEulerAnglesRaw.x",
            "localEulerAnglesRaw.y",
            "localEulerAnglesRaw.z",
            "m_LocalEulerAngles.x",
            "m_LocalEulerAngles.y",
            "m_LocalEulerAngles.z",
        };

        // WeaponManager.kinemationWeaponBindings reference values.
        private static readonly Vector3 AkViewmodelLocalPosition = new Vector3(0.1699999f, -1.750005f, 0f);
        // Captured from AK runtime pose and treated as gold-standard baseline.
        private static readonly Vector3 AkUpperarmLocalEuler = new Vector3(7.379921f, 309.6783f, 111.0653f);
        private static readonly Vector3 AkLowerarmLocalEuler = new Vector3(349.5138f, 9.435305f, 66.62358f);
        private struct WeaponClipBinding {
            public readonly string ClipName;
            public readonly Vector3 ViewmodelLocalPosition;
            public readonly Vector3 AdditionalClavicleLocalOffset;

            public WeaponClipBinding(string clipName, Vector3 viewmodelLocalPosition,
                Vector3 additionalClavicleLocalOffset = default) {
                ClipName = clipName;
                ViewmodelLocalPosition = viewmodelLocalPosition;
                AdditionalClavicleLocalOffset = additionalClavicleLocalOffset;
            }
        }

        private static readonly WeaponClipBinding[] WeaponBindings = {
            new WeaponClipBinding("A_FP_Drake_Grapple", new Vector3(0.1675f, -1.719001f, 0f)),
            new WeaponClipBinding("A_FP_Kar_Grapple", new Vector3(0.1649999f, -1.735005f, 0f)),
            new WeaponClipBinding("A_FP_M1911_Grapple", new Vector3(0.2099999f, -1.705005f, 0f)),
            // No per-weapon manual extra by default; deterministic root-delta conversion
            // plus AK arm-pose copy/forced arm baseline should keep this aligned.
            new WeaponClipBinding("A_FP_DGL_Grapple", new Vector3(0.1974999f, -1.715005f, 0f)),
            new WeaponClipBinding("A_FP_PDW_Grapple", new Vector3(0.2024998f, -1.702505f, 0f)),
        };

        [MenuItem("Tools/Grapple/Bake Viewmodel Delta Offsets")]
        public static void ApplyOffsets() {
            ApplyOffsetsInternal(verbose: false);
        }

        [MenuItem("Tools/Grapple/Bake Viewmodel Delta Offsets (Verbose)")]
        public static void ApplyOffsetsVerbose() {
            ApplyOffsetsInternal(verbose: true);
        }

        private static void ApplyOffsetsInternal(bool verbose) {
            var akClipPath = $"{GrappleAnimFolder}/{AkClipName}.anim";
            var akClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(akClipPath);
            if(akClip == null) {
                Debug.LogError($"[GrappleBake] Missing AK reference clip: {akClipPath}");
                return;
            }

            if(!TryGetClaviclePath(akClip, out var akClaviclePath)) {
                Debug.LogError($"[GrappleBake] Could not find {ClavicleBoneName} local position path in {akClipPath}");
                return;
            }

            var akX = AnimationUtility.GetEditorCurve(akClip, MakeLocalPositionBinding(akClaviclePath, 'x'));
            var akY = AnimationUtility.GetEditorCurve(akClip, MakeLocalPositionBinding(akClaviclePath, 'y'));
            var akZ = AnimationUtility.GetEditorCurve(akClip, MakeLocalPositionBinding(akClaviclePath, 'z'));
            if(akX == null || akY == null || akZ == null) {
                Debug.LogError($"[GrappleBake] AK clip is missing one or more {ClavicleBoneName} position curves.");
                return;
            }
            var sampleTimes = CollectSampleTimes(akX, akY, akZ);

            var modified = 0;
            foreach(var weapon in WeaponBindings) {
                var targetPath = $"{GrappleAnimFolder}/{weapon.ClipName}.anim";
                var targetClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(targetPath);
                if(targetClip == null) {
                    Debug.LogWarning($"[GrappleBake] Missing target clip: {targetPath}");
                    continue;
                }

                var targetClaviclePath = TryGetClaviclePath(targetClip, out var foundPath)
                    ? foundPath
                    : akClaviclePath;
                var rootOffset = AkViewmodelLocalPosition - weapon.ViewmodelLocalPosition;
                var convertedOffsets = BuildConstantOffsets(sampleTimes, rootOffset);
                var usedRigPath = "constant-fallback";
                if(TryBuildOffsetsFromRig(akClip, akClaviclePath, sampleTimes, rootOffset, out var rigOffsets,
                       out var rigPath)) {
                    convertedOffsets = rigOffsets;
                    usedRigPath = rigPath;
                }
                if(weapon.AdditionalClavicleLocalOffset.sqrMagnitude > 0.00000001f) {
                    AddOffsetToAll(convertedOffsets, weapon.AdditionalClavicleLocalOffset);
                }

                var bakedX = CreateOffsetCurve(akX, sampleTimes, convertedOffsets, 0);
                var bakedY = CreateOffsetCurve(akY, sampleTimes, convertedOffsets, 1);
                var bakedZ = CreateOffsetCurve(akZ, sampleTimes, convertedOffsets, 2);

                AnimationUtility.SetEditorCurve(targetClip, MakeLocalPositionBinding(targetClaviclePath, 'x'), bakedX);
                AnimationUtility.SetEditorCurve(targetClip, MakeLocalPositionBinding(targetClaviclePath, 'y'), bakedY);
                AnimationUtility.SetEditorCurve(targetClip, MakeLocalPositionBinding(targetClaviclePath, 'z'), bakedZ);
                var armPoseCurvesApplied = ApplyEarlyArmPoseMatch(akClip, targetClip, verbose);
                var forcedArmCurvesApplied = ApplyForcedArmRotations(
                    targetClip, targetClaviclePath, sampleTimes, verbose);

                EditorUtility.SetDirty(targetClip);
                modified++;

                if(verbose) {
                    var firstLocalOffset = convertedOffsets.Length > 0 ? convertedOffsets[0] : Vector3.zero;
                    var midLocalOffset = convertedOffsets.Length > 0 ? convertedOffsets[convertedOffsets.Length / 2] : Vector3.zero;
                    var first = new Vector3(
                        bakedX.length > 0 ? bakedX.keys[0].value : 0f,
                        bakedY.length > 0 ? bakedY.keys[0].value : 0f,
                        bakedZ.length > 0 ? bakedZ.keys[0].value : 0f);
                    var last = new Vector3(
                        bakedX.length > 0 ? bakedX.keys[bakedX.length - 1].value : 0f,
                        bakedY.length > 0 ? bakedY.keys[bakedY.length - 1].value : 0f,
                        bakedZ.length > 0 ? bakedZ.keys[bakedZ.length - 1].value : 0f);
                    Debug.Log(
                        $"[GrappleBake] Baked {weapon.ClipName} path={targetClaviclePath} " +
                        $"weaponVM={Format(weapon.ViewmodelLocalPosition)} rootOffset={Format(rootOffset)} " +
                        $"firstLocalOffset={Format(firstLocalOffset)} midLocalOffset={Format(midLocalOffset)} " +
                        $"additionalLocalOffset={Format(weapon.AdditionalClavicleLocalOffset)} " +
                        $"armPoseCurvesApplied={armPoseCurvesApplied} forcedArmCurvesApplied={forcedArmCurvesApplied} " +
                        $"rig={usedRigPath} " +
                        $"first={Format(first)} last={Format(last)}");
                } else {
                    Debug.Log(
                        $"[GrappleBake] Baked {weapon.ClipName} rootOffset={Format(rootOffset)} " +
                        $"additionalLocalOffset={Format(weapon.AdditionalClavicleLocalOffset)} " +
                        $"armPoseCurvesApplied={armPoseCurvesApplied} " +
                        $"forcedArmCurvesApplied={forcedArmCurvesApplied} rig={usedRigPath}");
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[GrappleBake] Done. Modified {modified}/{WeaponBindings.Length} clips using AK source \"{AkClipName}\".");
        }

        private static bool TryGetClaviclePath(AnimationClip clip, out string path) {
            path = null;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var bestScore = -1;

            var scores = new Dictionary<string, int>();
            foreach(var binding in bindings) {
                if(binding.type != typeof(Transform)) continue;
                if(!binding.path.EndsWith(ClavicleBoneName)) continue;

                int axisBit;
                if(binding.propertyName == "m_LocalPosition.x") axisBit = 1;
                else if(binding.propertyName == "m_LocalPosition.y") axisBit = 2;
                else if(binding.propertyName == "m_LocalPosition.z") axisBit = 4;
                else continue;

                if(scores.TryGetValue(binding.path, out var value)) {
                    scores[binding.path] = value | axisBit;
                } else {
                    scores[binding.path] = axisBit;
                }
            }

            foreach(var pair in scores) {
                if(pair.Value > bestScore) {
                    bestScore = pair.Value;
                    path = pair.Key;
                }
            }

            return bestScore > 0;
        }

        private static bool TryGetBonePath(AnimationClip clip, string boneName, out string path) {
            path = null;
            if(clip == null || string.IsNullOrEmpty(boneName)) return false;

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var bestScore = -1;
            var scores = new Dictionary<string, int>();
            foreach(var binding in bindings) {
                if(binding.type != typeof(Transform)) continue;
                if(!binding.path.EndsWith(boneName)) continue;

                var score = 1;
                if(binding.propertyName.StartsWith("m_LocalPosition")) {
                    score = 3;
                } else if(binding.propertyName.IndexOf("EulerAngles", StringComparison.Ordinal) >= 0) {
                    score = 2;
                }

                if(scores.TryGetValue(binding.path, out var existingScore)) {
                    scores[binding.path] = existingScore + score;
                } else {
                    scores[binding.path] = score;
                }
            }

            foreach(var pair in scores) {
                if(pair.Value > bestScore) {
                    bestScore = pair.Value;
                    path = pair.Key;
                }
            }

            return bestScore > 0;
        }

        private static float[] CollectSampleTimes(AnimationCurve curveX, AnimationCurve curveY, AnimationCurve curveZ) {
            var times = new SortedSet<float>();
            AddCurveTimes(curveX, times);
            AddCurveTimes(curveY, times);
            AddCurveTimes(curveZ, times);
            if(times.Count == 0) {
                times.Add(0f);
            }

            var result = new float[times.Count];
            var index = 0;
            foreach(var value in times) {
                result[index++] = value;
            }
            return result;
        }

        private static void AddCurveTimes(AnimationCurve curve, ISet<float> destination) {
            if(curve == null || destination == null) return;
            var keys = curve.keys;
            foreach(var k in keys) {
                destination.Add(k.time);
            }
        }

        private static int ApplyEarlyArmPoseMatch(AnimationClip sourceAkClip, AnimationClip targetClip, bool verbose) {
            if(sourceAkClip == null || targetClip == null) return 0;

            var appliedCurveCount = 0;
            var akBindings = AnimationUtility.GetCurveBindings(sourceAkClip);
            foreach(var boneName in ArmPoseBones) {
                if(!TryGetBonePath(sourceAkClip, boneName, out var akBonePath)) {
                    if(verbose) {
                        Debug.LogWarning($"[GrappleBake] Missing AK bone path for {boneName}.");
                    }
                    continue;
                }

                if(!TryGetBonePath(targetClip, boneName, out var targetBonePath)) {
                    if(verbose) {
                        Debug.LogWarning($"[GrappleBake] Missing target bone path for {boneName} in {targetClip.name}.");
                    }
                    continue;
                }

                foreach(var akBinding in akBindings) {
                    if(akBinding.type != typeof(Transform)) continue;
                    if(!string.Equals(akBinding.path, akBonePath, StringComparison.Ordinal)) continue;
                    if(!IsCopiedPosePropertyName(akBinding.propertyName)) continue;
                    if(string.Equals(boneName, ClavicleBoneName, StringComparison.Ordinal) &&
                       IsLocalPositionPropertyName(akBinding.propertyName)) {
                        // Clavicle position is baked separately with viewmodel-derived offsets.
                        continue;
                    }

                    var akCurve = AnimationUtility.GetEditorCurve(sourceAkClip, akBinding);
                    if(akCurve == null) continue;

                    var targetBinding = akBinding;
                    targetBinding.path = targetBonePath;
                    AnimationUtility.SetEditorCurve(targetClip, targetBinding, CloneCurve(akCurve));
                    appliedCurveCount++;
                }
            }

            return appliedCurveCount;
        }

        private static bool IsCopiedPosePropertyName(string propertyName) {
            if(string.IsNullOrEmpty(propertyName)) return false;
            if(IsLocalPositionPropertyName(propertyName)) return true;
            return propertyName.StartsWith("localEulerAnglesRaw.", StringComparison.Ordinal) ||
                propertyName.StartsWith("localEulerAnglesBaked.", StringComparison.Ordinal) ||
                propertyName.StartsWith("m_LocalEulerAngles.", StringComparison.Ordinal) ||
                propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal);
        }

        private static bool IsLocalPositionPropertyName(string propertyName) {
            if(string.IsNullOrEmpty(propertyName)) return false;
            return propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal);
        }

        private static AnimationCurve CloneCurve(AnimationCurve source) {
            if(source == null) return null;
            var sourceKeys = source.keys;
            var keys = new Keyframe[sourceKeys.Length];
            for(var i = 0; i < sourceKeys.Length; i++) {
                var sourceKey = sourceKeys[i];
                keys[i] = new Keyframe(
                    sourceKey.time,
                    sourceKey.value,
                    sourceKey.inTangent,
                    sourceKey.outTangent,
                    sourceKey.inWeight,
                    sourceKey.outWeight);
            }

            return new AnimationCurve(keys) {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };
        }

        private static int ApplyForcedArmRotations(AnimationClip targetClip, string targetClaviclePath,
            float[] sampleTimes, bool verbose) {
            if(targetClip == null || string.IsNullOrEmpty(targetClaviclePath)) return 0;

            var upperarmPath = $"{targetClaviclePath}/upperarm_l";
            var lowerarmPath = $"{upperarmPath}/lowerarm_l";

            var applied = 0;
            applied += ApplyForcedLocalEulerRotation(targetClip, upperarmPath, AkUpperarmLocalEuler, sampleTimes);
            applied += ApplyForcedLocalEulerRotation(targetClip, lowerarmPath, AkLowerarmLocalEuler, sampleTimes);

            if(verbose) {
                Debug.Log(
                    $"[GrappleBake] Forced AK arm baseline rotations on {targetClip.name}: " +
                    $"upperarmPath={upperarmPath} euler={Format(AkUpperarmLocalEuler)} " +
                    $"lowerarmPath={lowerarmPath} euler={Format(AkLowerarmLocalEuler)} curves={applied}");
            }

            return applied;
        }

        private static int ApplyForcedLocalEulerRotation(AnimationClip targetClip, string bonePath, Vector3 eulerAngles,
            float[] sampleTimes) {
            if(targetClip == null || string.IsNullOrEmpty(bonePath)) return 0;

            var keyTimes = BuildConstantCurveTimes(sampleTimes, targetClip.length);
            var applied = 0;
            foreach(var propertyName in ForcedEulerProperties) {
                var axis = GetAxisFromPropertyName(propertyName);
                if(axis < 0) continue;

                var curve = BuildConstantCurve(keyTimes, GetAxis(eulerAngles, axis));
                AnimationUtility.SetEditorCurve(targetClip, MakeTransformBinding(bonePath, propertyName), curve);
                applied++;
            }

            return applied;
        }

        private static float[] BuildConstantCurveTimes(float[] sampleTimes, float clipLength) {
            if(sampleTimes is not { Length: > 0 })
                return clipLength > TimeEpsilon ? new[] { 0f, clipLength } : new[] { 0f };
            var last = sampleTimes[^1];
            if(!(last < clipLength - TimeEpsilon)) return sampleTimes;
            var result = new float[sampleTimes.Length + 1];
            for(var i = 0; i < sampleTimes.Length; i++) {
                result[i] = sampleTimes[i];
            }
            result[sampleTimes.Length] = clipLength;
            return result;

        }

        private static AnimationCurve BuildConstantCurve(float[] keyTimes, float value) {
            if(keyTimes == null || keyTimes.Length == 0) {
                return new AnimationCurve(new Keyframe(0f, value));
            }

            var keys = new Keyframe[keyTimes.Length];
            for(var i = 0; i < keyTimes.Length; i++) {
                keys[i] = new Keyframe(keyTimes[i], value);
            }

            return new AnimationCurve(keys);
        }

        private static int GetAxisFromPropertyName(string propertyName) {
            if(string.IsNullOrEmpty(propertyName)) return -1;
            if(propertyName.EndsWith(".x")) return 0;
            if(propertyName.EndsWith(".y")) return 1;
            if(propertyName.EndsWith(".z")) return 2;
            return -1;
        }

        private static bool TryBuildOffsetsFromRig(AnimationClip sourceClip, string claviclePath, float[] sampleTimes,
            Vector3 rootOffset, out Vector3[] convertedOffsets, out string usedRigPath) {
            convertedOffsets = null;
            usedRigPath = null;

            if(sourceClip == null || string.IsNullOrEmpty(claviclePath) || sampleTimes == null || sampleTimes.Length == 0) {
                return false;
            }

            var clavicleParentPath = GetParentPath(claviclePath);
            if(string.IsNullOrEmpty(clavicleParentPath)) {
                return false;
            }

            foreach(var candidatePath in RigModelCandidatePaths) {
                var rigAsset = AssetDatabase.LoadAssetAtPath<GameObject>(candidatePath);
                if(rigAsset == null) continue;

                var rigInstance = UnityEngine.Object.Instantiate(rigAsset);
                try {
                    if(rigInstance == null) continue;
                    rigInstance.hideFlags = HideFlags.HideAndDontSave;
                    rigInstance.SetActive(true);

                    var clavicleParent = ResolveHierarchyPath(rigInstance.transform, clavicleParentPath);
                    if(clavicleParent == null) continue;

                    var localOffsets = new Vector3[sampleTimes.Length];
                    for(var i = 0; i < sampleTimes.Length; i++) {
                        var time = sampleTimes[i];
                        sourceClip.SampleAnimation(rigInstance, time);
                        var worldOffset = rigInstance.transform.TransformDirection(rootOffset);
                        localOffsets[i] = clavicleParent.InverseTransformDirection(worldOffset);
                    }

                    convertedOffsets = localOffsets;
                    usedRigPath = candidatePath;
                    return true;
                } finally {
                    if(rigInstance != null) {
                        UnityEngine.Object.DestroyImmediate(rigInstance);
                    }
                }
            }

            return false;
        }

        private static Transform ResolveHierarchyPath(Transform root, string relativePath) {
            if(root == null || string.IsNullOrEmpty(relativePath)) return null;
            var segments = relativePath.Split('/');
            var current = root;
            foreach(var segment in segments) {
                if(string.IsNullOrEmpty(segment)) return null;

                Transform next = null;
                var childCount = current.childCount;
                for(var c = 0; c < childCount; c++) {
                    var child = current.GetChild(c);
                    if(child == null || child.name != segment) continue;
                    next = child;
                    break;
                }

                if(next == null) {
                    return null;
                }

                current = next;
            }

            return current;
        }

        private static Vector3[] BuildConstantOffsets(float[] sampleTimes, Vector3 constantOffset) {
            var result = new Vector3[sampleTimes.Length];
            for(var i = 0; i < result.Length; i++) {
                result[i] = constantOffset;
            }
            return result;
        }

        private static void AddOffsetToAll(Vector3[] offsets, Vector3 additionalOffset) {
            if(offsets == null || offsets.Length == 0) return;
            for(var i = 0; i < offsets.Length; i++) {
                offsets[i] += additionalOffset;
            }
        }

        private static string GetParentPath(string path) {
            if(string.IsNullOrEmpty(path)) return string.Empty;
            var slashIndex = path.LastIndexOf('/');
            return slashIndex <= 0 ? string.Empty : path[..slashIndex];
        }

        private static EditorCurveBinding MakeTransformBinding(string path, string propertyName) {
            return new EditorCurveBinding {
                path = path,
                type = typeof(Transform),
                propertyName = propertyName
            };
        }

        private static EditorCurveBinding MakeLocalPositionBinding(string path, char axis) {
            return new EditorCurveBinding {
                path = path,
                type = typeof(Transform),
                propertyName = $"m_LocalPosition.{axis}"
            };
        }

        private static AnimationCurve CreateOffsetCurve(AnimationCurve source, float[] sampleTimes, Vector3[] localOffsets,
            int axis) {
            if(source == null) {
                return null;
            }

            var sourceKeys = source.keys;
            var keys = new Keyframe[sourceKeys.Length];
            for(var i = 0; i < sourceKeys.Length; i++) {
                var sourceKey = sourceKeys[i];
                var axisOffset = SampleAxisOffset(sourceKey.time, sampleTimes, localOffsets, axis);
                keys[i] = new Keyframe(
                    sourceKey.time,
                    sourceKey.value + axisOffset,
                    sourceKey.inTangent,
                    sourceKey.outTangent,
                    sourceKey.inWeight,
                    sourceKey.outWeight);
            }

            var result = new AnimationCurve(keys) {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };
            return result;
        }

        private static float SampleAxisOffset(float time, float[] sampleTimes, Vector3[] localOffsets, int axis) {
            if(sampleTimes == null || localOffsets == null || sampleTimes.Length == 0 || localOffsets.Length == 0) {
                return 0f;
            }

            if(sampleTimes.Length == 1 || localOffsets.Length == 1) {
                return GetAxis(localOffsets[0], axis);
            }

            if(time <= sampleTimes[0] + TimeEpsilon) {
                return GetAxis(localOffsets[0], axis);
            }

            var lastIndex = sampleTimes.Length - 1;
            if(time >= sampleTimes[lastIndex] - TimeEpsilon) {
                return GetAxis(localOffsets[lastIndex], axis);
            }

            for(var i = 1; i < sampleTimes.Length; i++) {
                var nextTime = sampleTimes[i];
                if(time > nextTime + TimeEpsilon) continue;

                var prevTime = sampleTimes[i - 1];
                var span = Mathf.Max(TimeEpsilon, nextTime - prevTime);
                var t = Mathf.Clamp01((time - prevTime) / span);
                var from = localOffsets[Mathf.Min(i - 1, localOffsets.Length - 1)];
                var to = localOffsets[Mathf.Min(i, localOffsets.Length - 1)];
                return Mathf.LerpUnclamped(GetAxis(from, axis), GetAxis(to, axis), t);
            }

            return GetAxis(localOffsets[Mathf.Min(localOffsets.Length - 1, lastIndex)], axis);
        }

        private static float GetAxis(Vector3 value, int axis) {
            return axis switch {
                0 => value.x,
                1 => value.y,
                _ => value.z
            };
        }

        private static string Format(Vector3 value) {
            return $"({value.x:F6},{value.y:F6},{value.z:F6})";
        }
    }
}
