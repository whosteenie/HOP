using System.Collections.Generic;
using Game.Player;
using Game.Weapons;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Network {
    /// <summary>
    /// Maintains duplicate FP visuals during unexpected disconnect so the player sees a seamless
    /// transition: host disconnects -> fade to black (with duplicate visible) -> screen black ->
    /// teardown/cleanup (hidden by black) -> main menu -> fade in.
    /// The duplicate survives NGO's despawn and is destroyed after we're fully black.
    /// </summary>
    public class DisconnectTransitionController : MonoBehaviour {
        public static DisconnectTransitionController Instance { get; private set; }

        private Camera _standbyOverlayCamera;
        private GameObject _duplicateFpVisualsRoot;
        private int _weaponLayer;
        private bool _isActive;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _weaponLayer = LayerMask.NameToLayer("Weapon");
            if(_weaponLayer < 0) _weaponLayer = LayerMask.NameToLayer("Default");
        }

        private void OnDestroy() {
            if(Instance == this) Instance = null;
            CleanupDuplicate();
        }

        /// <summary>
        /// Captures the current FP weapon visuals and shows a duplicate that survives player despawn.
        /// Call synchronously at disconnect, before any await.
        /// Returns true if duplicate was shown; false if fallback (hide) should be used instead.
        /// </summary>
        public bool CaptureAndShowDuplicateFpVisuals(PlayerController player) {
            if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] CaptureAndShowDuplicateFpVisuals called");
            if(player == null || !player.IsOwner) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: player null or not owner"); return false; }
            CleanupDuplicate();

            var weaponManager = player.WeaponManager;
            if(weaponManager == null) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: weaponManager null"); return false; }

            var holderRoot = weaponManager.GetCurrentFpWeaponHolderRootForDisconnectDuplicate();
            if(holderRoot == null) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: holderRoot null (no FP weapon?)"); return false; }

            var mainCamera = Camera.main;
            if(mainCamera == null) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: mainCamera null"); return false; }

            EnsureStandbyOverlayCamera(mainCamera);
            if(_standbyOverlayCamera == null) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: overlay null after Ensure"); return false; }

            var weaponCam = player.WeaponCamera;
            if(weaponCam == null) { if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] FAIL: weaponCam null"); return false; }

            ApplyWeaponCameraSettingsToOverlay(weaponCam);

            _duplicateFpVisualsRoot = Instantiate(holderRoot);
            _duplicateFpVisualsRoot.name = "DisconnectFpVisualsDuplicate";
            StripToVisualsOnly(_duplicateFpVisualsRoot);
            SetLayerRecursive(_duplicateFpVisualsRoot.transform, _weaponLayer);

            _duplicateFpVisualsRoot.transform.SetParent(_standbyOverlayCamera.transform, false);
            _duplicateFpVisualsRoot.transform.localPosition = holderRoot.transform.localPosition;
            _duplicateFpVisualsRoot.transform.localRotation = holderRoot.transform.localRotation;
            _duplicateFpVisualsRoot.transform.localScale = holderRoot.transform.localScale;
            _duplicateFpVisualsRoot.SetActive(true);

            if(!_isActive) {
                AddOverlayToStack(mainCamera, weaponCam);
                _isActive = true;
            }
            if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] OK: duplicate shown, overlay active");
            return true;
        }

        /// <summary>
        /// Destroys the duplicate and removes overlay. Call after screen is black (during LeaveToMainMenuAsync).
        /// Explicitly destroys the overlay so it can be recreated for the next disconnect in the same session.
        /// </summary>
        public void CleanupDuplicate() {
            if(_duplicateFpVisualsRoot != null) {
                Destroy(_duplicateFpVisualsRoot);
                _duplicateFpVisualsRoot = null;
            }

            if(_isActive && _standbyOverlayCamera != null) {
                RemoveOverlayFromStack();
                _isActive = false;
            }

            // Explicitly destroy overlay and clear reference so a fresh one is created on the next disconnect.
            if(_standbyOverlayCamera != null) {
                if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] Cleanup: destroying overlay, clearing ref");
                Destroy(_standbyOverlayCamera.gameObject);
                _standbyOverlayCamera = null;
            }
        }

        private void EnsureStandbyOverlayCamera(Camera mainCamera) {
            // Recreate if null or destroyed (e.g. scene unload from previous match)
            var hadValid = _standbyOverlayCamera != null && _standbyOverlayCamera;
            if(hadValid) {
                if(Debug.isDebugBuild) Debug.Log("[DisconnectTransition] EnsureOverlay: reusing existing");
                return;
            }
            _standbyOverlayCamera = null;
            if(Debug.isDebugBuild) Debug.Log($"[DisconnectTransition] EnsureOverlay: creating new (mainCam={mainCamera?.name})");

            var go = new GameObject("DisconnectStandbyOverlay");
            go.transform.SetParent(mainCamera.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            _standbyOverlayCamera = go.AddComponent<Camera>();
            _standbyOverlayCamera.clearFlags = CameraClearFlags.Nothing;
            _standbyOverlayCamera.cullingMask = 1 << _weaponLayer;
            _standbyOverlayCamera.depth = 100;
            _standbyOverlayCamera.rect = new Rect(0, 0, 1, 1);
            _standbyOverlayCamera.orthographic = false;
            _standbyOverlayCamera.enabled = false;

            var camData = _standbyOverlayCamera.GetUniversalAdditionalCameraData();
            if(camData != null) camData.renderType = CameraRenderType.Overlay;
        }

        private void ApplyWeaponCameraSettingsToOverlay(Camera weaponCamera) {
            if(_standbyOverlayCamera == null || weaponCamera == null) return;
            _standbyOverlayCamera.fieldOfView = weaponCamera.fieldOfView;
            _standbyOverlayCamera.nearClipPlane = weaponCamera.nearClipPlane;
            _standbyOverlayCamera.farClipPlane = weaponCamera.farClipPlane;
            _standbyOverlayCamera.cullingMask = weaponCamera.cullingMask;
            _standbyOverlayCamera.orthographic = weaponCamera.orthographic;

            var weaponData = weaponCamera.GetUniversalAdditionalCameraData();
            var overlayData = _standbyOverlayCamera.GetUniversalAdditionalCameraData();
            if(weaponData != null && overlayData != null) {
                overlayData.volumeLayerMask = weaponData.volumeLayerMask;
                overlayData.renderPostProcessing = weaponData.renderPostProcessing;
            }
        }

        private void AddOverlayToStack(Camera mainCamera, Camera playerWeaponCamera) {
            if(_standbyOverlayCamera == null || mainCamera == null) return;
            var data = mainCamera.GetUniversalAdditionalCameraData();
            if(data == null) return;
            if(playerWeaponCamera != null && data.cameraStack.Contains(playerWeaponCamera)) {
                data.cameraStack.Remove(playerWeaponCamera);
                playerWeaponCamera.enabled = false;
            }
            if(!data.cameraStack.Contains(_standbyOverlayCamera)) {
                data.cameraStack.Add(_standbyOverlayCamera);
            }
            _standbyOverlayCamera.enabled = true;
        }

        private void RemoveOverlayFromStack() {
            if(_standbyOverlayCamera == null) return;
            _standbyOverlayCamera.enabled = false;
            var mainCamera = Camera.main;
            if(mainCamera != null) {
                var data = mainCamera.GetUniversalAdditionalCameraData();
                data?.cameraStack.Remove(_standbyOverlayCamera);
            }
        }

        private static void StripToVisualsOnly(GameObject root) {
            var toDestroy = new List<Component>();
            foreach(var c in root.GetComponentsInChildren<Component>(true)) {
                if(c == null) continue;
                if(c is Transform) continue;
                if(c is Renderer or MeshFilter or SkinnedMeshRenderer or MeshRenderer) continue;
                if(c is ParticleSystem or ParticleSystemRenderer) continue;
                toDestroy.Add(c);
            }
            foreach(var c in toDestroy) {
                if(c != null) Destroy(c);
            }
        }

        private static void SetLayerRecursive(Transform t, int layer) {
            t.gameObject.layer = layer;
            for(var i = 0; i < t.childCount; i++) {
                SetLayerRecursive(t.GetChild(i), layer);
            }
        }
    }
}
