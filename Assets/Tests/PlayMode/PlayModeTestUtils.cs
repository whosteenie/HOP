using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Tests.PlayMode {
    internal static class PlayModeTestUtils {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        internal readonly struct BehaviourToggleState {
            public readonly Behaviour Behaviour;
            public readonly bool WasEnabled;

            public BehaviourToggleState(Behaviour behaviour, bool wasEnabled) {
                Behaviour = behaviour;
                WasEnabled = wasEnabled;
            }
        }

        public static void DestroyImmediateIfExists(ref GameObject obj) {
            if(obj == null) {
                return;
            }

            UnityEngine.Object.DestroyImmediate(obj);
            obj = null;
        }

        public static Type ResolveTypeOrAssert(string assemblyQualifiedName) {
            var resolved = Type.GetType(assemblyQualifiedName);
            Assert.That(resolved, Is.Not.Null, $"Could not resolve type '{assemblyQualifiedName}'.");
            return resolved;
        }

        public static object InvokePrivate(object target, string methodName, params object[] args) {
            var method = target.GetType().GetMethod(methodName, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Expected private method '{methodName}'.");
            return method.Invoke(target, args);
        }

        public static T GetPrivateField<T>(object target, string fieldName) {
            var field = target.GetType().GetField(fieldName, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Expected private field '{fieldName}'.");
            return (T)field.GetValue(target);
        }

        public static void SetAutoPropertyBackingField(object target, string propertyName, object value) {
            var backingField = $"<{propertyName}>k__BackingField";
            var field = target.GetType().GetField(backingField, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Expected auto-property backing field '{backingField}'.");
            field.SetValue(target, value);
        }

        public static List<BehaviourToggleState> MuteSceneBehaviours(string assemblyQualifiedTypeName) {
            var muted = new List<BehaviourToggleState>();
            var behaviourType = Type.GetType(assemblyQualifiedTypeName);
            if(behaviourType == null) {
                return muted;
            }

            foreach(var obj in Resources.FindObjectsOfTypeAll(behaviourType)) {
                if(obj is not Behaviour behaviour || behaviour == null) {
                    continue;
                }

                if(!behaviour.gameObject.scene.IsValid()) {
                    continue;
                }

                muted.Add(new BehaviourToggleState(behaviour, behaviour.enabled));
                behaviour.enabled = false;
            }

            return muted;
        }

        public static void RestoreBehaviours(List<BehaviourToggleState> states) {
            if(states == null) {
                return;
            }

            foreach(var state in states) {
                if(state.Behaviour != null) {
                    state.Behaviour.enabled = state.WasEnabled;
                }
            }

            states.Clear();
        }
    }
}
