using System.Collections;
using Game.Player;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public class Weapon : NetworkBehaviour {
        [Header("Weapon Data")] public GameObject weaponPrefab;
        public Vector3 spawnPosition;
        public Vector3 spawnRotation;
        public GameObject weaponMuzzle;
        [SerializeField] private Transform fpMuzzle;
        [SerializeField] private Transform worldMuzzle;
        public Vector3 positionWorldMuzzle;
        public int weaponSlot;
        public int currentAmmo;
        public int magSize;
        public int baseDamage;
        public float fireRate;
        public float bulletSpread;
        public float maxDamageMultiplier;
        public float damageCap;
        public float reloadTime;
        public string fireMode;
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;

        [Header("Speed Damage Scaling")] public const float MinSpeedThreshold = 15f;
        public const float MaxSpeedThreshold = 28f;
        [SerializeField] private float multiplierDecayRate = 1.8f;
        [SerializeField] private float multiplierGainRate = 0.8f;
        [SerializeField] private float multiplierGracePeriod = 1f;

        [Header("Components")] public CinemachineCamera fpCamera;
        public PlayerController playerController;
        public Animator playerAnimator;
        public NetworkAnimator networkAnimator;
        public LayerMask enemyLayer;
        public LayerMask worldLayer;

        [Header("Weapon References")] [SerializeField]
        private Animator weaponAnimator;

        [SerializeField] private VisualEffect muzzleFlashEffect;
        [SerializeField] private GameObject muzzleLight;
        [SerializeField] private float bulletSpeed = 500f;

        #region Private Fields

        private float _lastFireTime;
        private float _peakDamageMultiplier = 1f;
        private float _lastPeakTime;
        private float _graceEndTime;
        private Coroutine _reloadCoroutine;
        private NetworkDamageRelay _damageRelay;
        private NetworkSfxRelay _sfxRelay;
        private NetworkFxRelay _networkFXRelay;
        private AudioClip _hitSound;
        private AudioClip _killSound;
        private float _lastHitDamage;
        private float _targetHealth;

        #endregion

        #region Properties

        public bool IsReloading { get; private set; }
        public float CurrentDamageMultiplier { get; set; } = 1f;

        #endregion

        #region Animation Hashes

        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");

        #endregion

        [SerializeField] private float muzzleLightTime = 0.04f;
        [SerializeField] private VisualEffect worldMuzzleFlashPrefab;

        #region Unity Lifecycle

        private void Awake() {
            enemyLayer = LayerMask.GetMask("Enemy");
            worldLayer = LayerMask.GetMask("World");
            _lastFireTime = Time.time;
            _hitSound = Resources.Load<AudioClip>("Audio/Player/hitmarker");
            _killSound = Resources.Load<AudioClip>("Audio/Player/hitmarker-kill");
        }

        private void OnValidate() {
            if(fpCamera == null) {
                fpCamera = GetComponentInChildren<CinemachineCamera>();
            }

            if(playerController == null) {
                playerController = GetComponent<PlayerController>();
            }

            if(playerAnimator == null) {
                var animators = GetComponentsInChildren<Animator>();
                foreach(var anim in animators) {
                    if(anim != weaponAnimator) {
                        playerAnimator = anim;
                        break;
                    }
                }
            }

            if(networkAnimator == null) {
                networkAnimator = GetComponentInChildren<NetworkAnimator>();
            }
        }

        public void BindAndResolve(CinemachineCamera cam, PlayerController controller) {
            fpCamera = cam;
            playerController = controller;

            weaponAnimator = weaponPrefab.GetComponent<Animator>();
            muzzleFlashEffect = weaponMuzzle.GetComponent<VisualEffect>();

            muzzleLight = weaponMuzzle.transform.GetChild(0).gameObject;
            if(muzzleLight != null) {
                muzzleLight.SetActive(false);
            }

            if(_networkFXRelay == null) _networkFXRelay = GetComponent<NetworkFxRelay>();
            AssignMuzzlesIfMissing();

            if(_damageRelay == null) _damageRelay = GetComponent<NetworkDamageRelay>();
            if(_sfxRelay == null) _sfxRelay = GetComponent<NetworkSfxRelay>();
        }

        #endregion

        #region Initialization

        public void Initialize(WeaponData data) {
            weaponPrefab = data.weaponPrefab;
            spawnPosition = data.spawnPosition;
            spawnRotation = data.spawnRotation;
            weaponMuzzle = data.muzzlePrefab;
            weaponSlot = data.weaponSlot;
            positionWorldMuzzle = data.positionWorldMuzzle;

            magSize = data.magSize;
            currentAmmo = data.currentAmmo;

            baseDamage = data.baseDamage;
            maxDamageMultiplier = data.maxDamageMultiplier;
            damageCap = data.damageCap;

            fireRate = data.fireRate;
            fireMode = data.fireMode;
            bulletSpread = data.bulletSpread;

            reloadTime = data.reloadTime;

            bulletTrail = data.bulletTrail;
            bulletImpact = data.bulletImpact;
        }

        public Transform GetActiveMuzzle() {
            // Prefer the FP muzzle for owner, world muzzle for others
            if(playerController != null && playerController.IsOwner)
                return fpMuzzle ? fpMuzzle : weaponMuzzle?.transform;
            return worldMuzzle ? worldMuzzle : weaponMuzzle?.transform;
        }

        public void AssignMuzzlesIfMissing() {
            // Ensure fpMuzzle
            if(fpMuzzle == null && weaponMuzzle != null)
                fpMuzzle = weaponMuzzle.transform;

            if(worldMuzzle == null) {
                foreach(var t in GetComponentsInChildren<Transform>(true)) {
                    if(t.CompareTag("WorldMuzzle")) {
                        worldMuzzle = t;
                        break;
                    }
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

            var target = PerformShot();
            PlayFireSound(target);
        }

        public void StartReload() {
            if(!CanReload()) return;

            PlayReloadEffects();
            IsReloading = true;
            _reloadCoroutine = StartCoroutine(ReloadCoroutine());
        }

        private IEnumerator ReloadCoroutine() {
            yield return new WaitForSeconds(reloadTime);

            CompleteReload();
        }

        public void CancelReload() {
            if(!IsReloading) return;
            
            StopCoroutine(_reloadCoroutine);

            IsReloading = false;
            _reloadCoroutine = null;
        }

        #endregion

        #region Private Methods - Shooting

        private bool CanFire() {
            return Time.time >= _lastFireTime + fireRate && currentAmmo > 0 && !IsReloading;
        }

        private void HandleCannotFire() {
            if(Time.time < _lastFireTime + fireRate || _reloadCoroutine != null || currentAmmo != 0 ||
               IsReloading) return;

            _lastFireTime = Time.time;
            PlayDryFireSound();
        }

        private NetworkObject PerformShot() {
            var origin = fpCamera.transform.position;
            var forward = fpCamera.transform.forward;
            var hitLayer = enemyLayer | worldLayer;

            var shotHit = Physics.Raycast(origin, forward, out var hit, Mathf.Infinity, hitLayer);
            _lastHitDamage = GetScaledDamage();

            currentAmmo--;
            _lastFireTime = Time.time;
            HUDManager.Instance.UpdateAmmo(currentAmmo, magSize);

            NetworkObject target = null;

            if(shotHit) {
                Debug.DrawRay(origin, forward * hit.distance, Color.green, 5f);

                target = hit.collider.GetComponent<NetworkObject>();

                if(target != null && target.IsSpawned && _damageRelay != null) {
                    var targetRef = new NetworkObjectReference(target);
                    _targetHealth = target.GetComponent<PlayerController>().netHealth.Value;
                    _damageRelay.RequestDamageServerRpc(targetRef, _lastHitDamage, hit.point, hit.normal);
                }
            } else {
                Debug.DrawRay(origin, forward * 500f, Color.red, 5f);
            }

            var endPoint = shotHit ? hit.point : (origin + forward * 100f);
            
            if(_networkFXRelay != null)
                _networkFXRelay.RequestShotFx(this, endPoint);

            return target;
        }

        private float GetScaledDamage() {
            return Mathf.Min(baseDamage * CurrentDamageMultiplier, damageCap);
        }

        public void UpdateDamageMultiplier() {
            var currentSpeed = playerController.CurrentFullVelocity.magnitude;
            float targetMultiplier;

            // Calculate target multiplier based on current velocity
            if(currentSpeed < MinSpeedThreshold) {
                targetMultiplier = 1f;
            } else {
                var scaleFactor = Mathf.InverseLerp(MinSpeedThreshold, MaxSpeedThreshold, currentSpeed);
                targetMultiplier = Mathf.Lerp(1f, maxDamageMultiplier, scaleFactor);
            }

            // If target is higher than current, jump to it immediately and start grace period
            if(targetMultiplier >= CurrentDamageMultiplier) {
                CurrentDamageMultiplier = Mathf.Lerp(CurrentDamageMultiplier, targetMultiplier,
                    multiplierGainRate * Time.deltaTime);
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
                CurrentDamageMultiplier = Mathf.MoveTowards(CurrentDamageMultiplier, targetMultiplier,
                    multiplierDecayRate * Time.deltaTime);
                _peakDamageMultiplier = CurrentDamageMultiplier;
            }

            CurrentDamageMultiplier = Mathf.Clamp(CurrentDamageMultiplier, 1f, maxDamageMultiplier);
        }

        #endregion

        #region Private Methods - Reloading

        private bool CanReload() {
            return currentAmmo < magSize && _reloadCoroutine == null;
        }

        private void CompleteReload() {
            currentAmmo = magSize;
            IsReloading = false;
            _reloadCoroutine = null;

            HUDManager.Instance.UpdateAmmo(currentAmmo, magSize);
        }

        #endregion

        #region Private Methods - Effects

        public void PlayNetworkedMuzzleFlashLocal()
{
    var isOwner = playerController && playerController.IsOwner;

    if (isOwner)
    {
        weaponAnimator?.SetTrigger(RecoilHash);
        networkAnimator?.SetTrigger(RecoilHash);
        if (muzzleFlashEffect) muzzleFlashEffect.Play();
        if (muzzleLight) StartCoroutine(FlashLight(muzzleLight, muzzleLightTime));
        return;
    }

    // ---- Non-owner: world model ----
    AssignMuzzlesIfMissing();

    // Prefer a dedicated world VFX prefab
    if (worldMuzzleFlashPrefab && worldMuzzle)
    {
        var fx = Instantiate(worldMuzzleFlashPrefab, worldMuzzle.position, worldMuzzle.rotation);
        fx.Play();
        Destroy(fx.gameObject, 1f);
    }
    else
    {
        // Fallbacks and diagnostics
        if (!worldMuzzle)
        {
            // try to find any child tagged/suffixed as world muzzle
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.CompareTag("WorldMuzzle") || t.name.Contains("WorldMuzzle"))
                {
                    worldMuzzle = t;
                    break;
                }
            }
        }

        if (worldMuzzle && muzzleFlashEffect)  // sometimes FP effect is shared
        {
            // As a last resort, play the same effect at the world muzzle
            var fxGO = Instantiate(muzzleFlashEffect.gameObject, worldMuzzle.position, worldMuzzle.rotation);
            var fx = fxGO.GetComponent<VisualEffect>();
            fx?.Play();
            Destroy(fxGO, 1f);
        }
        else
        {
            Debug.LogWarning($"[Weapon] No world muzzle VFX for {name} (worldMuzzle={(worldMuzzle?worldMuzzle.name:"null")}, worldMuzzleFlashPrefab={(worldMuzzleFlashPrefab?"ok":"null")})");
        }
    }

    // Try toggling a world light if one exists
    GameObject worldMuzzleLight = null;
    if (worldMuzzle)
    {
        worldMuzzleLight = worldMuzzle.GetComponentInChildren<Light>()?.gameObject;
        if (worldMuzzleLight == null)
        {
            foreach (Transform child in worldMuzzle.GetComponentsInChildren<Transform>(true))
            {
                if (child.GetComponent<Light>() != null) { worldMuzzleLight = child.gameObject; break; }
            }
        }
    }
    if (worldMuzzleLight) StartCoroutine(FlashLight(worldMuzzleLight, muzzleLightTime));
}

        private IEnumerator FlashLight(GameObject lightObj, float time) {
            if(!lightObj) yield break;
            lightObj.SetActive(true);
            yield return new WaitForSeconds(time);
            if(lightObj) lightObj.SetActive(false);
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end) {
            if(bulletTrail == null) return;
            var trail = Instantiate(bulletTrail, start, Quaternion.LookRotation(end - start));
            StartCoroutine(SpawnTrail(trail, end, end, true));
        }

        private void PlayFireSound(NetworkObject target) {
            if(_sfxRelay == null)
                _sfxRelay = GetComponent<NetworkSfxRelay>();

            // owner issues the request
            if(_sfxRelay != null && playerController.IsOwner) {
                _sfxRelay.RequestWorldSfx(SfxKey.Shoot, attachToSelf: true, true);

                if(target == null) return;

                if(_targetHealth - _lastHitDamage <= 0) {
                    SoundFXManager.Instance.PlayUISound(_killSound);
                    playerController.damageDealt.Value += _lastHitDamage;
                    playerController.kills.Value++;
                } else {
                    SoundFXManager.Instance.PlayUISound(_hitSound);
                    playerController.damageDealt.Value += _lastHitDamage;
                }
            }
        }

        private void PlayDryFireSound() {
            if(_sfxRelay != null && playerController.IsOwner)
                _sfxRelay.RequestWorldSfx(SfxKey.Dry, attachToSelf: true, true);
        }

        private void PlayReloadEffects() {
            if(weaponAnimator)
                weaponAnimator.SetTrigger(ReloadHash);

            if(networkAnimator)
                networkAnimator.SetTrigger(ReloadHash);

            if(playerController.IsOwner)
                _sfxRelay?.RequestWorldSfx(SfxKey.Reload, attachToSelf: true);
        }

        private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact) {
            var startPosition = trail.transform.position;
            var distance = Vector3.Distance(trail.transform.position, hitPoint);
            var remainingDistance = distance;

            while(remainingDistance > 0) {
                trail.transform.position = Vector3.Lerp(startPosition, hitPoint, 1 - (remainingDistance / distance));

                remainingDistance -= bulletSpeed * Time.deltaTime;

                yield return null;
            }

            trail.transform.position = hitPoint;
            if(madeImpact) {
                // Instantiate(bulletImpact, hitPoint, Quaternion.LookRotation(hitNormal));
            }

            Destroy(trail.gameObject, trail.time);
        }

        #endregion

        public void ResetWeapon() {
            currentAmmo = magSize;
            IsReloading = false;
            _lastFireTime = Time.time;
        }
    }
}