using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class Player : MonoBehaviour
{
    [Header("Components")]
    [SerializeField] private FpController fpController;
    [SerializeField] private CinemachineCamera fpCamera;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private PauseMenuManager pauseMenuManager;
    
    [Header("Input Settings")]
    [SerializeField] private bool toggleSprint;
    [SerializeField] private bool toggleCrouch;
    
    [FormerlySerializedAs("currentWeapon")]
    [Header("Private Fields")]
    [SerializeField] private int currentWeaponIndex;
    [SerializeField] private GameObject currentWeaponModel;
    public bool isShooting;
    
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

        currentWeaponIndex = weaponManager.currentWeaponIndex;
        currentWeaponModel = fpCamera.transform.GetChild(currentWeaponIndex).gameObject;
    }

    private void LateUpdate() {
        isShooting = Mouse.current.leftButton.isPressed;
        if(isShooting && !pauseMenuManager.IsPaused && weaponManager.equippedWeapons[currentWeaponIndex].fireMode == "Full") {
            weaponManager.equippedWeapons[currentWeaponIndex].Shoot();
        }
    }
    #endregion
    
    #region Input Handling

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

    private void OnPrimary(InputValue value) {
        if(pauseMenuManager.IsPaused || currentWeaponIndex == 0) return;
        
        if(weaponManager.equippedWeapons[currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        if(pauseMenuManager.IsPaused || currentWeaponIndex == 1) return;

        if(weaponManager.equippedWeapons[currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        if(pauseMenuManager.IsPaused || currentWeaponIndex == 2) return;
        
        if(weaponManager.equippedWeapons[currentWeaponIndex].IsReloading) {
            weaponManager.equippedWeapons[currentWeaponIndex].CancelReload();
        }
        
        SwitchWeapon(2);
        Debug.Log("Tertiary");
    }
    
    private void SwitchWeapon(int weaponIndex) {
        currentWeaponModel.SetActive(false);
        currentWeaponIndex = weaponIndex;
        weaponManager.currentWeaponIndex = weaponIndex;
        currentWeaponModel = fpCamera.transform.GetChild(currentWeaponIndex).gameObject;
        currentWeaponModel.SetActive(true);
    }

    private void OnAttack(InputValue value) {
        // if(optionsMenuManager.isPaused) return;
        
        // weaponManager.equippedWeapons[currentWeaponIndex].Shoot();
    }
    
    private void OnReload(InputValue value) {
        if(pauseMenuManager.IsPaused) return;
        
        weaponManager.equippedWeapons[currentWeaponIndex].StartReload();
    }

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
