using System.Collections;
using Game.Player;
using Network.Rpc;
using Network.Singletons;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.VFX;

namespace Game.Weapons {
    public class Weapon : NetworkBehaviour {
        [Header("References")] 
        [SerializeField] private CinemachineCamera fpCamera;
        [SerializeField] private PlayerController playerController;
        [SerializeField] private Animator playerAnimator;
        [SerializeField] private LayerMask enemyLayer;
        [SerializeField] private LayerMask worldLayer;
        [SerializeField] private NetworkDamageRelay damageRelay;
        [SerializeField] private NetworkSfxRelay sfxRelay;
        [SerializeField] private NetworkFxRelay networkFXRelay;
        [SerializeField] private AudioClip hitSound;
        [SerializeField] private AudioClip killSound;

        [Header("Current Weapon State")]
        private WeaponData _currentWeaponData;
        private GameObject _currentFpWeaponInstance;
        private GameObject _currentWorldWeaponInstance;
        private Animator _weaponAnimator;
        private GameObject _fpMuzzleLight;
        private GameObject _worldMuzzleLight;
        private Coroutine _fpMuzzleLightCoroutine;
        private Coroutine _worldMuzzleLightCoroutine;

        [Header("Runtime State")]
        public int currentAmmo;
        public bool IsReloading { get; private set; }
        public float CurrentDamageMultiplier { get; set; } = 1f;

        [Header("Speed Damage Scaling")] 
        public const float MinSpeedThreshold = 15f;
        public const float MaxSpeedThreshold = 28f;
        [SerializeField] private float multiplierDecayRate = 1.8f;
        [SerializeField] private float multiplierGainRate = 0.8f;
        [SerializeField] private float multiplierGracePeriod = 1f;

        [Header("Visual Settings")]
        [SerializeField] private float bulletSpeed = 500f;

        [SerializeField] private float muzzleLightTime = 5f;
        private float _fpLightOffTime;
        private float _worldLightOffTime;

        #region Private Fields

        private float _lastFireTime;
        private float _peakDamageMultiplier = 1f;
        private float _lastPeakTime;
        private Coroutine _reloadCoroutine;
        private float _lastHitDamage;

        #endregion

        #region Animation Hashes

        private static readonly int RecoilHash = Animator.StringToHash("Recoil");
        private static readonly int ReloadHash = Animator.StringToHash("Reload");

        #endregion

        #region Unity Lifecycle

        private void Awake() {
            _lastFireTime = Time.time;
            
            damageRelay.OnHitConfirm -= OnHitConfirm;
            damageRelay.OnHitConfirm += OnHitConfirm;
        }

        private void LateUpdate() {
            if(_fpMuzzleLight && _fpMuzzleLight.activeSelf && Time.time >= _fpLightOffTime) {
                _fpMuzzleLight.SetActive(false);
            }
    
            // Turn off 3P light when time is up
            if(_worldMuzzleLight && _worldMuzzleLight.activeSelf && Time.time >= _worldLightOffTime) {
                _worldMuzzleLight.SetActive(false);
            }
        }

        private void OnHitConfirm(bool wasKill) {
            SoundFXManager.Instance.PlayUISound(wasKill ? killSound : hitSound);
        }

        #endregion

        #region Weapon Switching

        /// <summary>
        /// Switch to a new weapon by loading its data
        /// </summary>
        public void SwitchToWeapon(WeaponData newWeaponData, GameObject fpWeaponInstance, GameObject worldWeaponInstance, int restoredAmmo) {
            // Cancel any ongoing reload
            if(_reloadCoroutine != null) {
                StopCoroutine(_reloadCoroutine);
                _reloadCoroutine = null;
            }

            // Set new weapon data
            _currentWeaponData = newWeaponData;
            _currentFpWeaponInstance = fpWeaponInstance;
            _currentWorldWeaponInstance = worldWeaponInstance;

            // Restore ammo
            currentAmmo = restoredAmmo;
            Debug.Log($"[Weapon] SwitchToWeapon - currentAmmo set to {currentAmmo}, magSize = {_currentWeaponData.magSize}");
            IsReloading = false;

            // Get animator from FP weapon
            if(_currentFpWeaponInstance) {
                _weaponAnimator = _currentFpWeaponInstance.GetComponent<Animator>();
                var lightTransform = _currentFpWeaponInstance.transform.Find(_currentWeaponData.fpMuzzleLightChildName);
                _fpMuzzleLight = lightTransform?.gameObject;
                
                if(_fpMuzzleLight) {
                    _fpMuzzleLight.SetActive(false);
                }
            }
            
            if(_currentWorldWeaponInstance != null) {
                var lightTransform = _currentWorldWeaponInstance.transform.Find(_currentWeaponData.worldMuzzleLightChildName);
                _worldMuzzleLight = lightTransform?.gameObject;
            }
            
            if(_worldMuzzleLight) {
                _worldMuzzleLight.SetActive(false);
            }

            // Update HUD
            if(playerController && playerController.IsOwner) {
                HUDManager.Instance.UpdateAmmo(currentAmmo, _currentWeaponData.magSize);
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
            PlayFireSound();
        }

        public void StartReload() {
            if(!CanReload()) return;

            PlayReloadEffects();
            IsReloading = true;
            _reloadCoroutine = StartCoroutine(ReloadCoroutine());
        }

        private IEnumerator ReloadCoroutine() {
            yield return new WaitForSeconds(_currentWeaponData.reloadTime);
            CompleteReload();
        }

        public void CancelReload() {
            if(!IsReloading) return;
            if(_reloadCoroutine != null) {
                StopCoroutine(_reloadCoroutine);
            }

            IsReloading = false;
            _reloadCoroutine = null;
        }

        public void ResetWeapon() {
            if(!_currentWeaponData) return;
            currentAmmo = _currentWeaponData.magSize;
            IsReloading = false;
            _lastFireTime = Time.time;
            CurrentDamageMultiplier = 1f;
        }

        #endregion

        #region Getters

        public Vector3 GetMuzzlePosition() {
            if(playerController && playerController.IsOwner) {
                // Owner: FP muzzle position (weapon local position -> world space)
                if(_currentFpWeaponInstance) {
                    return _currentFpWeaponInstance.transform.TransformPoint(_currentWeaponData.fpMuzzleLocalPosition);
                }
                return fpCamera.transform.position;
            } else {
                // Non-owner: World muzzle position (weapon local position -> world space)
                if(_currentWorldWeaponInstance) {
                    return _currentWorldWeaponInstance.transform.TransformPoint(_currentWeaponData.worldMuzzleLocalPosition);
                }
                return transform.position;
            }
        }

        public Quaternion GetMuzzleRotation() {
            if(playerController && playerController.IsOwner) {
                return _currentFpWeaponInstance ? _currentFpWeaponInstance.transform.rotation : fpCamera.transform.rotation;
            }

            return _currentWorldWeaponInstance ? _currentWorldWeaponInstance.transform.rotation : transform.rotation;
        }

        public int GetWeaponSlot() => _currentWeaponData?.weaponSlot ?? 0;
        public float GetFireRate() => _currentWeaponData?.fireRate ?? 0.1f;
        public int GetMagSize() => _currentWeaponData?.magSize ?? 30;
        public GameObject GetWeaponPrefab() => _currentFpWeaponInstance;
        public Vector3 GetSpawnPosition() => _currentWeaponData?.spawnPosition ?? Vector3.zero;
        public Vector3 GetSpawnRotation() => _currentWeaponData?.spawnRotation ?? Vector3.zero;

        #endregion

        #region Private Methods - Shooting

        private bool CanFire() {
            if(!_currentWeaponData) return false;
            return Time.time >= _lastFireTime + _currentWeaponData.fireRate && currentAmmo > 0 && !IsReloading;
        }

        private void HandleCannotFire() {
            if(!_currentWeaponData) return;
            if(Time.time < _lastFireTime + _currentWeaponData.fireRate || IsReloading || currentAmmo != 0) return;

            _lastFireTime = Time.time;
            PlayDryFireSound();
        }

        private void PerformShot() {
            var origin = fpCamera.transform.position;
            var forward = fpCamera.transform.forward;
            var hitLayer = enemyLayer | worldLayer;

            var shotHit = Physics.Raycast(origin, forward, out var hit, Mathf.Infinity, hitLayer);
            _lastHitDamage = GetScaledDamage();

            currentAmmo--;
            _lastFireTime = Time.time;

            if(playerController && playerController.IsOwner) {
                HUDManager.Instance.UpdateAmmo(currentAmmo, _currentWeaponData.magSize);
            }

            if(shotHit) {
                var target = hit.collider.GetComponent<NetworkObject>();

                if(target && target.IsSpawned) {
                    var targetRef = new NetworkObjectReference(target);
                    damageRelay.RequestDamageServerRpc(targetRef, _lastHitDamage, hit.point, hit.normal);
                }
            }

            var endPoint = shotHit ? hit.point : (origin + forward * 100f);

            if(playerController.IsOwner) {
                var startPos = GetMuzzlePosition();
                PlayLocalMuzzleFlash();
                SpawnTracerLocal(startPos, endPoint);
            }

            networkFXRelay.RequestShotFx(endPoint);
        }

        private float GetScaledDamage() {
            if(!_currentWeaponData) return 0f;
            return Mathf.Min(_currentWeaponData.baseDamage * CurrentDamageMultiplier, _currentWeaponData.damageCap);
        }

        public void UpdateDamageMultiplier() {
            if(!_currentWeaponData) return;

            var currentSpeed = playerController.CurrentFullVelocity.magnitude;
            float targetMultiplier;

            // Calculate target multiplier based on current velocity
            if(currentSpeed < MinSpeedThreshold) {
                targetMultiplier = 1f;
            } else {
                var scaleFactor = Mathf.InverseLerp(MinSpeedThreshold, MaxSpeedThreshold, currentSpeed);
                targetMultiplier = Mathf.Lerp(1f, _currentWeaponData.maxDamageMultiplier, scaleFactor);
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
                CurrentDamageMultiplier = Mathf.MoveTowards(CurrentDamageMultiplier, targetMultiplier,
                    multiplierDecayRate * Time.deltaTime);
                _peakDamageMultiplier = CurrentDamageMultiplier;
            }

            CurrentDamageMultiplier = Mathf.Clamp(CurrentDamageMultiplier, 1f, _currentWeaponData.maxDamageMultiplier);
        }

        #endregion

        #region Private Methods - Reloading

        private bool CanReload() {
            if(!_currentWeaponData) return false;
            return currentAmmo < _currentWeaponData.magSize && _reloadCoroutine == null;
        }

        private void CompleteReload() {
            if(!_currentWeaponData) return;
            currentAmmo = _currentWeaponData.magSize;
            IsReloading = false;
            _reloadCoroutine = null;

            if(playerController.IsOwner) {
                HUDManager.Instance.UpdateAmmo(currentAmmo, _currentWeaponData.magSize);
            }
        }

        #endregion

        #region Private Methods - Effects

        /// <summary>
        /// Play muzzle flash locally (owner only, FP)
        /// </summary>
        private void PlayLocalMuzzleFlash() {
            _weaponAnimator?.SetTrigger(RecoilHash);
            playerAnimator?.SetTrigger(RecoilHash);
    
            if(_currentWeaponData?.muzzleFlashPrefab && _currentFpWeaponInstance) {
                Vector3 muzzlePos = _currentFpWeaponInstance.transform.TransformPoint(_currentWeaponData.fpMuzzleLocalPosition);
                Quaternion muzzleRot = GetMuzzleRotation();
        
                var fxGo = Instantiate(_currentWeaponData.muzzleFlashPrefab, muzzlePos, muzzleRot);
                var fx = fxGo.GetComponent<VisualEffect>();
                fx?.Play();
                Destroy(fxGo, 1f);
            }
    
            if(_fpMuzzleLight) {
                _fpMuzzleLight.SetActive(true);
                _fpLightOffTime = Time.time + muzzleLightTime;
            }
        }

        /// <summary>
        /// Play muzzle flash from network (non-owners only, 3P)
        /// Called via NetworkFxRelay RPC
        /// </summary>
        public void PlayNetworkedMuzzleFlash() {
            // NON-OWNER ONLY: Play 3P world muzzle flash
            if(_currentWeaponData?.muzzleFlashPrefab && _currentWorldWeaponInstance) {
                Vector3 muzzlePos = _currentWorldWeaponInstance.transform.TransformPoint(_currentWeaponData.worldMuzzleLocalPosition);
                Quaternion muzzleRot = GetMuzzleRotation();
        
                var fxGo = Instantiate(_currentWeaponData.muzzleFlashPrefab, muzzlePos, muzzleRot);
                var fx = fxGo.GetComponent<VisualEffect>();
                fx?.Play();
                Destroy(fxGo, 1f);
            }
    
            if(_worldMuzzleLight) {
                _worldMuzzleLight.SetActive(true);
                _worldLightOffTime = Time.time + muzzleLightTime;
            }
        }

        public void SpawnTracerLocal(Vector3 start, Vector3 end) {
            if(!_currentWeaponData || !_currentWeaponData.bulletTrail) return;
            var trail = Instantiate(_currentWeaponData.bulletTrail, start, Quaternion.LookRotation(end - start));
            StartCoroutine(SpawnTrail(trail, end, end, true));
        }

        private void PlayFireSound() {
            if(playerController.IsOwner)
                sfxRelay.RequestWorldSfx(SfxKey.Shoot, attachToSelf: true, true);
        }

        private void PlayDryFireSound() {
            if(playerController.IsOwner)
                sfxRelay.RequestWorldSfx(SfxKey.Dry, attachToSelf: true, true);
        }

        private void PlayReloadEffects() {
            _weaponAnimator?.SetTrigger(ReloadHash);

            playerAnimator.SetTrigger(ReloadHash);

            if(playerController.IsOwner)
                sfxRelay.RequestWorldSfx(SfxKey.Reload, attachToSelf: true);
        }

        private IEnumerator SpawnTrail(TrailRenderer trail, Vector3 hitPoint, Vector3 hitNormal, bool madeImpact) {
            var startPosition = trail.transform.position;
            var distance = Vector3.Distance(trail.transform.position, hitPoint);

            var remainingDistance = distance;

            while(remainingDistance > 0) {
                var t = 1f - (remainingDistance / distance);
                trail.transform.position = Vector3.Lerp(startPosition, hitPoint, t);
                remainingDistance -= bulletSpeed * Time.deltaTime;
                yield return null;
            }

            trail.transform.position = hitPoint;
            if(madeImpact && _currentWeaponData && _currentWeaponData.bulletImpact) {
                Instantiate(_currentWeaponData.bulletImpact, hitPoint, Quaternion.LookRotation(hitNormal));
            }

            Destroy(trail.gameObject, trail.time);
        }

        #endregion
    }
}