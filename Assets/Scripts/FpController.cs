using System;
using UnityEngine;
using Unity.Cinemachine;
using Unity.Netcode;

public class FpController : NetworkBehaviour {
    #region Constants
    
    private const float WalkSpeed = 5f;
    public const float SprintSpeed = 10f;
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

    [Header("Look Parameters")]
    [SerializeField] public Vector2 lookSensitivity = new (0.1f, 0.1f);
    
    [Header("Components")]
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private CharacterController characterController;
    [SerializeField] private Animator characterAnimator;
    // [SerializeField] private HUDManager hudManager;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private GrappleController grappleController;

    [Header("Player Fields")]
    [SerializeField] private int health = 100;
    
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
    
    #endregion
    
    #region Private Properties
    
    public Vector3 CurrentVelocity => _horizontalVelocity;
    public Vector3 CurrentFullVelocity => new(_horizontalVelocity.x, _verticalVelocity, _horizontalVelocity.z);
    private bool IsGrounded => characterController.isGrounded;
    
    private float CurrentPitch {
        get => _currentPitch;

        set => _currentPitch = Mathf.Clamp(value, -PitchLimit, PitchLimit);
    }
    
    #endregion
    
    #region Unity Lifecycle

    private void OnValidate() {
        if(characterController == null) {
            characterController = GetComponent<CharacterController>();
        }
    }

    private void Start() {
        if(!IsOwner) return;

        _hudManager = FindFirstObjectByType<HUDManager>();
        _impulseSource = FindFirstObjectByType<CinemachineImpulseSource>();
    }

    private void Update() {
        if(!IsOwner) return;
        
        HandleLook();
        HandleMovement();
        HandleCrouch();
        HandleLanding();
        UpdateAnimator();
        
        if(transform.position.y <= 0f) {
            TakeDamage(health);
        }
    }
    
    #endregion

    #region Public Methods
    
    public void TryJump(float height = JumpHeight) {
        if(!IsGrounded) {
            return;
        }

        height = CheckForJumpPad() ? 15f : height;
        
        _verticalVelocity = Mathf.Sqrt(height * -2f * Physics.gravity.y * GravityScale);
        characterAnimator.SetTrigger(JumpTriggerHash);
        characterAnimator.SetBool(IsJumpingHash, true);
        _isJumping = true;
        PlayRandomAudio(jumpSounds);
    }
    
    public void PlayWalkSound() {
        if(!IsGrounded) return;
        PlayRandomAudio(walkSounds);
    }
    
    public void PlayRunSound() {
        if(!IsGrounded) return;
        PlayRandomAudio(runSounds);
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
    
    #region Gameplay Methods - WIP
    
    public void TakeDamage(int damage) {
        health = Mathf.Max(0, health - damage);
        _impulseSource.GenerateImpulse();
        _hudManager.UpdateHealth(health, 100);
        
        Debug.Log($"Player took {damage} damage. Current health: {health}");

        if(health <= 0) {
            Die();
        }
    }
    
    private void Die() {
        Debug.Log("Player has died.");
        // Implement respawn or game over logic here
    }
    
    #endregion
    
    #region Private Methods - Animation

    private void HandleLanding() {
        // Landing from a jump
        if(_isJumping && IsGrounded) {
            _isJumping = false;
            characterAnimator.SetBool(IsJumpingHash, false);
            characterAnimator.SetTrigger(LandTriggerHash);
            PlayRandomAudio(landSounds);
        }

        // Landing from a fall
        if(!_isFalling || !IsGrounded) return;
        characterAnimator.SetTrigger(LandTriggerHash);
        PlayRandomAudio(landSounds);
    }

    private void UpdateAnimator() {
        var localVelocity = transform.InverseTransformDirection(_horizontalVelocity);

        _isFalling = !IsGrounded && _verticalVelocity < -4.5f;
        
        characterAnimator.SetFloat(MoveXHash, localVelocity.x / _maxSpeed, 0.1f, Time.deltaTime);
        characterAnimator.SetFloat(MoveYHash, localVelocity.z / _maxSpeed, 0.1f, Time.deltaTime);
        characterAnimator.SetBool(IsSprintingHash, sprintInput);
        characterAnimator.SetBool(IsFallingHash, _isFalling);
    }
    
    private void UpdateTurnAnimation(float yawDelta) {
        var turnSpeed = Mathf.Abs(yawDelta) > 0.001f ? Mathf.Clamp(yawDelta * 10f, -1f, 1f) : 0f;

        characterAnimator.SetFloat(LookXHash, turnSpeed, 0.1f, Time.deltaTime);
    }

    private void PlayRandomAudio(AudioClip[] clips) {
        SoundFXManager.Instance.PlayRandomSoundFX(clips, transform);
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
