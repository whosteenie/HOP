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
    [SerializeField] private AudioListener audioListener;
    [SerializeField] private WeaponManager weaponManager;
    [SerializeField] private GrappleController grappleController;
    
    [Header("Input Settings")]
    [SerializeField] private bool toggleSprint;
    [SerializeField] private bool toggleCrouch;
    
    #endregion
    
    #region Private Fields
    
    private GameMenuManager _gameMenuManager;
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
            
            audioListener.enabled = false;
            
            return;
        }
        
        if(fpCamera != null) {
            fpCamera.gameObject.SetActive(true);
            // fpCamera.Priority = 100; // Make sure it's the active camera
        }

        _gameMenuManager = GameMenuManager.Instance;
        _hudManager = HUDManager.Instance;
        
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

        if(_gameMenuManager == null) {
            _gameMenuManager = GameMenuManager.Instance;
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
        if(!IsOwner) return;
        if(weaponManager.CurrentWeapon == null) return;
        
        if(_gameMenuManager != null && !_gameMenuManager.IsPaused && 
           (Mouse.current.leftButton.isPressed || Mouse.current.rightButton.isPressed) && 
           weaponManager.CurrentWeapon.fireMode == "Full" && !playerController.netIsDead.Value) {
            Debug.Log("SHOOTING");
            _currentWeapon.Shoot();
        }

        if(_gameMenuManager != null && !_gameMenuManager.IsPaused &&
           Mouse.current.scroll.value.magnitude > 0f && !playerController.netIsDead.Value) {
            playerController.TryJump();
            grappleController.CancelGrapple();
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
        if(_gameMenuManager != null && _gameMenuManager.IsPaused) {
            playerController.lookInput = Vector2.zero;
            return;
        }

        if(playerController.netIsDead.Value) {
            deathCamera.lookInput = value.Get<Vector2>();
        } else {
            playerController.lookInput = value.Get<Vector2>();
        }

    }
    
    private void OnMove(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) {
            playerController.moveInput = Vector2.zero;
            return;
        }
        
        playerController.moveInput = value.Get<Vector2>();
    }

    private void OnSprint(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) {
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
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) {
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
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }

    private void OnScrollWheel(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) return;
        
        playerController.TryJump();
        
        if(grappleController.IsGrappling) {
            grappleController.CancelGrapple();
        }
    }
    
    private void OnGrapple(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) return;

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
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || _currentWeaponIndex == 0 || playerController.netIsDead.Value) return;
        
        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(0);
        Debug.Log("Primary");
    }
    
    private void OnSecondary(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || _currentWeaponIndex == 1 || playerController.netIsDead.Value) return;

        if(weaponManager.CurrentWeapon.IsReloading) {
            weaponManager.CurrentWeapon.CancelReload();
        }
        
        SwitchWeapon(1);
        Debug.Log("Secondary");
    }
    
    private void OnTertiary(InputValue value) {
        // if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || _currentWeaponIndex == 2 || playerController.netIsDead.Value) return;
        
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
        if(_gameMenuManager != null && _gameMenuManager.IsPaused || playerController.netIsDead.Value) return;

        if(weaponManager.CurrentWeapon) {
            weaponManager.CurrentWeapon.StartReload();
        }
    }
    
    #endregion
    
    #region System

    private void OnPause(InputValue value) {
        if(!IsOwner) return;
        if(_gameMenuManager)
            _gameMenuManager.TogglePause();
    }

    private void OnTestRespawn(InputValue value) {
        if(!IsOwner) return;
        if(_gameMenuManager != null && _gameMenuManager.IsPaused) return;
        Debug.Log("Respawning player for testing.");
        // playerController.Respawn();
        playerController.RequestRespawnServerRpc();
    }
    
    #endregion
}
