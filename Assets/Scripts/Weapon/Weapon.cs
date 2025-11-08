using System.Collections;
using Player;
using Relays;
using Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Weapon {
    public class Weapon : NetworkBehaviour
    {
        [Header("Weapon Data")]
        public GameObject weaponPrefab;
        public GameObject weaponMuzzle;
        [SerializeField] private Transform fpMuzzle;
        [SerializeField] private Transform worldMuzzle;
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
        public AudioClip[] fireSounds;
        public AudioClip[] reloadSounds;
        public AudioClip[] dryFireSounds;
        public TrailRenderer bulletTrail;
        public ParticleSystem bulletImpact;
    
        [Header("Speed Damage Scaling")]
        public const float MinSpeedThreshold = 15f;
        public const float MaxSpeedThreshold = 30f;
        [SerializeField] private float multiplierDecayRate = 1.8f;
        [SerializeField] private float multiplierGracePeriod = 1.25f;

        [Header("Components")]
        public CinemachineCamera fpCamera;
        public PlayerController playerController;
        public Animator playerAnimator;
        public NetworkAnimator networkAnimator;
        public LayerMask enemyLayer;
        public LayerMask worldLayer;
    
        [Header("Weapon References")]
        [SerializeField] private Animator weaponAnimator;
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
        private NetworkSoundRelay _soundRelay;
        private NetworkFXRelay _networkFXRelay;
        private AudioClip _hitSound;
        private AudioClip _killSound;

        #endregion
    
        #region Properties

        public bool IsReloading { get; private set; }
        public float CurrentDamageMultiplier { get; private set; } = 1f;

        #endregion

        #region Animation Hashes
    
        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");
    
        #endregion
    
        [SerializeField] private TrailRenderer tracerPrefab;
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

            if(networkAnimtor == null) {
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

            if(_networkFXRelay == null) _networkFXRelay = GetComponent<NetworkFXRelay>();
            AssignMuzzlesIfMissing();
        
            if (_damageRelay == null) _damageRelay = GetComponent<NetworkDamageRelay>();
            if (_soundRelay == null) _soundRelay = GetComponent<NetworkSoundRelay>();
        }
    
        #endregion
    
        #region Initialization
    
        public void Initialize(WeaponData data) {
            weaponPrefab = data.weaponPrefab;
            weaponMuzzle = data.muzzlePrefab;
            weaponSlot = data.weaponSlot;
        
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
    
        private NetworkObject PerformShot() {
            var origin = fpCamera.transform.position;
            var forward = fpCamera.transform.forward;
            var hitLayer = enemyLayer | worldLayer;
        
            var shotHit = Physics.Raycast(origin, forward, out var hit, Mathf.Infinity, hitLayer);
            var damage = GetScaledDamage();
        
            currentAmmo--;
            _lastFireTime = Time.time;
            HUDManager.Instance.UpdateAmmo(currentAmmo, magSize);
        
            NetworkObject target = null;

            if(shotHit) {
                Debug.DrawRay(origin, forward * hit.distance, Color.green, 5f);
            
                target = hit.collider.GetComponent<NetworkObject>();
            
                if(target != null && target.IsSpawned && _damageRelay != null) {
                    var targetRef = new NetworkObjectReference(target);
                    _damageRelay.RequestDamageServerRpc(targetRef, damage, hit.point, hit.normal);
                }
            } else {
                Debug.DrawRay(origin, forward * 500f, Color.red, 5f);
            }
        
            var endPoint = shotHit ? hit.point : (origin + forward * 100f);
            if(_networkFXRelay != null && playerController.IsOwner)
                _networkFXRelay.RequestShotFx(endPoint);

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
    
        public void PlayNetworkedMuzzleFlashLocal() {
            // Owner sees FP viewmodel VFX; others see world muzzle prefab
            var isOwner = playerController != null && playerController.IsOwner;

            if(isOwner) {
                if(weaponAnimator) weaponAnimator.SetTrigger(RecoilHash);
                if(networkAnimator) networkAnimator.SetTrigger(RecoilHash);
                if(muzzleFlashEffect) muzzleFlashEffect.Play();
                if(muzzleLight) StartCoroutine(FlashLight(muzzleLight, muzzleLightTime));
            } else {
                // Spawn a tiny world flash at worldMuzzle so others see it
                if(worldMuzzleFlashPrefab && worldMuzzle) {
                    var fx = Instantiate(worldMuzzleFlashPrefab, worldMuzzle.position, worldMuzzle.rotation);
                    fx.Play();
                    Destroy(fx.gameObject, 1.0f);
                }
            }
        }
    
        private IEnumerator FlashLight(GameObject lightObj, float time) {
            if(!lightObj) yield break;
            lightObj.SetActive(true);
            yield return new WaitForSeconds(time);
            if(lightObj) lightObj.SetActive(false);
        }
    
        public void SpawnTracerLocal(Vector3 start, Vector3 end) {
            Debug.LogWarning("Active muzzle: " + GetActiveMuzzle());
            if(bulletTrail == null) return;
            var trail = Instantiate(bulletTrail, start, Quaternion.LookRotation(end - start));
            // StartCoroutine(AnimateTracer(trail, start, end, bulletSpeed));
            StartCoroutine(SpawnTrail(trail, end, end, true));
        }
    
        private IEnumerator AnimateTracer(TrailRenderer trail, Vector3 start, Vector3 end, float speed) {
            var dist = Vector3.Distance(start, end);
            var t = 0f;
            while(t < 1f && trail) {
                t += (speed * Time.deltaTime) / Mathf.Max(0.001f, dist);
                trail.transform.position = Vector3.Lerp(start, end, t);
                yield return null;
            }
            if(trail) {
                trail.transform.position = end;
                Destroy(trail.gameObject, trail.time);
            }
        }
    
        private void PlayFireSound(NetworkObject target) {
            if(_soundRelay == null)
                _soundRelay = GetComponent<NetworkSoundRelay>();

            // owner issues the request
            if(_soundRelay != null && playerController.IsOwner) {
                _soundRelay.RequestWorldSfx(SFXKey.Shoot, attachToSelf: true, true);
            
                if(target == null) return;

                var targetDead = target.GetComponent<PlayerController>().netIsDead.Value;

                // may need to predict death with health and damage, IsDead may not be updated yet
                SoundFXManager.Instance.PlayUISound(targetDead ? _killSound : _hitSound);
            } 
        }
    
        private void PlayDryFireSound() {
            if(_soundRelay != null && playerController.IsOwner)
                _soundRelay.RequestWorldSfx(SFXKey.Dry, attachToSelf: true, true);
        }

        private void PlayReloadEffects() {
            if(weaponAnimator)
                weaponAnimator.SetTrigger(ReloadHash);
        
            if(networkAnimator)
                networkAnimator.SetTrigger(ReloadHash);
        
            if(_soundRelay != null && playerController.IsOwner)
                _soundRelay.RequestWorldSfx(SFXKey.Reload, attachToSelf: true);
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

        public void ResetWeapon() {
            currentAmmo = magSize;
            IsReloading = false;
            _lastFireTime = Time.time;
        }
    }
}
