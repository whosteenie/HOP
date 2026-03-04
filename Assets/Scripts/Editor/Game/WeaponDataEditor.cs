using Game.Weapons;
using UnityEditor;

namespace Game.Editor {
    [CustomEditor(typeof(WeaponData))]
    public class WeaponDataEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            var useDamageFalloffProp = serializedObject.FindProperty("useDamageFalloff");
            var usePelletSpreadProp = serializedObject.FindProperty("usePelletSpread");
            var kinemationSpecialHandlingProp = serializedObject.FindProperty("kinemationSpecialHandling");
            var kinemationGrappleWeaponIndexProp = serializedObject.FindProperty("kinemationGrappleWeaponIndex");
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

                if((iterator.name == "maxDamageRange" ||
                    iterator.name == "minDamageRange" ||
                    iterator.name == "minDamage") &&
                   useDamageFalloffProp != null &&
                   !useDamageFalloffProp.boolValue) {
                    continue;
                }

                if((iterator.name == "pelletCount" ||
                    iterator.name == "pelletDamageMultiplier") &&
                   usePelletSpreadProp != null &&
                   !usePelletSpreadProp.boolValue) {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }

            if(IsEnumSetToNull(kinemationSpecialHandlingProp)) {
                EditorGUILayout.HelpBox(
                    "KINEMATION Special Handling is required. Set None if no special behavior is needed. " +
                    "Drake/Kar-specific handling will not run while this is NULL.",
                    MessageType.Error);
            }

            if(IsEnumSetToNull(kinemationGrappleWeaponIndexProp)) {
                EditorGUILayout.HelpBox(
                    "KINEMATION Grapple Weapon Index is required. Grapple animation will use default bucket 0 while NULL.",
                    MessageType.Error);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static bool IsEnumSetToNull(SerializedProperty property) {
            if(property == null || property.propertyType != SerializedPropertyType.Enum) {
                return false;
            }

            var index = property.enumValueIndex;
            if(index < 0 || index >= property.enumNames.Length) {
                return false;
            }

            return property.enumNames[index] == "Null";
        }
    }
}
