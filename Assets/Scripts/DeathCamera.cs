using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using System.Collections;

public class DeathCamera : NetworkBehaviour
{
    [Header("References")]
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private WeaponManager weaponManager;

    [Header("Death Camera Settings")]
    [SerializeField] private float orbitDistance = 3f;
    [SerializeField] private float orbitHeight = 1.5f;
    [SerializeField] private float orbitSpeed = 100f;
    [SerializeField] private float minPitch = -60f;
    [SerializeField] private float maxPitch = 60f;

    [Header("Transition")]
    [SerializeField] private float transitionSpeed = 5f;

    public Vector2 lookInput;

    private bool _isDeathCam;
    private Vector2 _orbitAngles; // x = yaw, y = pitch
    private Vector3 _originalLocalPos;
    private Quaternion _originalLocalRot;

    private Transform _bodyTransform;      // set at runtime
    private HUDManager _hudManager;
    private PlayerRagdoll _ragdoll;

    private void Awake()
    {
        _hudManager = HUDManager.Instance;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (!IsOwner) return;

        _ragdoll = GetComponent<PlayerRagdoll>();
        if (_ragdoll != null)
            _ragdoll.RagdollBecameActive += OnRagdollBecameActive;
    }

    private void OnDestroy()
    {
        if (_ragdoll != null)
            _ragdoll.RagdollBecameActive -= OnRagdollBecameActive;
    }

    private void Start()
    {
        if (!IsOwner) return;
        _originalLocalPos = fpCamera.transform.localPosition;
        _originalLocalRot = fpCamera.transform.localRotation;
    }

    private void OnRagdollBecameActive(Transform focus)
    {
        _bodyTransform = focus;
        if (_isDeathCam)
            SnapToTargetNow();
    }

    public void SetRagdollFocus(Transform t) // optional external setter
    {
        _bodyTransform = t;
    }

    public void EnableDeathCamera()
    {
        if (!IsOwner) return;
        StartCoroutine(EnableDeathCamCo());
    }

    private IEnumerator EnableDeathCamCo()
    {
        // Wait up to ~0.5s for a valid target if it isn’t ready yet
        float t = 0f;
        while (_bodyTransform == null && t < 0.5f)
        {
            // try to pull from ragdoll once more
            if (_ragdoll != null && _ragdoll.Focus != null)
                _bodyTransform = _ragdoll.Focus;

            t += Time.deltaTime;
            yield return null;
        }

        if (_bodyTransform == null)
        {
            // As a last resort, don’t switch (prevents jumping to 0,0,0)
            Debug.LogWarning("DeathCamera: No ragdoll focus found; aborting death cam enable.");
            yield break;
        }

        _isDeathCam = true;

        // Seed orbit angles from current facing
        _orbitAngles = new Vector2(transform.eulerAngles.y, fpCamera.transform.localEulerAngles.x);

        // Detach AFTER we have a target
        fpCamera.transform.SetParent(null, true);

        // Hide weapon models
        for (int i = 0; i < fpCamera.transform.childCount; i++)
            fpCamera.transform.GetChild(i).gameObject.SetActive(false);

        // First frame: snap to a clean orbit position & look
        SnapToTargetNow();

        _hudManager?.HideHUD();
    }

    public void DisableDeathCamera()
    {
        if (!IsOwner) return;

        _isDeathCam = false;

        // Re-attach camera to the player
        fpCamera.transform.SetParent(transform, true);
        fpCamera.transform.localPosition = _originalLocalPos;
        fpCamera.transform.localRotation = _originalLocalRot;

        // Re-enable current weapon viewmodel
        if (weaponManager != null &&
            weaponManager.currentWeaponIndex >= 0 &&
            weaponManager.currentWeaponIndex < fpCamera.transform.childCount)
        {
            fpCamera.transform.GetChild(weaponManager.currentWeaponIndex).gameObject.SetActive(true);
        }

        _hudManager?.ShowHUD();
    }

    private void LateUpdate()
    {
        if (!IsOwner || !_isDeathCam || _bodyTransform == null) return;
        UpdateDeathCamera();
    }

    private void UpdateDeathCamera()
    {
        _orbitAngles.x += lookInput.x * orbitSpeed * Time.deltaTime;
        _orbitAngles.y -= lookInput.y * orbitSpeed * Time.deltaTime;
        _orbitAngles.y = Mathf.Clamp(_orbitAngles.y, minPitch, maxPitch);

        Vector3 bodyCenter = _bodyTransform.position + Vector3.up * orbitHeight;
        Quaternion rot = Quaternion.Euler(_orbitAngles.y, _orbitAngles.x, 0f);
        Vector3 desiredPos = bodyCenter - (rot * Vector3.forward * orbitDistance);

        fpCamera.transform.position = Vector3.Lerp(
            fpCamera.transform.position, desiredPos, transitionSpeed * Time.deltaTime);

        fpCamera.transform.rotation = Quaternion.Slerp(
            fpCamera.transform.rotation,
            Quaternion.LookRotation(bodyCenter - fpCamera.transform.position, Vector3.up),
            transitionSpeed * Time.deltaTime);
    }

    private void SnapToTargetNow()
    {
        Vector3 bodyCenter = _bodyTransform.position + Vector3.up * orbitHeight;
        Quaternion rot = Quaternion.Euler(_orbitAngles.y, _orbitAngles.x, 0f);
        Vector3 pos = bodyCenter - (rot * Vector3.forward * orbitDistance);

        fpCamera.transform.position = pos;
        fpCamera.transform.rotation = Quaternion.LookRotation(bodyCenter - pos, Vector3.up);
    }
}