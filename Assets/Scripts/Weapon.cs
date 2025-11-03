using System.Collections;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.VFX;

public class Weapon : MonoBehaviour
{
    [Header("Weapon Data")]
    public string weaponName;
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
    
    [Header("Velocity Damage Scaling")]
    [SerializeField] private float minVelocityThreshold;
    [SerializeField] private float maxVelocityThreshold = 18f;
    
    [Header("Components")]
    public CinemachineCamera fpCamera;
    public FpController fpController;
    public WeaponManager weaponManager;
    public HUDManager hudManager;
    public LayerMask playerBodyLayer;
    
    [Header("Weapon References")]
    [SerializeField] private GameObject weaponModel;
    [SerializeField] private Animator weaponAnimator;
    [SerializeField] private GameObject weaponMuzzle;
    [SerializeField] private VisualEffect muzzleFlashEffect;
    [SerializeField] private GameObject muzzleLight;
    [SerializeField] private ParticleSystem impactParticleSystem;
    [SerializeField] private float bulletSpeed = 100f;

    #region Private Fields
    
    private float _lastFireTime;
    private float _reloadStartTime;

    #endregion
    
    #region Properties

    public bool IsReloading { get; private set; }

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
    }

    private void Awake() {
        playerBodyLayer = LayerMask.GetMask("Masked");
        _lastFireTime = Time.time;
        minVelocityThreshold = FpController.SprintSpeed;
        hudManager = FindFirstObjectByType<HUDManager>();
    }

    private void Start() {
        FindWeaponComponents();
    }

    private void Update() {
        if(IsReloading && Time.time >= _reloadStartTime + reloadTime) {
            CompleteReload();
        }
    }
    
    #endregion
    
    #region Initialization
    
    public void Initialize(WeaponData data) {
        weaponName = data.name;
        
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
    }

    private void FindWeaponComponents() {
        if(weaponModel == null) {
            weaponModel = fpCamera.transform.Find(weaponName)?.gameObject;
        }

        if(weaponModel == null) {
            Debug.LogWarning($"Weapon model '{weaponName}' not found under FpCamera!");
            return;
        }

        if(weaponAnimator == null) {
            weaponAnimator = weaponModel.GetComponent<Animator>();
        }

        if(weaponMuzzle == null) {
            weaponMuzzle = weaponModel.transform.Find("Muzzle")?.gameObject;
        }

        if(weaponMuzzle == null) {
            Debug.LogWarning($"Muzzle not found on weapon '{weaponName}'!");
            return;
        }

        if(muzzleFlashEffect == null) {
            muzzleFlashEffect = weaponMuzzle.GetComponent<VisualEffect>();
        }

        if(muzzleLight == null) {
            muzzleLight = weaponMuzzle.transform.Find("MuzzleLight")?.gameObject;
            if(muzzleLight != null) {
                muzzleLight.SetActive(false);
            }
        }
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
        _reloadStartTime = Time.time;

        if(weaponAnimator) {
            weaponAnimator.SetTrigger(ReloadHash);
        }
        
        PlayReloadEffects();
        
        Debug.Log($"Reloading {weaponName}... ({reloadTime}s)");
    }
    
    public void CancelReload() {
        if(!IsReloading) return;
        
        IsReloading = false;
    }
    
    #endregion
    
    #region Private Methods - Shooting
    
    private bool CanFire() {
        return Time.time >= _lastFireTime + fireRate && currentAmmo > 0 && !IsReloading;
    }

    private void HandleCannotFire() {
        if(!(Time.time >= _lastFireTime + fireRate) || currentAmmo != 0 || IsReloading) return;
        
        PlayDryFireSound();
        _lastFireTime = Time.time;
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
            
            var trail = Instantiate(bulletTrail, weaponMuzzle.transform.position, Quaternion.identity);
            StartCoroutine(SpawnTrail(trail, weaponMuzzle.transform.position + fpCamera.transform.forward * 100, Vector3.zero, false));
        }
        
        currentAmmo--;
        _lastFireTime = Time.time;
        hudManager.UpdateAmmo(currentAmmo, magSize);
        
        Debug.Log($"{weaponName} fired! - Damage: {damage:F1} | Ammo: {currentAmmo}/{magSize} | Hit: {(shotHit ? "Yes" : "No")}");
    }
    
    private float GetScaledDamage() {
        if(fpController.velocity < minVelocityThreshold) {
            return baseDamage;
        }
        
        var scaleFactor = Mathf.InverseLerp(minVelocityThreshold, maxVelocityThreshold, fpController.velocity);
        var damageMultiplier = Mathf.Lerp(1f, maxDamageMultiplier, scaleFactor);
        
        return Mathf.Min(baseDamage * damageMultiplier, damageCap);
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
    }
    
    #endregion
    
    #region Private Methods - Effects
    
    private void PlayFireEffects() {
        if(weaponAnimator) {
            weaponAnimator.SetTrigger(RecoilHash);
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
        // This has been updated from the video implementation to fix a commonly raised issue about the bullet trails
        // moving slowly when hitting something close, and not
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
            // Instantiate(impactParticleSystem, hitPoint, Quaternion.LookRotation(hitNormal));
        }

        Destroy(trail.gameObject, trail.time);
    }
    
    #endregion
}
