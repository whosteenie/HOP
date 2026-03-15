using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Weapon.Core;
using KINEMATION.FPSAnimationPack.Scripts.Weapon;
using UnityEditor;
using UnityEngine;

namespace Editor {
    [CustomEditor(typeof(WeaponData))]
    public class WeaponDataEditor : UnityEditor.Editor {
        public override void OnInspectorGUI() {
            serializedObject.Update();

            var useDamageFalloffProp = serializedObject.FindProperty("useDamageFalloff");
            var usePelletSpreadProp = serializedObject.FindProperty("usePelletSpread");
            var kinemationSpecialHandlingProp = serializedObject.FindProperty("kinemationSpecialHandling");
            var kinemationGrappleWeaponIndexProp = serializedObject.FindProperty("kinemationGrappleWeaponIndex");
            var kinemationReloadEventSoundIndicesProp =
                serializedObject.FindProperty("kinemationReloadEventSoundIndices");
            var iterator = serializedObject.GetIterator();
            var enterChildren = true;

            while(iterator.NextVisible(enterChildren)) {
                enterChildren = false;

                switch(iterator.name) {
                    case "m_Script": {
                        using(new EditorGUI.DisabledScope(true)) {
                            EditorGUILayout.PropertyField(iterator, true);
                        }
                        continue;
                    }
                    case "maxDamageRange" or "minDamageRange" or "minDamage" when
                        useDamageFalloffProp is { boolValue: false }:
                    case "pelletCount" or "pelletDamageMultiplier" when
                        usePelletSpreadProp is { boolValue: false }:
                    case "kinemationReloadEventSoundIndices":
                        continue;
                    default:
                        EditorGUILayout.PropertyField(iterator, true);
                        break;
                }
            }

            DrawReloadEventSoundIndicesEditor(kinemationReloadEventSoundIndicesProp);

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

            if(kinemationReloadEventSoundIndicesProp is { isArray: true, arraySize: 0 }) {
                EditorGUILayout.HelpBox(
                    "KINEMATION Reload Event Sound Indices are required. Empty list disables strict reload SFX stop matching.",
                    MessageType.Error);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private static bool IsEnumSetToNull(SerializedProperty property) {
            if(property is not { propertyType: SerializedPropertyType.Enum }) {
                return false;
            }

            var index = property.enumValueIndex;
            if(index < 0 || index >= property.enumNames.Length) {
                return false;
            }

            return property.enumNames[index] == "Null";
        }

        private void DrawReloadEventSoundIndicesEditor(SerializedProperty indicesProp) {
            if(indicesProp is not { isArray: true }) return;

            var data = target as WeaponData;
            if(data == null) return;

            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("KINEMATION Reload Event Sounds", EditorStyles.boldLabel);

            if(!TryResolveKinemationProfile(data.kinemationGrappleWeaponIndex, out var settingsPath,
                   out var reloadAnimationsFolder, out var profileLabel)) {
                EditorGUILayout.HelpBox(
                    "Unable to resolve KINEMATION profile from Grapple Weapon Index. Assign the index first.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(indicesProp, true);
                return;
            }

            var settings = AssetDatabase.LoadAssetAtPath<FPSWeaponSettings>(settingsPath);
            var eventSounds = settings != null ? settings.weaponEventSounds : null;
            var eventSoundCount = eventSounds != null ? eventSounds.Count : 0;

            if(settings == null) {
                EditorGUILayout.HelpBox(
                    $"Could not load FPSWeaponSettings at '{settingsPath}'. Falling back to raw index array.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(indicesProp, true);
                return;
            }

            var suggestedIndices = CollectReloadSoundIndices(reloadAnimationsFolder);
            if(suggestedIndices.Count > 0 && eventSoundCount > 0) {
                suggestedIndices.RemoveWhere(index => index >= eventSoundCount);
            }

            EditorGUILayout.HelpBox(
                $"Profile: {profileLabel}\n" +
                $"Settings: {settings.name}\n" +
                $"Reload animation folder: {reloadAnimationsFolder}",
                MessageType.None);

            if(eventSoundCount == 0) {
                EditorGUILayout.HelpBox(
                    "No weapon event sounds found in FPSWeaponSettings. Use raw index array only if intentional.",
                    MessageType.Warning);
                EditorGUILayout.PropertyField(indicesProp, true);
                return;
            }

            var selectedIndices = ReadIndexSet(indicesProp);
            var changed = false;
            for(var index = 0; index < eventSoundCount; index++) {
                if(eventSounds == null) continue;
                var clip = eventSounds[index];
                var clipName = clip != null ? clip.name : "(missing clip)";
                var isSuggested = suggestedIndices.Contains(index);
                var label = isSuggested
                    ? $"{index} - {clipName}  [Reload Event]"
                    : $"{index} - {clipName}";
                var isSelected = selectedIndices.Contains(index);
                var next = EditorGUILayout.ToggleLeft(label, isSelected);
                if(next == isSelected) continue;

                changed = true;
                if(next) {
                    selectedIndices.Add(index);
                } else {
                    selectedIndices.Remove(index);
                }
            }

            using(new EditorGUILayout.HorizontalScope()) {
                if(GUILayout.Button("Sync From KIN Reload Anim Events")) {
                    selectedIndices = new HashSet<int>(suggestedIndices);
                    changed = true;
                }

                if(GUILayout.Button("Clear")) {
                    selectedIndices.Clear();
                    changed = true;
                }
            }

            if(changed) {
                SetIndexSet(indicesProp, selectedIndices);
            }

            if(suggestedIndices.Count > 0) {
                var ordered = suggestedIndices.OrderBy(value => value);
                EditorGUILayout.HelpBox(
                    $"Suggested from reload clips: [{string.Join(", ", ordered)}]",
                    MessageType.Info);
            } else {
                EditorGUILayout.HelpBox(
                    "No reload PlayWeaponSound events detected in KIN animation metadata for this profile.",
                    MessageType.Warning);
            }
        }

        private static HashSet<int> ReadIndexSet(SerializedProperty indicesProp) {
            var set = new HashSet<int>();
            if(indicesProp is not { isArray: true }) return set;

            for(var i = 0; i < indicesProp.arraySize; i++) {
                var entry = indicesProp.GetArrayElementAtIndex(i);
                if(entry == null) continue;
                if(entry.propertyType != SerializedPropertyType.Integer) continue;
                if(entry.intValue < 0) continue;
                set.Add(entry.intValue);
            }

            return set;
        }

        private static void SetIndexSet(SerializedProperty indicesProp, IEnumerable<int> indices) {
            if(indicesProp is not { isArray: true }) return;

            var ordered = indices == null
                ? Array.Empty<int>()
                : indices.Where(index => index >= 0).Distinct().OrderBy(index => index).ToArray();

            indicesProp.arraySize = ordered.Length;
            for(var i = 0; i < ordered.Length; i++) {
                indicesProp.GetArrayElementAtIndex(i).intValue = ordered[i];
            }
        }

        private static HashSet<int> CollectReloadSoundIndices(string reloadAnimationsFolder) {
            var indices = new HashSet<int>();
            if(string.IsNullOrWhiteSpace(reloadAnimationsFolder)) return indices;
            if(!AssetDatabase.IsValidFolder(reloadAnimationsFolder)) return indices;

            var absoluteFolderPath = Path.GetFullPath(reloadAnimationsFolder);
            if(!Directory.Exists(absoluteFolderPath)) return indices;

            var metaFiles = Directory.GetFiles(absoluteFolderPath, "*.FBX.meta", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0);

            foreach(var filePath in metaFiles) {
                var lines = File.ReadAllLines(filePath);
                for(var i = 0; i < lines.Length; i++) {
                    if(lines[i].IndexOf("functionName: PlayWeaponSound", StringComparison.Ordinal) < 0) continue;
                    var parsed = TryParseNearbyIntParameter(lines, i + 1, i + 12);
                    if(parsed >= 0) {
                        indices.Add(parsed);
                    }
                }
            }

            return indices;
        }

        private static int TryParseNearbyIntParameter(string[] lines, int startInclusive, int endInclusive) {
            if(lines == null || lines.Length == 0) return -1;
            var start = Mathf.Clamp(startInclusive, 0, lines.Length - 1);
            var end = Mathf.Clamp(endInclusive, 0, lines.Length - 1);
            if(end < start) return -1;

            for(var i = start; i <= end; i++) {
                var line = lines[i];
                const string marker = "intParameter:";
                var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
                if(markerIndex < 0) continue;
                var raw = line[(markerIndex + marker.Length)..].Trim();
                if(int.TryParse(raw, out var parsed)) {
                    return parsed;
                }
            }

            return -1;
        }

        private static bool TryResolveKinemationProfile(WeaponData.KinemationGrappleWeaponIndex index,
            out string settingsAssetPath, out string reloadAnimationsFolder, out string profileLabel) {
            settingsAssetPath = string.Empty;
            reloadAnimationsFolder = string.Empty;
            profileLabel = string.Empty;

            const string settingsRoot = "Assets/Imported/KINEMATION/FPSAnimationPack/Settings/Weapons";
            const string animationsRoot = "Assets/Imported/KINEMATION/FPSAnimationPack/Animations";

            switch(index) {
                case WeaponData.KinemationGrappleWeaponIndex.Ak:
                    profileLabel = "AK";
                    settingsAssetPath = $"{settingsRoot}/AK_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/AK/Weapon";
                    return true;
                case WeaponData.KinemationGrappleWeaponIndex.M1911:
                    profileLabel = "M1911";
                    settingsAssetPath = $"{settingsRoot}/M1911_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/M1911/Weapon";
                    return true;
                case WeaponData.KinemationGrappleWeaponIndex.Pdw:
                    profileLabel = "PDW90";
                    settingsAssetPath = $"{settingsRoot}/PDW90_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/PDW90/Weapon";
                    return true;
                case WeaponData.KinemationGrappleWeaponIndex.Kar:
                    profileLabel = "Kar98K";
                    settingsAssetPath = $"{settingsRoot}/Kar98k_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/Kar98K/Weapon";
                    return true;
                case WeaponData.KinemationGrappleWeaponIndex.Drake:
                    profileLabel = "Drake-12";
                    settingsAssetPath = $"{settingsRoot}/Drake-12_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/Drake-12/Weapon";
                    return true;
                case WeaponData.KinemationGrappleWeaponIndex.Dgl:
                    profileLabel = "DGL50";
                    settingsAssetPath = $"{settingsRoot}/DGL50_Settings.asset";
                    reloadAnimationsFolder = $"{animationsRoot}/DGL50/Weapon";
                    return true;
                default:
                    return false;
            }
        }
    }
}
