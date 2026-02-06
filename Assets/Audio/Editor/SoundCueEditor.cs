#if UNITY_EDITOR
using System;
using System.Reflection;
using Game.Audio2;
using UnityEditor;
using UnityEngine;

namespace Game.Audio2.Editor {
    [CustomEditor(typeof(SoundCue))]
    public sealed class SoundCueEditor : UnityEditor.Editor {
        private static MethodInfo _playClip;
        private static MethodInfo _stopAllClips;

        public override void OnInspectorGUI() {
            serializedObject.Update();

            var idProp = serializedObject.FindProperty("id");
            var variantsProp = serializedObject.FindProperty("variants");
            var busProp = serializedObject.FindProperty("bus");
            var outputGroupProp = serializedObject.FindProperty("outputGroup");
            var is3dProp = serializedObject.FindProperty("is3D");
            var spatialBlendProp = serializedObject.FindProperty("spatialBlend");
            var minDistanceProp = serializedObject.FindProperty("minDistance");
            var maxDistanceProp = serializedObject.FindProperty("maxDistance");
            var rolloffModeProp = serializedObject.FindProperty("rolloffMode");
            var priorityProp = serializedObject.FindProperty("priority");
            var maxInstancesProp = serializedObject.FindProperty("maxInstances");
            var cooldownSecondsProp = serializedObject.FindProperty("cooldownSeconds");
            var stealPolicyProp = serializedObject.FindProperty("stealPolicy");
            var stopBehaviorProp = serializedObject.FindProperty("stopBehavior");
            var preloadProp = serializedObject.FindProperty("preload");

            if(idProp == null ||
               variantsProp == null ||
               busProp == null ||
               outputGroupProp == null ||
               is3dProp == null ||
               spatialBlendProp == null ||
               minDistanceProp == null ||
               maxDistanceProp == null ||
               rolloffModeProp == null ||
               priorityProp == null ||
               maxInstancesProp == null ||
               cooldownSecondsProp == null ||
               stealPolicyProp == null ||
               stopBehaviorProp == null ||
               preloadProp == null) {
                EditorGUILayout.HelpBox("[AudioService] SoundCue inspector is out of sync with SoundCue fields. Falling back to default inspector.", MessageType.Warning);
                DrawDefaultInspector();
                return;
            }

            EditorGUILayout.PropertyField(idProp);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(variantsProp, includeChildren: true);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(busProp);
            EditorGUILayout.PropertyField(outputGroupProp);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(is3dProp);
            if(is3dProp.boolValue) {
                EditorGUILayout.PropertyField(spatialBlendProp);
                EditorGUILayout.PropertyField(minDistanceProp);
                EditorGUILayout.PropertyField(maxDistanceProp);
                EditorGUILayout.PropertyField(rolloffModeProp);
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(priorityProp);
            EditorGUILayout.PropertyField(maxInstancesProp);
            EditorGUILayout.PropertyField(cooldownSecondsProp);
            EditorGUILayout.PropertyField(stealPolicyProp);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(stopBehaviorProp);

            EditorGUILayout.Space(6);
            EditorGUILayout.PropertyField(preloadProp);

            serializedObject.ApplyModifiedProperties();

            var cue = (SoundCue)target;
            if(cue == null) return;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("AudioService Tools", EditorStyles.boldLabel);

            using(new EditorGUILayout.HorizontalScope()) {
                if(GUILayout.Button("Normalize Weights")) {
                    NormalizeWeights(cue);
                }

                if(GUILayout.Button("Stop Preview")) {
                    StopPreview();
                }
            }

            if(cue.variants != null && cue.variants.Count > 0) {
                EditorGUILayout.Space(4);
                for(var i = 0; i < cue.variants.Count; i++) {
                    var v = cue.variants[i];
                    using(new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField($"{i}", GUILayout.Width(18));
                        EditorGUILayout.ObjectField(v.clip, typeof(AudioClip), allowSceneObjects: false);
                        if(GUILayout.Button("Preview", GUILayout.Width(70))) {
                            PreviewClip(v.clip);
                        }
                    }
                }
            }
        }

        private static void NormalizeWeights(SoundCue cue) {
            if(cue == null || cue.variants == null || cue.variants.Count == 0) return;

            var total = 0f;
            for(var i = 0; i < cue.variants.Count; i++) {
                var v = cue.variants[i];
                if(v.clip == null) continue;
                if(v.weight <= 0f) continue;
                total += v.weight;
            }

            if(!(total > 0f)) return;

            Undo.RecordObject(cue, "Normalize SoundCue Weights");
            for(var i = 0; i < cue.variants.Count; i++) {
                var v = cue.variants[i];
                if(v.clip == null) continue;
                if(v.weight <= 0f) continue;
                v.weight /= total;
                cue.variants[i] = v;
            }
            EditorUtility.SetDirty(cue);
        }

        private static void EnsureAudioUtil() {
            if(_playClip != null && _stopAllClips != null) return;

            var audioUtil = Type.GetType("UnityEditor.AudioUtil,UnityEditor");
            if(audioUtil == null) return;

            // Newer Unity: PlayPreviewClip(AudioClip, int, bool)
            _playClip = audioUtil.GetMethod("PlayPreviewClip", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);

            // Older Unity: PlayClip(AudioClip)
            if(_playClip == null) {
                _playClip = audioUtil.GetMethod("PlayClip", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(AudioClip) }, null);
            }

            _stopAllClips = audioUtil.GetMethod("StopAllPreviewClips", BindingFlags.Public | BindingFlags.Static);
            if(_stopAllClips == null) {
                _stopAllClips = audioUtil.GetMethod("StopAllClips", BindingFlags.Public | BindingFlags.Static);
            }
        }

        private static void PreviewClip(AudioClip clip) {
            if(clip == null) return;
            EnsureAudioUtil();
            if(_playClip == null) return;

            try {
                var parms = _playClip.GetParameters();
                if(parms.Length == 3) {
                    _playClip.Invoke(null, new object[] { clip, 0, false });
                } else {
                    _playClip.Invoke(null, new object[] { clip });
                }
            } catch {
                // Ignore preview failures; editor-only helper.
            }
        }

        private static void StopPreview() {
            EnsureAudioUtil();
            if(_stopAllClips == null) return;
            try {
                _stopAllClips.Invoke(null, null);
            } catch {
            }
        }
    }
}
#endif

