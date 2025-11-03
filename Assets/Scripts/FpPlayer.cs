using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class Player : MonoBehaviour
{
    #region Serialized Fields
    
    [Header("Components")]
    [SerializeField] private FpController fpController;
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private PauseMenuManager pauseMenuManager;
    
    [Header("Input Settings")]
    [SerializeField] private bool toggleSprint;
    [SerializeField] private bool toggleCrouch;
    
    
    #endregion
    
    #region Private Fields
    
    private int _currentWeaponIndex;
    private Weapon _currentWeapon;
    private GameObject _currentWeaponModel;
    
    #endregion
    
    #region Unity Methods

    private void OnValidate() {
        if(fpController == null) {
            fpController = GetComponent<FpController>();
        }

        if(weaponManager == null) {
            weaponManager = GetComponent<WeaponManager>();
        }

        if(fpCamera == null) {
            fpCamera = transform.GetComponentInChildren<CinemachineCamera>();
        }
    }

    private void Start() {
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;

        _currentWeaponIndex = weaponManager.currentWeaponIndex;
        _currentWeapon = weaponManager.CurrentWeapon;
        _currentWeaponModel = fpCamera.transform.GetChild(_currentWeaponIndex).gameObject;
    }

    private void LateUpdate() {
        if(Mouse.current.leftButton.isPressed && _currentWeapon.fireMode == "Full" && !pauseMenuManager.IsPaused) {
            _currentWeapon.Shoot();
        }
    }
    #endregion
    
    #region Movement

    private void OnLook(InputValue value) {
        if(pauseMenuManager.IsPaused) {
            fpController.lookInput = Vector2.zero;
            return;
        }
        
        fpController.lookInput = value.Get<Vector2>();
    }
    
    private void OnMove(InputValue value) {
        if(pauseMenuManager.IsPaused) {
            fpController.moveInput = Vector2.zero;
            return;
        }
        
        fpController.moveInput = value.Get<Vector2>();
    }

    private void OnSprint(InputValue value) {
        if(pauseMenuManager.IsPaused) {
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
        if(pauseMenuManager.IsPaused) {
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
        if(pauseMenuManager.IsPaused) return;
        
        fpController.TryJump();
    }

    private void OnScrollWheel(InputValue value) {
        if(pauseMenuManager.IsPaused) return;
        
        fpController.TryJump();
    }
    
    #endregion
    
    #region Weapons

    private void OnPrimary(InputValue value) {
        if(pauseMenuManager.IsPaused || _currentWeaponIndex == 0) return;
        
        if(weaponManager.equippedWeapons[_currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[_currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        if(pauseMenuManager.IsPaused || _currentWeaponIndex == 1) return;

        if(weaponManager.equippedWeapons[_currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[_currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        if(pauseMenuManager.IsPaused || _currentWeaponIndex == 2) return;
        
        if(weaponManager.equippedWeapons[_currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[_currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(2);
        Debug.Log("Tertiary");
    }
    
    private void SwitchWeapon(int weaponIndex) {
        _currentWeaponModel.SetActive(false);
        _currentWeaponIndex = weaponIndex;
        weaponManager.currentWeaponIndex = weaponIndex;
        _currentWeaponModel = fpCamera.transform.GetChild(_currentWeaponIndex).gameObject;
        _currentWeaponModel.SetActive(true);
    }

    private void OnAttack(InputValue value) {
        // if(optionsMenuManager.isPaused) return;
        
        // weaponManager.equippedWeapons[currentWeaponIndex].Shoot();
    }
    
    private void OnReload(InputValue value) {
        if(pauseMenuManager.IsPaused) return;
        
        weaponManager.equippedWeapons[_currentWeaponIndex].StartReload();
    }
    
    #endregion
    
    #region System

    private void OnPause(InputValue value) {
        pauseMenuManager.TogglePause();
    }
    
    private void OnTestDamage(InputValue value) {
        if(pauseMenuManager.IsPaused) return;
        Debug.Log("Taking 10 damage for testing.");
        fpController.TakeDamage(10);
    }
    
    #endregion
}
