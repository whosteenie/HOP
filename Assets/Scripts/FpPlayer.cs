using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class FpPlayer : NetworkBehaviour
{
    #region Serialized Fields
    
    [Header("Components")]
    [SerializeField] private FpController fpController;
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
        
        weaponManager.InitializeWeapons(fpCamera, fpController, FindFirstObjectByType<HUDManager>());

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
        
        if(Mouse.current.leftButton.isPressed && _currentWeapon.fireMode == "Full" && !_pauseMenuManager.IsPaused) {
            _currentWeapon.Shoot();
        }

        weaponManager.CurrentWeapon.UpdateDamageMultiplier();
        _hudManager.UpdateMultiplier(weaponManager.CurrentWeapon.CurrentDamageMultiplier, weaponManager.CurrentWeapon.maxDamageMultiplier);
    }
    #endregion
    
    #region Movement

    private void OnLook(InputValue value) {
        if(_pauseMenuManager.IsPaused) {
            fpController.lookInput = Vector2.zero;
            return;
        }
        
        fpController.lookInput = value.Get<Vector2>();
    }
    
    private void OnMove(InputValue value) {
        if(_pauseMenuManager.IsPaused) {
            fpController.moveInput = Vector2.zero;
            return;
        }
        
        fpController.moveInput = value.Get<Vector2>();
    }

    private void OnSprint(InputValue value) {
        if(_pauseMenuManager.IsPaused) {
            fpController.sprintInput = false;
            return;
        }
        
        if(toggleSprint) {
            // Toggle mode
            fpController.sprintInput = !fpController.sprintInput;
        } else {
            // Hold mode
            fpController.sprintInput = value.isPressed;
        }
    }
    
    private void OnCrouch(InputValue value) {
        if(_pauseMenuManager.IsPaused) {
            fpController.crouchInput = false;
            return;
        }
        
        if(toggleCrouch) {
            // Toggle mode
            fpController.crouchInput = !fpController.crouchInput;
        } else {
            // Hold mode
            fpController.crouchInput = value.isPressed;
        }
    }
    
    private void OnJump(InputValue value) {
        if(_pauseMenuManager.IsPaused) return;
        
        fpController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }

    private void OnScrollWheel(InputValue value) {
        if(_pauseMenuManager.IsPaused) return;
        
        fpController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }
    
    private void OnGrapple(InputValue value)
    {
        if(_pauseMenuManager.IsPaused) return;

        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        } else {
            grappleController.TryGrapple();
        }
    }
    
    #endregion
    
    #region Weapons

    private void OnPrimary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 0) return;
        
        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 1) return;

        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        if(_pauseMenuManager.IsPaused || _currentWeaponIndex == 2) return;
        
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
        _currentWeapon.BindAndResolve(fpCamera, fpController, weaponManager, FindFirstObjectByType<HUDManager>());
        _currentWeapon.gameObject.SetActive(true);
    }
    
    private void OnReload(InputValue value) {
        if(_pauseMenuManager.IsPaused) return;
        
        weaponManager.CurrentWeapon.StartReload();
    }
    
    #endregion
    
    #region System

    private void OnPause(InputValue value) {
        _pauseMenuManager.TogglePause();
    }
    
    private void OnTestDamage(InputValue value) {
        if(_pauseMenuManager.IsPaused) return;
        Debug.Log("Taking 10 damage for testing.");
        fpController.TakeDamage(10);
    }
    
    #endregion
}
