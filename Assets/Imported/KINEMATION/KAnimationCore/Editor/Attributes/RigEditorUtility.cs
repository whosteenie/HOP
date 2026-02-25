// Designed by KINEMATION, 2024.

using KINEMATION.KAnimationCore.Runtime.Attributes;
using KINEMATION.KAnimationCore.Runtime.Rig;

using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace KINEMATION.KAnimationCore.Editor.Attributes
{
    public class RigEditorUtility
    {
        public static IRigProvider TryGetRigProvider(FieldInfo fieldInfo, SerializedProperty property)
        {
            IRigProvider provider = null;

            RigAssetSelectorAttribute assetAttribute = null;
            foreach (var customAttribute in fieldInfo.GetCustomAttributes(false))
            {
                if (customAttribute is RigAssetSelectorAttribute)
                {
                    assetAttribute = customAttribute as RigAssetSelectorAttribute;
                }
            }
            
            if (assetAttribute != null && !string.IsNullOrEmpty(assetAttribute.assetName))
            {
                if (property.serializedObject.FindProperty(assetAttribute.assetName) is var prop)
                {
                    provider = prop.objectReferenceValue as IRigProvider;
                }
            }

            if (provider == null)
            {
                provider = property.serializedObject.targetObject as IRigProvider;
            }

            if (provider == null && property.serializedObject.targetObject is MonoBehaviour component)
            {
                provider = component.GetComponentInChildren<IRigProvider>();
            }

            return provider;
        }
    }
}