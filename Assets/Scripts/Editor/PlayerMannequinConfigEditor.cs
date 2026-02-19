using Game.Player;
using System.Collections.Generic;
using UnityEditor.Animations;
using UnityEditor;
using UnityEngine;

namespace Game.EditorTools {
    [CustomEditor(typeof(PlayerMannequinConfig))]
    public class PlayerMannequinConfigEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            DrawOrderedPropertiesWithInlineBaseLayerPopup();

            var mannequin = (PlayerMannequinConfig)target;

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Weapon Selection", EditorStyles.boldLabel);

            DrawWeaponPopup(
                mannequin,
                serializedObject.FindProperty("selectedPrimaryIndex"),
                mannequin.GetPrimaryOptionNames(),
                "Selected Primary");

            DrawWeaponPopup(
                mannequin,
                serializedObject.FindProperty("selectedSecondaryIndex"),
                mannequin.GetSecondaryOptionNames(),
                "Selected Secondary");

            serializedObject.ApplyModifiedProperties();

            if(GUI.changed) {
                mannequin.ApplyNow();
                EditorUtility.SetDirty(mannequin);
            }
        }

        private void DrawOrderedPropertiesWithInlineBaseLayerPopup() {
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;

            while(iterator.NextVisible(enterChildren)) {
                enterChildren = false;

                if(iterator.name == "m_Script") {
                    using(new EditorGUI.DisabledScope(true)) {
                        EditorGUILayout.PropertyField(iterator, true);
                    }
                    continue;
                }

                if(iterator.name == "selectedPrimaryIndex" ||
                   iterator.name == "selectedSecondaryIndex" ||
                   iterator.name == "baseLayerStateName" ||
                   iterator.name == "poseSourceMode" ||
                   iterator.name == "freezePose" ||
                   iterator.name == "overrideBaseLayerState") {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);

                if(iterator.name == "baseLayerNormalizedTime") {
                    DrawBaseLayerStatePopup();
                }
            }
        }

        private void DrawBaseLayerStatePopup() {
            var animatorProp = serializedObject.FindProperty("animator");
            var stateNameProp = serializedObject.FindProperty("baseLayerStateName");
            if(animatorProp == null || stateNameProp == null) return;

            var animator = animatorProp.objectReferenceValue as Animator;
            if(animator == null) return;

            var runtimeController = animator.runtimeAnimatorController;
            var controller = runtimeController as AnimatorController;
            if(controller == null || controller.layers == null || controller.layers.Length == 0) {
                EditorGUILayout.HelpBox("Assign an AnimatorController to use base layer state selection.", MessageType.Info);
                return;
            }

            var stateNames = new List<string>();
            CollectStateNames(controller.layers[0].stateMachine, stateNames);
            if(stateNames.Count == 0) {
                EditorGUILayout.HelpBox("No base layer states found in animator controller.", MessageType.Info);
                return;
            }

            var currentIndex = Mathf.Max(0, stateNames.IndexOf(stateNameProp.stringValue));
            var selectedIndex = EditorGUILayout.Popup("Base Layer State", currentIndex, stateNames.ToArray());
            stateNameProp.stringValue = stateNames[Mathf.Clamp(selectedIndex, 0, stateNames.Count - 1)];
        }

        private static void CollectStateNames(AnimatorStateMachine stateMachine, List<string> names) {
            if(stateMachine == null || names == null) return;

            foreach(var state in stateMachine.states) {
                if(state.state == null || string.IsNullOrWhiteSpace(state.state.name)) continue;
                names.Add(state.state.name);
            }

            foreach(var child in stateMachine.stateMachines) {
                CollectStateNames(child.stateMachine, names);
            }
        }

        private static void DrawWeaponPopup(PlayerMannequinConfig mannequin, SerializedProperty indexProperty, string[] names, string label) {
            if(indexProperty == null || names == null || names.Length == 0) return;

            var clamped = names.Length == 1 && names[0].StartsWith("No ") ? 0 : indexProperty.intValue;
            clamped = Mathf.Clamp(clamped, 0, names.Length - 1);
            indexProperty.intValue = EditorGUILayout.Popup(label, clamped, names);
        }
    }
}
