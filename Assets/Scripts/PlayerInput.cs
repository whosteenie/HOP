using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class PlayerInput : NetworkBehaviour
{
    #region Serialized Fields
    
    [FormerlySerializedAs("fpController")]
    [Header("Components")]
    [SerializeField] private PlayerController playerController;
    [SerializeField] private DeathCamera deathCamera;
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private GrappleController grappleController;
    
    [Header("Input Settings")]
    [SerializeField] private bool toggleSprint;
    [SerializeField] private bool toggleCrouch;
    
    #endregion
    
    #region Private Fields
    
    private PauseMenuManager _pauseMenuManager;
    private HUDManager _hudManager;
    private int _currentWeaponIndex;
    private Weapon _currentWeapon;
    
    #endregion
    
    #region Unity Methods

    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();
        
        if(!IsOwner) return;
        
        weaponManager.InitializeWeapons(fpCamera, playerController, FindFirstObjectByType<HUDManager>());

        _currentWeaponIndex = weaponManager.currentWeaponIndex;
        _currentWeapon = weaponManager.CurrentWeapon;
    }

    private void Start() {
        if(!IsOwner) return;
        
        _pauseMenuManager = FindFirstObjectByType<PauseMenuManager>();
        _hudManager = FindFirstObjectByType<HUDManager>();
        
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
        
        _currentWeaponIndex = weaponManager.currentWeaponIndex;
        _currentWeapon = weaponManager.CurrentWeapon;
    }

    private void LateUpdate() {
        if(!IsOwner) return;
        
        if(Mouse.current.leftButton.isPressed && _currentWeapon.fireMode == "Full" && !_pauseMenuManager.IsPaused && !playerController.IsDead) {
            _currentWeapon.Shoot();
        }

        weaponManager.CurrentWeapon.UpdateDamageMultiplier();
        _hudManager.UpdateMultiplier(weaponManager.CurrentWeapon.CurrentDamageMultiplier, weaponManager.CurrentWeapon.maxDamageMultiplier);
    }
    #endregion
    
    #region Movement

    private void OnLook(InputValue value) {
        if(_pauseMenuManager.IsPaused) {
            playerController.lookInput = Vector2.zero;
            return;
        }

        if(playerController.IsDead) {
            deathCamera.lookInput = value.Get<Vector2>();
        } else {
            playerController.lookInput = value.Get<Vector2>();
        }

    }
    
    private void OnMove(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) {
            playerController.moveInput = Vector2.zero;
            return;
        }
        
        playerController.moveInput = value.Get<Vector2>();
    }

    private void OnSprint(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) {
            playerController.sprintInput = false;
            return;
        }
        
        if(toggleSprint) {
            // Toggle mode
            playerController.sprintInput = !playerController.sprintInput;
        } else {
            // Hold mode
            playerController.sprintInput = value.isPressed;
        }
    }
    
    private void OnCrouch(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) {
            playerController.crouchInput = false;
            return;
        }
        
        if(toggleCrouch) {
            // Toggle mode
            playerController.crouchInput = !playerController.crouchInput;
        } else {
            // Hold mode
            playerController.crouchInput = value.isPressed;
        }
    }
    
    private void OnJump(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }

    private void OnScrollWheel(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }
    
    private void OnGrapple(InputValue value)
    {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) return;

        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        } else {
            grappleController.TryGrapple();
        }
    }
    
    #endregion
    
    #region Weapons

    private void OnPrimary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 0 || playerController.IsDead) return;
        
        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 1 || playerController.IsDead) return;

        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 2 || playerController.IsDead) return;
        
        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(2);
        Debug.Log("Tertiary");
    }
    
    private void SwitchWeapon(int weaponIndex) {
        _currentWeapon.gameObject.SetActive(false);
        weaponManager.currentWeaponIndex = weaponIndex;
        _currentWeaponIndex = weaponIndex;
        _currentWeapon.BindAndResolve(fpCamera, playerController, weaponManager, _hudManager);
        _currentWeapon.gameObject.SetActive(true);
    }
    
    private void OnReload(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) return;
        
        weaponManager.CurrentWeapon.StartReload();
    }
    
    #endregion
    
    #region System

    private void OnPause(InputValue value) {
        _pauseMenuManager.TogglePause();
    }
    
    private void OnTestDamage(InputValue value) {
        if(_pauseMenuManager.IsPaused || playerController.IsDead) return;
        Debug.Log("Taking 10 damage for testing.");
        playerController.TakeDamage(10);
    }

    private void OnTestRespawn(InputValue value) {
        if(_pauseMenuManager.IsPaused || !playerController.IsDead) return;
        Debug.Log("Respawning player for testing.");
        playerController.Respawn();
    }
    
    #endregion
}
