using Game.Weapons;
using UnityEditor;

namespace Game.EditorTools {
    [CustomEditor(typeof(WeaponData))]
    public class WeaponDataEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            var useDamageFalloffProp = serializedObject.FindProperty("useDamageFalloff");
            var usePelletSpreadProp = serializedObject.FindProperty("usePelletSpread");
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

            serializedObject.ApplyModifiedProperties();
        }
    }
}
