using JetBrains.Annotations;
using UnityEngine;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class PlayerController : NetworkBehaviour, IDamageable {
    #region Constants
    
    private const float WalkSpeed = 5f;
    private const float SprintSpeed = 10f;
    private const float JumpHeight = 2f;
    private const float CrouchSpeed = 2.5f;
    private const float StandHeight = 1.7f;
    private const float CrouchHeight = 1.1f;
    private const float StandCollider = 1.9f;
    private const float CrouchCollider = 1.3f;
    private const float StandCheckHeight = StandCollider - CrouchCollider;
    private const float PitchLimit = 90f;
    private const float GravityScale = 3f;
    
    #endregion
    
    #region Animation Parameter Hashes

    private static readonly int MoveXHash = Animator.StringToHash("moveX");
    private static readonly int MoveYHash = Animator.StringToHash("moveY");
    private static readonly int LookXHash = Animator.StringToHash("lookX");
    private static readonly int IsSprintingHash = Animator.StringToHash("IsSprinting");
    private static readonly int IsCrouchingHash = Animator.StringToHash("IsCrouching");
    private static readonly int JumpTriggerHash = Animator.StringToHash("JumpTrigger");
    private static readonly int LandTriggerHash = Animator.StringToHash("LandTrigger");
    private static readonly int IsJumpingHash = Animator.StringToHash("IsJumping");
    private static readonly int IsFallingHash = Animator.StringToHash("IsFalling");

    #endregion
    
    #region Serialized Fields
    
    [Header("Movement Parameters")]
    [SerializeField] private float acceleration = 15f;
    [SerializeField] private float airAcceleration = 50f;
    [SerializeField] private float maxAirSpeed = 5f;
    [SerializeField] private float friction = 8f;
    
    [Header("Movement Audio Clips")]
    [SerializeField] public AudioClip[] walkSounds;
    [SerializeField] public AudioClip[] runSounds;
    [SerializeField] private AudioClip[] jumpSounds;
    [SerializeField] private AudioClip[] landSounds;
    [SerializeField] private AudioClip jumpPadSound;

    [Header("Look Parameters")]
    [SerializeField] public Vector2 lookSensitivity = new (0.1f, 0.1f);
    
    [Header("Components")]
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Animator characterAnimator;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private GrappleController grappleController;
    [SerializeField] private PlayerRagdoll playerRagdoll;
    [SerializeField] private DeathCamera deathCamera;

    [Header("Player Fields")]
    [SerializeField] private float health = 100;
    
    #endregion
    
    #region Public Input Fields
    
    public Vector2 moveInput;
    public Vector2 lookInput;
    public bool sprintInput;
    public bool crouchInput;
    
    #endregion
    
    #region Private Fields
    
    private float _currentPitch;
    private float _maxSpeed = WalkSpeed;
    private float _verticalVelocity;
    private Vector3 _horizontalVelocity;
    private bool _isJumping;
    private bool _isFalling;
    private bool _wasFalling;
    private float _crouchTransition;
    private HUDManager _hudManager;
    private CinemachineImpulseSource _impulseSource;
    private Vector3? _lastHitPoint;
    private Vector3? _lastHitNormal;
    private LayerMask _playerBodyLayer;
    private LayerMask _maskedLayer;
    
    #endregion
    
    #region Private Properties
    
    public Vector3 CurrentVelocity => _horizontalVelocity;
    public Vector3 CurrentFullVelocity => new(_horizontalVelocity.x, _verticalVelocity, _horizontalVelocity.z);
    
    public bool IsDead => health <= 0;
    private bool IsGrounded => characterController.isGrounded;
    
    private float CurrentPitch {
        get => _currentPitch;

        set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
    }
    
    #endregion
    
    #region Unity Lifecycle

    private void Awake() {
        _hudManager = HUDManager.Instance;
        _impulseSource = FindFirstObjectByType<CinemachineImpulseSource>();
        _playerBodyLayer = LayerMask.GetMask("Player");
        _maskedLayer = LayerMask.GetMask("Masked");
    }

    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();
        
        if(_impulseSource == null) {
            _impulseSource = FindFirstObjectByType<CinemachineImpulseSource>();
        }

        if(!IsOwner) {
            gameObject.layer = LayerMask.NameToLayer("Default");
        }
    }
    
    private void Start() {
        if(_impulseSource == null) {
            _impulseSource = FindFirstObjectByType<CinemachineImpulseSource>();
        }
    }

    private void Update() {
        if(IsOwner) {
            if(!IsDead) {
                HandleMovement();
                HandleCrouch();

                if(transform.position.y <= 600f) {
                    TakeDamage(health);
                }
            }
        }
        
        if(!IsDead) {
            HandleLanding();
            UpdateAnimator();
        }
    }

    private void LateUpdate() {
        if(IsOwner && !IsDead) {
            HandleLook();
        }
    }
    
    #endregion

    #region Public Methods
    
    public void TryJump(float height = JumpHeight) {
        if(!IsGrounded) {
            return;
        }

        height = CheckForJumpPad() ? 15f : height;

        if(Mathf.Approximately(height, 15f)) {
            SoundFXManager.Instance.PlaySoundFX(jumpPadSound, transform, true, "jumpPad");
        }
        
        _verticalVelocity = Mathf.Sqrt(height * -2f * Physics.gravity.y * GravityScale);
        characterAnimator.SetTrigger(JumpTriggerHash);
        characterAnimator.SetBool(IsJumpingHash, true);
        _isJumping = true;
        SoundFXManager.Instance.PlayRandomSoundFX(jumpSounds, transform, false, "jump");
    }
    
    public void PlayWalkSound() {
        if(!IsGrounded) return;
        SoundFXManager.Instance.PlayRandomSoundFX(walkSounds, transform, false, "walk");

    }
    
    public void PlayRunSound() {
        if(!IsGrounded) return;
        SoundFXManager.Instance.PlayRandomSoundFX(runSounds, transform, false, "run");
    }
    
    #endregion
    
    #region Movement Methods

    private void OnControllerColliderHit(ControllerColliderHit hit) {
        if(hit.gameObject.CompareTag("JumpPad")) {
            TryJump(15f);
        }
        
        grappleController.CancelGrapple();
    }
    
    private bool CheckForJumpPad() {
        // Raycast downward to check for jump pad
        if(Physics.Raycast(transform.position, Vector3.down, out RaycastHit hit, characterController.height * 0.6f)) {
            if(hit.collider.CompareTag("JumpPad")) {
                return true;
            }
        }
        return false;
    }
    
    private void HandleMovement() {
        UpdateMaxSpeed();
        CalculateHorizontalVelocity();
        CheckCeilingHit();
        ApplyGravity();
        MoveCharacter();
    }

    private void UpdateMaxSpeed() {
        if(crouchInput) {
            _maxSpeed = CrouchSpeed;
        } else if(sprintInput) {
            _maxSpeed = SprintSpeed;
        } else {
            _maxSpeed = WalkSpeed;
        }
    }

    private void CalculateHorizontalVelocity() {
        var motion = (transform.forward * moveInput.y + transform.right * moveInput.x).normalized;
        motion.y = 0f;

        if(IsGrounded) {
            ApplyFriction();
            ApplyDirectionChange(motion);
            
            var targetVelocity = motion.sqrMagnitude >= 0.1f ? motion * _maxSpeed : Vector3.zero;
            _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, targetVelocity, acceleration * Time.deltaTime);
        } else {
            AirStrafe(motion);
        }
    }

    private void ApplyFriction() {
        if(!(moveInput.sqrMagnitude < 0.01f)) return;
        
        var horizontalSpeed = _horizontalVelocity.magnitude;
            
        if(!(horizontalSpeed > 0.1f)) return;
            
        var drop = horizontalSpeed * friction * Time.deltaTime;
        _horizontalVelocity *= Mathf.Max(horizontalSpeed - drop, 0f) / horizontalSpeed;
    }

    private void ApplyDirectionChange(Vector3 motion) {
        if(!(_horizontalVelocity.magnitude > 0.1f) || !(motion.magnitude > 0.1f)) return;
        
        var angle = Vector3.Angle(_horizontalVelocity, motion);

        if(!(angle > 90f)) return;
        
        var normalizedAngle = Mathf.InverseLerp(90f, 180f, angle);
        var reduction = Mathf.Lerp(0.85f, 0.2f, normalizedAngle * normalizedAngle);
        _horizontalVelocity *= reduction;
    }

    private void AirStrafe(Vector3 wishDir) {
        if(moveInput.sqrMagnitude < 0.01f) return;

        var currentSpeed = Vector3.Dot(_horizontalVelocity, wishDir);
        var addSpeed = maxAirSpeed - currentSpeed;

        if(addSpeed <= 0) return;
        
        var accelSpeed = airAcceleration * Time.deltaTime;
        accelSpeed = Mathf.Min(accelSpeed, addSpeed);
        
        _horizontalVelocity += wishDir * accelSpeed;
    }

    private void CheckCeilingHit() {
        Debug.DrawRay(fpCamera.transform.position, 0.75f * Vector3.up, Color.yellow);
        
        var mask = _playerBodyLayer | _maskedLayer;
        if(Physics.Raycast(fpCamera.transform.position, Vector3.up, out var hit, 0.75f, ~mask) && _verticalVelocity > 0f) {
            grappleController.CancelGrapple();
            _verticalVelocity = 0f;
        }
    }

    private void ApplyGravity() {
        if(IsGrounded && _verticalVelocity <= 0.01f) {
            _verticalVelocity = -3f;
        } else {
            _verticalVelocity += Physics.gravity.y * GravityScale * Time.deltaTime;
        }
    }

    private void MoveCharacter() {
        var fullVelocity = new Vector3(_horizontalVelocity.x, _verticalVelocity, _horizontalVelocity.z);
        characterController.Move(fullVelocity * Time.deltaTime);
    }
    
    private void HandleCrouch() {
        if(Physics.Raycast(fpCamera.transform.position, Vector3.up, StandCheckHeight) && !crouchInput) return;
        
        characterAnimator.SetBool(IsCrouchingHash, crouchInput);

        var targetTransition = crouchInput ? 1f : 0f;
        _crouchTransition = Mathf.Lerp(_crouchTransition, targetTransition, 10f * Time.deltaTime);
    
        var targetCameraHeight = Mathf.Lerp(StandHeight, CrouchHeight, _crouchTransition);
        var targetColliderHeight = Mathf.Lerp(StandCollider, CrouchCollider, _crouchTransition);
    
        fpCamera.transform.localPosition = new Vector3(0f, targetCameraHeight, 0f);
    
        // Adjust both height and center together
        var centerY = targetColliderHeight / 2f;
        characterController.height = targetColliderHeight;
        characterController.center = new Vector3(0f, centerY, 0f);
    }
    
    #endregion

    #region Look Methods

    private void HandleLook() {
        var lookDelta = new Vector2(lookInput.x * lookSensitivity.x, lookInput.y * lookSensitivity.y);

        UpdatePitch(lookDelta.y);
        UpdateYaw(lookDelta.x);
        UpdateTurnAnimation(lookDelta.x);
    }

    private void UpdatePitch(float pitchDelta) {
        CurrentPitch -= pitchDelta;
        fpCamera.transform.localRotation = Quaternion.Euler(CurrentPitch, 0f, 0f);
    }

    private void UpdateYaw(float yawDelta) {
        transform.Rotate(Vector3.up * yawDelta);
    }
    
    #endregion
    
    #region Gameplay Methods
    
    public void TakeDamage(float damage, Vector3? hitPoint = null, Vector3? hitNormal = null) {
        if(!IsOwner) return;
        
        _lastHitPoint = hitPoint;
        _lastHitNormal = hitNormal;
        
        health = Mathf.Round(Mathf.Max(0f, health - damage));
        if(_impulseSource) {
            _impulseSource.GenerateImpulse();
        } else {
            Debug.LogWarning("CinemachineImpulseSource not found on PlayerController!");
        }
        
        if(IsOwner && _hudManager)
            _hudManager.UpdateHealth(health, 100);
        
        if(hitPoint.HasValue && hitNormal.HasValue) {
            // weaponManager.ShowHitMarker(hitPoint.Value, hitNormal.Value);
        }

        if(health <= 0) {
            Die();
        }
    }
    
    private void Die() {
        deathCamera.EnableDeathCamera();
        
        Vector3? hitDirection = null;
        if(_lastHitNormal.HasValue) {
            hitDirection = -_lastHitNormal.Value;
        }

        playerRagdoll.EnableRagdoll(_lastHitPoint, hitDirection);
        
        // Implement respawn or game over logic here
    }
    
    public void Respawn() {
        health = 100f;
        if(_hudManager)
            _hudManager.UpdateHealth(health, 100f);
    
        // Disable ragdoll first
        if(playerRagdoll != null) {
            playerRagdoll.DisableRagdoll();
        }
    
        // Disable death camera
        if(deathCamera != null) {
            deathCamera.DisableDeathCamera();
        }
    
        // Get spawn position
        Vector3 spawnPosition;
        if(SpawnManager.Instance != null) {
            spawnPosition = SpawnManager.Instance.GetRandomSpawnPosition();
        } else {
            Debug.LogWarning("SpawnManager not found! Using default spawn position.");
            spawnPosition = new Vector3(0, 5, 0);
        }
    
        // CRITICAL: Disable CharacterController before moving
        if(characterController != null) {
            characterController.enabled = false;
        }
    
        // Move player
        transform.position = spawnPosition;
        transform.rotation = Quaternion.identity;
    
        // Reset velocity
        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = 0f;
    
        // Re-enable CharacterController
        if(characterController != null) {
            characterController.enabled = true;
        }
    
        Debug.Log($"Player respawned at {spawnPosition}");
    }
    
    #endregion
    
    #region Private Methods - Animation

    private void HandleLanding() {
        // Landing from a jump
        if(_isJumping && IsGrounded && _verticalVelocity <= 0f) {
            _isJumping = false;
            characterAnimator.SetBool(IsJumpingHash, false);
            characterAnimator.SetTrigger(LandTriggerHash);

            if(IsOwner) {
                SoundFXManager.Instance.PlayRandomSoundFX(landSounds, transform, false, "land");
            }
        }

        // Landing from a fall
        if(!_isFalling || !IsGrounded) return;
        characterAnimator.SetTrigger(LandTriggerHash);

        if(IsOwner) {
            SoundFXManager.Instance.PlayRandomSoundFX(landSounds, transform, false, "land");
        }
    }

    private void UpdateAnimator() {
        var localVelocity = transform.InverseTransformDirection(_horizontalVelocity);
        var isSprinting = _horizontalVelocity.magnitude > (WalkSpeed + 1f);

        var ray = Physics.Raycast(transform.position, Vector3.down, out var hit, 1f);
        Debug.DrawRay(transform.position, Vector3.down * 1f, Color.red);
        _isFalling = !IsGrounded && hit.distance > 3f;
        
        characterAnimator.SetFloat(MoveXHash, localVelocity.x / _maxSpeed, 0.1f, Time.deltaTime);
        characterAnimator.SetFloat(MoveYHash, localVelocity.z / _maxSpeed, 0.1f, Time.deltaTime);
        characterAnimator.SetBool(IsSprintingHash, isSprinting);
        characterAnimator.SetBool(IsFallingHash, _isFalling);
    }
    
    private void UpdateTurnAnimation(float yawDelta) {
        var turnSpeed = Mathf.Abs(yawDelta) > 0.001f ? Mathf.Clamp(yawDelta * 10f, -1f, 1f) : 0f;

        characterAnimator.SetFloat(LookXHash, turnSpeed, 0.1f, Time.deltaTime);
    }

    private void PlayRandomAudio(AudioClip[] clips, bool overlap, string soundType) {
        SoundFXManager.Instance.PlayRandomSoundFX(clips, transform, overlap, soundType);
    }
    
    #endregion
    
    #region Grapple Support Methods
    
    public void SetVelocity(Vector3 horizontalVelocity) {
        _horizontalVelocity = new Vector3(horizontalVelocity.x, 0f, horizontalVelocity.z);
    }
    
    public void AddVerticalVelocity(float verticalBoost) {
        _verticalVelocity += verticalBoost;
    }
    
    #endregion
}
