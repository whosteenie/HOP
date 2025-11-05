using System.Collections;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.VFX;

public class Weapon : NetworkBehaviour
{
    [Header("Weapon Data")]
    public string weaponName;
    public GameObject weaponPrefab;
    public GameObject weaponMuzzle;
    public int currentAmmo;
    public int magSize;
    public int baseDamage;
    public float fireRate;
    public float bulletSpread;
    public float maxDamageMultiplier;
    public float damageCap;
    public float reloadTime;
    public string fireMode;
    public AudioClip[] fireSounds;
    public AudioClip[] reloadSounds;
    public AudioClip[] dryFireSounds;
    public TrailRenderer bulletTrail;
    public ParticleSystem bulletImpact;
    
    [Header("Speed Damage Scaling")]
    [SerializeField] private float minSpeedThreshold;
    [SerializeField] private float maxSpeedThreshold = 30f;
    [SerializeField] private float multiplierDecayRate = 1.8f;
    [SerializeField] private float multiplierGracePeriod = 1.25f;

    [Header("Components")]
    public CinemachineCamera fpCamera;
    public FpController fpController;
    public WeaponManager weaponManager;
    public HUDManager hudManager;
    public LayerMask playerBodyLayer;
    
    [Header("Weapon References")]
    [SerializeField] private Animator weaponAnimator;
    [SerializeField] private VisualEffect muzzleFlashEffect;
    [SerializeField] private GameObject muzzleLight;
    [SerializeField] private float bulletSpeed = 100f;

    #region Private Fields
    
    private float _lastFireTime;
    private float _peakDamageMultiplier = 1f;
    private float _lastPeakTime;
    private float _graceEndTime;
    private Coroutine _reloadCoroutine;

    #endregion
    
    #region Properties

    public bool IsReloading { get; private set; }
    public float CurrentDamageMultiplier { get; private set; } = 1f;

    #endregion

    #region Animation Hashes
    
    private static readonly int RecoilHash = Animator.StringToHash("Recoil");
    private static readonly int ReloadHash = Animator.StringToHash("Reload");
    
    #endregion
    
    #region Unity Lifecycle
    private void OnValidate() {
        if(weaponManager == null) {
            weaponManager = GetComponent<WeaponManager>();
        }

        if(fpCamera == null) {
            fpCamera = GetComponentInChildren<CinemachineCamera>();
        }

        if(fpController == null) {
            fpController = GetComponent<FpController>();
        }

        if(hudManager == null) {
            hudManager = FindFirstObjectByType<HUDManager>();
        }
    }

    private void Awake() {
        playerBodyLayer = LayerMask.GetMask("Masked");
        _lastFireTime = Time.time;
        minSpeedThreshold = FpController.SprintSpeed;
    }

    public void BindAndResolve(CinemachineCamera cam, FpController controller, WeaponManager mgr, HUDManager hud) {
        fpCamera = cam;
        fpController = controller;
        weaponManager = mgr;
        hudManager = hud;
        
        weaponAnimator = weaponPrefab.GetComponent<Animator>();
        muzzleFlashEffect = weaponMuzzle.GetComponent<VisualEffect>();

        muzzleLight = weaponMuzzle.transform.GetChild(0).gameObject;
        if(muzzleLight != null) {
            muzzleLight.SetActive(false);
        }
    }
    
    #endregion
    
    #region Initialization
    
    public void Initialize(WeaponData data) {
        weaponName = data.weaponName;
        weaponPrefab = data.weaponPrefab;
        weaponMuzzle = data.muzzlePrefab;
        
        magSize = data.magSize;
        currentAmmo = data.currentAmmo;
        
        baseDamage = data.baseDamage;
        maxDamageMultiplier = data.maxDamageMultiplier;
        damageCap = data.damageCap;
        
        fireRate = data.fireRate;
        fireMode = data.fireMode;
        bulletSpread = data.bulletSpread;
        
        reloadTime = data.reloadTime;
        
        fireSounds = data.fireSounds;
        dryFireSounds = data.dryFireSounds;
        reloadSounds = data.reloadSounds;
        
        bulletTrail = data.bulletTrail;
        bulletImpact = data.bulletImpact;
    }
    
    #endregion
    
    #region Public Methods

    public void Shoot() {
        if(!CanFire()) {
            HandleCannotFire();
            return;
        }

        PerformShot();
        PlayFireEffects();
    }

    public void StartReload() {
        if(!CanReload()) {
            Debug.Log($"Cannot reload! - Ammo: {currentAmmo}/{magSize}");
            return;
        }
        
        IsReloading = true;
        weaponAnimator.SetTrigger(ReloadHash);
        _reloadCoroutine = StartCoroutine(ReloadCoroutine());
    }

    private IEnumerator ReloadCoroutine() {
        PlayReloadEffects();
        
        yield return new WaitForSeconds(reloadTime);
        
        CompleteReload();
    }
    
    public void CancelReload() {
        if(!IsReloading) return;
        
        IsReloading = false;
        _reloadCoroutine = null;
    }

    #endregion
    
    #region Private Methods - Shooting
    
    private bool CanFire() {
        return Time.time >= _lastFireTime + fireRate && currentAmmo > 0 && !IsReloading;
    }

    private void HandleCannotFire() {
        if(Time.time < _lastFireTime + fireRate || _reloadCoroutine != null || currentAmmo != 0 || IsReloading) return;
        
        _lastFireTime = Time.time;
        PlayDryFireSound();
    }
    
    private void PerformShot() {
        var origin = fpCamera.transform.position;
        var forward = fpCamera.transform.forward;
        var shotHit = Physics.Raycast(origin, forward, out var hit, Mathf.Infinity, ~playerBodyLayer);
        
        var damage = GetScaledDamage();

        if(shotHit) {
            Debug.DrawRay(origin, forward * hit.distance, Color.green, 5f);
            
            var trail = Instantiate(bulletTrail, weaponMuzzle.transform.position, Quaternion.identity);
            StartCoroutine(SpawnTrail(trail, hit.point, hit.normal, true));
        } else {
            Debug.DrawRay(origin, forward * 500f, Color.red, 5f);
            
            var trailEndPoint = weaponMuzzle.transform.position + forward * 100f;

            var trail = Instantiate(bulletTrail, weaponMuzzle.transform.position, Quaternion.LookRotation(forward));
            StartCoroutine(SpawnTrail(trail, trailEndPoint, Vector3.zero, false));
        }
        
        currentAmmo--;
        _lastFireTime = Time.time;
        hudManager.UpdateAmmo(currentAmmo, magSize);
        
        Debug.Log($"{weaponName} fired! - Damage: {damage:F1} | Ammo: {currentAmmo}/{magSize} | Hit: {(shotHit ? "Yes" : "No")}");
    }
    
    private float GetScaledDamage() {
        return Mathf.Min(baseDamage * CurrentDamageMultiplier, damageCap);
    }
    
    public void UpdateDamageMultiplier() {
        var currentSpeed = fpController.CurrentFullVelocity.magnitude;
        float targetMultiplier;
        
        // Calculate target multiplier based on current velocity
        if(currentSpeed < minSpeedThreshold) {
            targetMultiplier = 1f;
        } else {
            var scaleFactor = Mathf.InverseLerp(minSpeedThreshold, maxSpeedThreshold, currentSpeed);
            targetMultiplier = Mathf.Lerp(1f, maxDamageMultiplier, scaleFactor);
        }
        
        // If target is higher than current, jump to it immediately and start grace period
        if(targetMultiplier >= CurrentDamageMultiplier) {
            CurrentDamageMultiplier = targetMultiplier;
            _peakDamageMultiplier = CurrentDamageMultiplier;
            _lastPeakTime = Time.time;
        }
        // During grace period, hold at peak
        else if(Time.time - _lastPeakTime < multiplierGracePeriod) {
            CurrentDamageMultiplier = _peakDamageMultiplier;
        }
        // After grace period, decay
        else {
            // Simple decay towards target
            CurrentDamageMultiplier = Mathf.MoveTowards(CurrentDamageMultiplier, targetMultiplier, multiplierDecayRate * Time.deltaTime);
        }
        
        CurrentDamageMultiplier = Mathf.Clamp(CurrentDamageMultiplier, 1f, maxDamageMultiplier);
    }
    
    #endregion
    
    #region Private Methods - Reloading
    
    private bool CanReload() {
        return currentAmmo < magSize;
    }

    private void CompleteReload() {
        currentAmmo = magSize;
        IsReloading = false;
        
        hudManager.UpdateAmmo(currentAmmo, magSize);
        _reloadCoroutine = null;
        Debug.Log($"{weaponName} reloaded! - Ammo: {currentAmmo}/{magSize}");
    }
    
    #endregion
    
    #region Private Methods - Effects
    
    private void PlayFireEffects() {
        if(weaponAnimator) {
            weaponAnimator.SetTrigger("Recoil");
        }
        
        SoundFXManager.Instance.PlayRandomSoundFX(fireSounds, transform);
        muzzleFlashEffect.Play();
        muzzleLight.gameObject.SetActive(true);
    }
    
    private void PlayDryFireSound() {
        SoundFXManager.Instance.PlayRandomSoundFX(dryFireSounds, transform);
    }

    private void PlayReloadEffects() {
        if(weaponAnimator) {
            weaponAnimator.SetTrigger("Reload");
        }
        
        SoundFXManager.Instance.PlayRandomSoundFX(reloadSounds, transform);
    }
    
    private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact) {
        var startPosition = trail.transform.position;
        var distance = Vector3.Distance(trail.transform.position, hitPoint);
        var remainingDistance = distance;

        while (remainingDistance > 0) {
            trail.transform.position = Vector3.Lerp(startPosition, hitPoint, 1 - (remainingDistance / distance));

            remainingDistance -= bulletSpeed * Time.deltaTime;

            yield return null;
        }
        
        trail.transform.position = hitPoint;
        if (madeImpact) {
            // Instantiate(bulletImpact, hitPoint, Quaternion.LookRotation(hitNormal));
        }

        Destroy(trail.gameObject, trail.time);
    }
    
    #endregion
}
