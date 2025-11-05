using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class DeathCamera : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private Transform bodyTransform; // The HumanDummy or root of ragdoll
    
    [Header("Death Camera Settings")]
    [SerializeField] private float orbitDistance = 3f;
    [SerializeField] private float orbitHeight = 1.5f;
    [SerializeField] private float orbitSpeed = 100f;
    [SerializeField] private float minPitch = -90f;
    [SerializeField] private float maxPitch = 90f;
    
    [Header("Transition")]
    [SerializeField] private float transitionSpeed = 5f;
    
    private bool _isDeathCam;
    private Vector2 _orbitAngles; // x = yaw, y = pitch
    private Vector3 _targetPosition;
    private HUDManager _hudManager;
    
    // Store original FP camera values
    private Vector3 _originalLocalPos;
    private Quaternion _originalLocalRot;
    
    public Vector2 lookInput;

    private void Start() {
        if(!IsOwner) return;
        
        _originalLocalPos = fpCamera.transform.localPosition;
        _originalLocalRot = fpCamera.transform.localRotation;
        
        _hudManager = FindFirstObjectByType<HUDManager>();
    }

    public void EnableDeathCamera() {
        if(!IsOwner) return;
        
        _isDeathCam = true;

        _orbitAngles = new Vector2(transform.eulerAngles.y, fpCamera.transform.localEulerAngles.x);
        
        fpCamera.transform.SetParent(null, true);
        foreach(Transform weapon in fpCamera.transform) {
            weapon.gameObject.SetActive(false);
        }
        
        var bodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        bodyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        // gameObject.layer = LayerMask.NameToLayer("Default");
        // bodyTransform.gameObject.layer = LayerMask.NameToLayer("Default");
        // foreach(Transform body in bodyTransform) {
        //     body.gameObject.layer = LayerMask.NameToLayer("Default");
        // }
        _hudManager.HideHUD();
    }

    public void DisableDeathCamera() {
        if(!IsOwner) return;
        
        _isDeathCam = false;
        
        // Re-attach camera to player
        fpCamera.transform.SetParent(transform, true);
        fpCamera.transform.localPosition = _originalLocalPos;
        fpCamera.transform.localRotation = _originalLocalRot;
        
        fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(true);
        var bodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
        bodyRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        // gameObject.layer = LayerMask.NameToLayer("Player");
        // bodyTransform.gameObject.layer = LayerMask.NameToLayer("Player");
        // foreach(Transform body in bodyTransform) {
        //     body.gameObject.layer = LayerMask.NameToLayer("Player");
        // }
        _hudManager.ShowHUD();
    }

    public void LateUpdate() {
        if(!IsOwner || !_isDeathCam) return;
        
        UpdateDeathCamera();
    }

    private void UpdateDeathCamera() {
        _orbitAngles.x += lookInput.x * orbitSpeed * Time.deltaTime;
        _orbitAngles.y -= lookInput.y * orbitSpeed * Time.deltaTime;
        _orbitAngles.y = Mathf.Clamp(_orbitAngles.y, minPitch, maxPitch);
        
        var bodyCenter = bodyTransform.position + Vector3.up * orbitHeight;
        
        var rotation = Quaternion.Euler(_orbitAngles.y, _orbitAngles.x, 0);
        var desiredPosition = bodyCenter - (rotation * Vector3.forward * orbitDistance);
        
        fpCamera.transform.position = Vector3.Lerp(fpCamera.transform.position, desiredPosition, transitionSpeed * Time.deltaTime);
        
        fpCamera.transform.LookAt(bodyCenter);
    }
}
