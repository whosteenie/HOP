using System;
using Game.Weapons.Kinemation;
using UnityEngine;

namespace Game.Player.Core {
    internal sealed class PlayerRuntimeSafety {
        private const string KinemationFpsCameraControllerTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Camera.FPSCameraController";
        private const string KinemationFpsCameraAnimationTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Camera.FPSCameraAnimation";
        private const string KinemationFpsCameraShakeTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Camera.FPSCameraShake";
        private const string KinemationFpsAnimatorTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimator";
        private const string KinemationFpsBoneControllerTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Core.FPSBoneController";
        private const string KinemationFpsPlayablesControllerTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Playables.FPSPlayablesController";
        private const string KinemationFpsAnimatorEntityTypeName =
            "KINEMATION.FPSAnimationFramework.Runtime.Core.FPSAnimatorEntity";
        private const string KinemationUserInputControllerTypeName =
            "KINEMATION.Shared.KAnimationCore.Runtime.Input.UserInputController";
        private const string KinemationProceduralRecoilTypeName =
            "KINEMATION.ProceduralRecoilAnimationSystem.Runtime.RecoilAnimation";

        private readonly PlayerController _player;
        private MonoBehaviour[] _cachedChildBehaviours = Array.Empty<MonoBehaviour>();
        private Camera[] _cachedChildCameras = Array.Empty<Camera>();
        private AudioListener[] _cachedChildAudioListeners = Array.Empty<AudioListener>();
        private bool _childComponentCachesDirty = true;

        public PlayerRuntimeSafety(PlayerController player) {
            _player = player;
        }

        public void MarkChildComponentCachesDirty() {
            _childComponentCachesDirty = true;
        }

        /// <summary>Refreshes child component caches if dirty.</summary>
        private void RefreshChildCachesIfNeeded() {
            if(!_childComponentCachesDirty) return;
            _cachedChildBehaviours = _player.GetComponentsInChildren<MonoBehaviour>(true);
            _cachedChildCameras = _player.GetComponentsInChildren<Camera>(true);
            _cachedChildAudioListeners = _player.GetComponentsInChildren<AudioListener>(true);
            _childComponentCachesDirty = false;
        }

        public void DisableConflictingKinemationComponents() {
            if(!_player.DisableKinemationFrameworkComponentsConfigured) return;

            RefreshChildCachesIfNeeded();
            foreach(var behaviour in _cachedChildBehaviours) {
                if(behaviour == null || !behaviour.enabled) continue;
                if(IsRuntimeKinemationFpViewmodelComponent(behaviour)) continue;

                var fullName = behaviour.GetType().FullName;
                if(string.IsNullOrEmpty(fullName) || !ShouldDisableKinemationComponent(fullName)) continue;

                behaviour.enabled = false;
                if(_player.LogKinemationFrameworkDisables) {
                    Debug.Log($"[PlayerController] Disabled conflicting KINEMATION framework component: {fullName}",
                        behaviour);
                }
            }
        }

        private bool ShouldDisableKinemationComponent(string fullTypeName) {
            var isCameraComponent = fullTypeName is KinemationFpsCameraControllerTypeName or
                KinemationFpsCameraAnimationTypeName or KinemationFpsCameraShakeTypeName;

            if(_player.DisableOnlyKinemationFrameworkCameraComponents) {
                return isCameraComponent;
            }

            if(isCameraComponent) return true;

            return fullTypeName is KinemationFpsAnimatorTypeName or
                KinemationFpsBoneControllerTypeName or
                KinemationFpsPlayablesControllerTypeName or
                KinemationFpsAnimatorEntityTypeName or
                KinemationUserInputControllerTypeName or
                KinemationProceduralRecoilTypeName;
        }

        public void DisableUnexpectedCamerasAndListeners() {
            if(!_player.DisableUnexpectedChildCamerasConfigured) return;

            RefreshChildCachesIfNeeded();
            var activeWeaponCamera = _player.WeaponCamera;
            if(activeWeaponCamera == null) {
                foreach(var candidate in _cachedChildCameras) {
                    if(candidate == null || candidate.gameObject.name != "WeaponCamera") continue;
                    activeWeaponCamera = candidate;
                    _player.AssignWeaponCamera(candidate);
                    break;
                }
            }

            foreach(var cameraComponent in _cachedChildCameras) {
                if(cameraComponent == null || !cameraComponent.enabled) continue;
                if(IsRuntimeKinemationFpViewmodelComponent(cameraComponent)) continue;
                if(activeWeaponCamera != null && cameraComponent == activeWeaponCamera) continue;

                cameraComponent.enabled = false;
                if(_player.LogKinemationFrameworkDisables) {
                    Debug.Log($"[PlayerController] Disabled unexpected child camera: {cameraComponent.name}",
                        cameraComponent);
                }
            }

            foreach(var listener in _cachedChildAudioListeners) {
                if(listener == null || !listener.enabled) continue;
                if(IsRuntimeKinemationFpViewmodelComponent(listener)) continue;
                if(_player.AudioListener != null && listener == _player.AudioListener) continue;

                listener.enabled = false;
                if(_player.LogKinemationFrameworkDisables) {
                    Debug.Log($"[PlayerController] Disabled unexpected child audio listener: {listener.name}", listener);
                }
            }
        }

        private static bool IsRuntimeKinemationFpViewmodelComponent(Component component) {
            if(component == null) return false;
            return component.GetComponentInParent<KinFpWeaponDriver>(true) != null;
        }
    }
}
