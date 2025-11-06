using System;
using System.Collections;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

public class PlayerInput : NetworkBehaviour
{
    #region Serialized Fields
    
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

        if(!IsOwner) {
            if(fpCamera != null) {
                fpCamera.gameObject.SetActive(false);
            }
            
            return;
        }
        
        if(fpCamera != null) {
            fpCamera.gameObject.SetActive(true);
            fpCamera.Priority = 100; // Make sure it's the active camera
        }

        _pauseMenuManager = PauseMenuManager.Instance;
        _hudManager = HUDManager.Instance;
        
        weaponManager.InitializeWeapons(fpCamera, playerController, _hudManager);
        _currentWeaponIndex = weaponManager.currentWeaponIndex;
        _currentWeapon = weaponManager.CurrentWeapon;

        StartCoroutine(InitializeAfterSceneLoad());
    }

    private IEnumerator InitializeAfterSceneLoad() {
        yield return new WaitForEndOfFrame();
        
        if(!IsOwner) {
            Debug.Log("Not owner, skipping weapon initialization");
            yield break;
        }

        if(_pauseMenuManager == null) {
            _pauseMenuManager = PauseMenuManager.Instance;
        }

        if(_hudManager == null) {
            _hudManager = HUDManager.Instance;
        }
        
        if(fpCamera == null) {
            Debug.LogError("FP Camera is null!");
            yield break;
        }
        
        if(_hudManager != null) {
            weaponManager.InitializeWeapons(fpCamera, playerController, _hudManager);
            _currentWeaponIndex = weaponManager.currentWeaponIndex;
            _currentWeapon = weaponManager.CurrentWeapon;
            Debug.Log("Weapons initialized successfully!");
        } else {
            Debug.LogError("HUDManager not found! Weapons cannot be initialized.");
        }
    }

    private void Start() {
        if(!IsOwner) return;
        
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.Locked;
    }

    private void LateUpdate() {
        // if(!IsOwner) return;
        if(weaponManager.CurrentWeapon == null) return;
        
        if(_pauseMenuManager != null && !_pauseMenuManager.IsPaused && 
           (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed) && 
           weaponManager.CurrentWeapon.fireMode == "Full" && !playerController.IsDead) {
            _currentWeapon.Shoot();
        }

        if(_pauseMenuManager != null && !_pauseMenuManager.IsPaused &&
           Mouse.current.scroll.value.magnitude > 0f && !playerController.IsDead) {
            playerController.TryJump();
        }
        
        if(weaponManager.CurrentWeapon)
            weaponManager.CurrentWeapon.UpdateDamageMultiplier();
        
        if(_hudManager && weaponManager.CurrentWeapon)
            _hudManager.UpdateMultiplier(weaponManager.CurrentWeapon.CurrentDamageMultiplier, weaponManager.CurrentWeapon.maxDamageMultiplier);
    }
    #endregion
    
    #region Movement

    private void OnLook(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused) {
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
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) {
            playerController.moveInput = Vector2.zero;
            return;
        }
        
        playerController.moveInput = value.Get<Vector2>();
    }

    private void OnSprint(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) {
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
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) {
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
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }

    private void OnScrollWheel(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }
    
    private void OnGrapple(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) return;

        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        } else {
            grappleController.TryGrapple();
        }
    }
    
    #endregion
    
    #region Weapons

    private void OnPrimary(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || _currentWeaponIndex == 0 || playerController.IsDead) return;
        
        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || _currentWeaponIndex == 1 || playerController.IsDead) return;

        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || _currentWeaponIndex == 2 || playerController.IsDead) return;
        
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
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) return;

        if(weaponManager.CurrentWeapon) {
            weaponManager.CurrentWeapon.StartReload();
        }
    }
    
    #endregion
    
    #region System

    private void OnPause(InputValue value) {
        if(!IsOwner) return;
        if(_pauseMenuManager)
            _pauseMenuManager.TogglePause();
    }
    
    private void OnTestDamage(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || playerController.IsDead) return;
        Debug.Log("Taking 10 damage for testing.");
        playerController.TakeDamage(10);
    }

    private void OnTestRespawn(InputValue value) {
        // if(!IsOwner) return;
        if(_pauseMenuManager != null && _pauseMenuManager.IsPaused || !playerController.IsDead) return;
        Debug.Log("Respawning player for testing.");
        playerController.Respawn();
    }
    
    #endregion
}
