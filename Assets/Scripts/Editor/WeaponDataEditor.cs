using Game.Weapons;
using UnityEditor;

namespace Game.EditorTools {
    [CustomEditor(typeof(WeaponData))]
    public class WeaponDataEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            var useMagReloadProp = serializedObject.FindProperty("useMagReload");
            var useDamageFalloffProp = serializedObject.FindProperty("useDamageFalloff");
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

                if(iterator.name == "perRoundReloadTime" &&
                   useMagReloadProp != null &&
                   useMagReloadProp.boolValue) {
                    continue;
                }

                if((iterator.name == "maxDamageRange" ||
                    iterator.name == "minDamageRange" ||
                    iterator.name == "minDamage") &&
                   useDamageFalloffProp != null &&
                   !useDamageFalloffProp.boolValue) {
                    continue;
                }

                EditorGUILayout.PropertyField(iterator, true);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
