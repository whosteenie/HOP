using System;
using System.Collections.Generic;
using Game.Player.Core;
using Game.Player.Hopball;
using Network.Core;
using OSI;
using Network.AntiCheat;
using Network.Diagnostics;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Hopball {
    public class HopballController : NetworkBehaviour {
    private bool EnsureServerAuthority(string action) {
        if(HasHopballAuthority) return true;
        AntiCheatLogger.LogAuthorityViolation($"Hopball.{action}", OwnerClientId);
        return false;
    }
    public static HopballController Instance { get; private set; }

    public readonly int IntensityID = Shader.PropertyToID("_EmissionIntensity");
    public readonly int DissolveAmountID = Shader.PropertyToID("_DissolveAmount");

    private const float EnergySmoothing = 3.5f;
    private const float DissolveSmoothing = 2f;
    private const float MaxEnergy = 20;
    private const float VisualWriteEpsilon = 0.0001f;
    private readonly Vector3 _maxEffectScale = new(0.45f, 0.45f, 0.45f);
    private readonly Vector3 _minEffectScale = new(0.23f, 0.23f, 0.23f);

    // Network-synced energy (server-authoritative)
    private readonly NetworkVariable<float> _networkEnergy = new(value: MaxEnergy);

    private float _nextDrainAt = -1f;

    [Header("World Model Components (on this prefab)")]
    [SerializeField] private MeshRenderer meshRenderer;

    [SerializeField] private Transform effects;
    [SerializeField] private Light effectLight;
    [SerializeField] private Target target;
    [SerializeField] private Collider hopballCollider; // Collider to disable when equipped
    [SerializeField] private Rigidbody hopballRigidbody;
    [SerializeField] private GameObject godrayEffect;

    private NetworkTransform _networkTransform;
    private PlayerHopballController _equippedController; // Store reference to controller when equipped
    private readonly HashSet<Collider> _ignoredPlayerColliders = new();
    private bool _isIgnoringPlayerCollisions;

    public float Energy => _networkEnergy.Value;
    public float VisualEnergyRatio => Mathf.Clamp01(_displayEnergy / MaxEnergy);
    public bool IsEquipped { get; private set; }
    public bool IsDropped { get; private set; }

    public PlayerController HolderController { get; private set; }
    public Rigidbody Rigidbody => hopballRigidbody;

    private float DissolveAmount { get; set; }
    private float _displayEnergy = MaxEnergy;
    private Vector3 _lastAppliedEffectScale = new(float.NaN, float.NaN, float.NaN);
    private float _lastAppliedLightIntensity = float.NaN;
    private float _lastAppliedEmissionIntensity = float.NaN;
    private float _lastAppliedDissolveAmount = float.NaN;
    public HopballVisualState CurrentVisualState { get; private set; }

    public bool IsDissolving { get; private set; }

    public bool IsAwaitingRespawn { get; private set; }
    private bool HasHopballAuthority => NetworkAuthority.HasGlobalAuthority(this);
    private bool _sessionOwnerCallbacksRegistered;

    /// <summary>
    /// Gets the current emission intensity from the world hopball material.
    /// Returns 0 if material is not available or renderer is disabled.
    /// </summary>
    private float CurrentEmissionIntensity => meshRenderer.material.GetFloat(IntensityID);

    /// <summary>
    /// Gets the current effect scale from the world hopball effects transform.
    /// Returns zero vector if effects are not available or if dissolving.
    /// During dissolve, returns zero to ensure FP visuals don't show effects.
    /// </summary>
    private Vector3 CurrentEffectScale => IsDissolving ? Vector3.zero : effects.localScale;

    /// <summary>
    /// Gets the current light intensity from the world hopball light.
    /// Returns 0 if light is not available, disabled, or if dissolving.
    /// During dissolve, returns zero to ensure FP visuals don't show effects.
    /// </summary>
    private float CurrentLightIntensity => IsDissolving ? 0f : effectLight.intensity;

    [System.Flags]
    private enum HopballStateFlags : byte {
        HideReal = 1 << 0,
        ShowRealDropped = 1 << 1,
        ShowRealImmediate = 1 << 2,
        CleanupVisuals = 1 << 3
    }

    private struct HopballStateUpdate : INetworkSerializable {
        public HopballStateFlags Flags;
        public bool TargetStateSpecified;
        public bool TargetEnabled;
        public bool PositionSpecified;
        public Vector3 Position;
        public Quaternion Rotation;
        /// <summary>When true, the client with this ID should run full cleanup+restore (DA-compatible path).</summary>
        public bool DissolveHolderClientIdSpecified;
        public ulong DissolveHolderClientId;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
            serializer.SerializeValue(ref Flags);
            serializer.SerializeValue(ref TargetStateSpecified);
            serializer.SerializeValue(ref TargetEnabled);
            serializer.SerializeValue(ref PositionSpecified);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref DissolveHolderClientIdSpecified);
            serializer.SerializeValue(ref DissolveHolderClientId);
        }
    }

    public delegate void HopballVisualStateChanged(HopballVisualState state);

    public static event HopballVisualStateChanged VisualStateChanged;

    public readonly struct HopballVisualState {
        public readonly Vector3 EffectScale;
        public readonly float LightIntensity;
        public readonly float EmissionIntensity;
        public readonly float DissolveAmount;
        public readonly bool TargetEnabled;

        public HopballVisualState(Vector3 effectScale, float lightIntensity, float emissionIntensity,
            float dissolveAmount, bool targetEnabled) {
            EffectScale = effectScale;
            LightIntensity = lightIntensity;
            EmissionIntensity = emissionIntensity;
            DissolveAmount = dissolveAmount;
            TargetEnabled = targetEnabled;
        }
    }

    public override void OnNetworkSpawn() {
        base.OnNetworkSpawn();
        NetworkAuthority.TryConfigureSessionOwnerObject(this);
        RegisterSessionOwnerCallbacks();
        
        // Cache NetworkTransform reference
        if(_networkTransform == null) {
            _networkTransform = GetComponent<NetworkTransform>();
        }

        // Set singleton instance
        Instance = this;

        _networkEnergy.OnValueChanged += OnEnergyChanged;

        // Reset all state to initial spawn state
        ResetToInitialState();

        // Ensure root GameObject is active
        gameObject.SetActive(true);

        // Initialize energy display
        _displayEnergy = MaxEnergy;
        _nextDrainAt = -1f;
        InvalidateVisualCache();
        UpdateEffects(_displayEnergy);
        NotifyVisualStateChanged(true);

        // Set up dropped visuals initially (since hopball spawns dropped)
        SetupDroppedVisuals(isDrop: false); // Respawn - keep godray enabled

        foreach(var controller in PlayerHopballController.Instances) {
            OnControllerRegistered(controller);
        }
    }

    public override void OnNetworkDespawn() {
        base.OnNetworkDespawn();

        // Clear singleton instance
        if(Instance == this) {
            Instance = null;
        }

        _networkEnergy.OnValueChanged -= OnEnergyChanged;
        UnregisterSessionOwnerCallbacks();
    }

    private void RegisterSessionOwnerCallbacks() {
        if(_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
        NetworkManager.OnSessionOwnerPromoted += OnSessionOwnerPromoted;
        _sessionOwnerCallbacksRegistered = true;
    }

    private void UnregisterSessionOwnerCallbacks() {
        if(!_sessionOwnerCallbacksRegistered || NetworkManager == null) return;
        NetworkManager.OnSessionOwnerPromoted -= OnSessionOwnerPromoted;
        _sessionOwnerCallbacksRegistered = false;
    }

    private void OnSessionOwnerPromoted(ulong _) {
        if(!HasHopballAuthority) {
            return;
        }

        NetworkAuthority.TryConfigureSessionOwnerObject(this);
        _nextDrainAt = -1f;

        // Holder/controller references are local-only state. After migration, treat the ball as needing a clean respawn.
        if(!IsAwaitingRespawn && HopballSpawnManager.Instance != null && (IsEquipped || HolderController == null && !IsDropped)) {
            HopballSpawnManager.Instance.RespawnAtNewLocation();
        }
    }

    private void OnEnergyChanged(float previous, float current) {
        // Award scoring points as energy depletes (server only, while equipped)
        if(!HasHopballAuthority || !IsEquipped || !(previous > current)) return;
        var energyDepleted = previous - current;
        HopballSpawnManager.Instance.OnEnergyDepleted(_equippedController.OwnerClientId, energyDepleted);
    }

    private void Update() {
        if(!IsSpawned) return;

        switch(HasHopballAuthority) {
            // Server handles energy drain (only while equipped, unless dissolving)
            // If dissolving, continue draining even if dropped to complete the dissolve
            case true when IsEquipped || IsDissolving: {
                if(_nextDrainAt < 0f) {
                    _nextDrainAt = Time.time + 2f;
                }

                while(Time.time >= _nextDrainAt) {
                    var newEnergy = Mathf.Max(0f, _networkEnergy.Value - 1f);
                    _networkEnergy.Value = newEnergy;
                    _nextDrainAt += 2f;
                }

                break;
            }
            case true:
                _nextDrainAt = -1f;
                break;
        }

        // Update effects on all clients every frame (for visual syncing)
        // Effects now only update when state actually changes; no per-frame visual polling required

        // Handle dissolve effect when energy is 0
        if(_networkEnergy.Value <= 0 && !IsDissolving && !IsAwaitingRespawn) {
            IsDissolving = true;
            FlowLog.Emit(FlowEventIds.HopballDissolveStarted,
                ("hopballNetId", NetworkObjectId),
                ("energy", _networkEnergy.Value),
                ("holder", HolderController != null ? HolderController.OwnerClientId.ToString() : "None"));
            // Set effects scale to 0 immediately before starting dissolve
            // This ensures effects recede into the ball surface and aren't visible during dissolve
            // Only call ClientRpc from server (ClientRpcs can only be called from server)
            if(HasHopballAuthority) {
                SetEffectsScaleZeroClientRpc();
            } else {
                // On clients, set effects scale to 0 locally
                SetEffectsScaleToZero();
            }
        }

        // Smoothly interpolate display energy every frame for visual effects
        SmoothDisplayEnergy(Time.deltaTime);

        if(IsAwaitingRespawn) {
            return;
        }

        if(IsDissolving) {
            HandleDissolve();
        } else if(DissolveAmount > 0f) {
            DissolveAmount = 0f;
            if(ApplyDissolveAmount(DissolveAmount)) {
                NotifyVisualStateChanged(false);
            }
        }
    }

    private void SmoothDisplayEnergy(float deltaTime) {
        if(Mathf.Approximately(_displayEnergy, _networkEnergy.Value)) return;
        _displayEnergy = Mathf.Lerp(_displayEnergy, _networkEnergy.Value,
            1f - Mathf.Exp(-EnergySmoothing * deltaTime));
        UpdateEffects(_displayEnergy);
    }

    private void UpdateEffects(float energy) {
        var energyRatio = energy > 0 ? energy / MaxEnergy : 0f;
        var targetScale = Vector3.Lerp(_minEffectScale, _maxEffectScale, energyRatio);
        var changed = false;

        if(effects != null && HasSignificantDelta(_lastAppliedEffectScale, targetScale)) {
            effects.localScale = targetScale;
            _lastAppliedEffectScale = targetScale;
            changed = true;
        }

        if(effectLight != null && HasSignificantDelta(_lastAppliedLightIntensity, energyRatio)) {
            effectLight.intensity = energyRatio;
            _lastAppliedLightIntensity = energyRatio;
            changed = true;
        }

        if(meshRenderer != null && HasSignificantDelta(_lastAppliedEmissionIntensity, energyRatio)) {
            meshRenderer.material.SetFloat(IntensityID, energyRatio);
            _lastAppliedEmissionIntensity = energyRatio;
            changed = true;
        }

        if(changed) {
            NotifyVisualStateChanged(false);
        }
    }

    /// <summary>
    /// Called by HopballController when ball is equipped.
    /// </summary>
    public void SetEquipped(bool equipped, PlayerHopballController controller = null) {
        if(!EnsureServerAuthority(nameof(SetEquipped))) return;
        IsEquipped = equipped;
        IsDropped = false;

        if(equipped) {
            BroadcastStateUpdate(new HopballStateUpdate {
                Flags = HopballStateFlags.HideReal,
                TargetStateSpecified = true,
                TargetEnabled = false
            });

            // Reset dissolve amount and dissolve state when equipped
            DissolveAmount = 0f;
            ApplyDissolveAmount(DissolveAmount);

            IsDissolving = false;
            _equippedController = controller;
            HolderController = controller != null ? controller.PlayerController : null;
            _nextDrainAt = -1f;
        } else {
            _equippedController = null;
            HolderController = null;
        }
        FlowLog.Emit(FlowEventIds.HopballHoldStateChanged,
            ("hopballNetId", NetworkObjectId),
            ("isEquipped", IsEquipped),
            ("holder", HolderController != null ? HolderController.OwnerClientId.ToString() : "None"));
    }

    /// <summary>
    /// Respawns the hopball at a new location with full energy.
    /// Called by HopballSpawnManager after dissolve completes.
    /// </summary>
    public void RespawnAtLocation(Vector3 position, Quaternion rotation) {
        if(!EnsureServerAuthority(nameof(RespawnAtLocation))) return;
        // Clear equipped state FIRST to ensure controllers know they're not holding it
        // Also disable the previous holder's Target indicator and clean up visuals
        ulong? dissolveHolderId = null;
        if(_equippedController != null) {
            var controller = _equippedController; // Cache before clearing
            dissolveHolderId = controller.OwnerClientId;
            controller.ClearHopballReference();
            controller.OnHopballReleasedClientRpc();
            _equippedController = null;
            HolderController = null;
        }

        IsEquipped = false;

        // Position at new location
        var hopballTransform = transform;
        hopballTransform.position = position;
        hopballTransform.rotation = rotation;

        // Ensure unparented
        transform.SetParent(null);

        IsAwaitingRespawn = false;
        ResetToInitialState();

        var update = new HopballStateUpdate {
            Flags = HopballStateFlags.CleanupVisuals | HopballStateFlags.ShowRealImmediate,
            TargetStateSpecified = true,
            TargetEnabled = false,
            PositionSpecified = true,
            Position = position,
            Rotation = rotation
        };
        if(dissolveHolderId.HasValue) {
            update.DissolveHolderClientIdSpecified = true;
            update.DissolveHolderClientId = dissolveHolderId.Value;
        }
        BroadcastStateUpdate(update);
    }

    /// <summary>
    /// Repositions the hopball at a location (for OOB handling).
    /// Retains current energy.
    /// </summary>
    public void RepositionAtLocation(Vector3 position, Quaternion rotation) {
        if(!EnsureServerAuthority(nameof(RepositionAtLocation))) return;
        // Just move it, don't reset energy
        var hopballTransform = transform;
        hopballTransform.position = position;
        hopballTransform.rotation = rotation;

        // Ensure unparented and dropped (but don't enable Target - this is a reposition, not a natural drop)
        transform.SetParent(null);
        IsEquipped = false;
        IsDropped = true;
        _equippedController = null;

        BroadcastStateUpdate(new HopballStateUpdate {
            Flags = HopballStateFlags.ShowRealImmediate,
            TargetStateSpecified = true,
            TargetEnabled = false,
            PositionSpecified = true,
            Position = position,
            Rotation = rotation
        });

        // Temporarily set to non-kinematic to allow velocity changes, then set back to kinematic
        var wasKinematic = hopballRigidbody.isKinematic;
        if(wasKinematic) {
            hopballRigidbody.isKinematic = false;
        }
        hopballRigidbody.linearVelocity = Vector3.zero;
        hopballRigidbody.angularVelocity = Vector3.zero;
        hopballRigidbody.isKinematic = true;
        godrayEffect.SetActive(true);
    }


    /// <summary>
    /// Prepares for drop by hiding the hopball on all clients before teleport.
    /// Called before teleporting to prevent clients from seeing the teleport.
    /// </summary>
    [ClientRpc]
    public void PrepareDropClientRpc() {
        // Hide the hopball on all clients before teleport
        HideRealHopball();
    }

    /// <summary>
    /// Called by HopballController when ball is dropped.
    /// This method is called directly from owner, so we need to ensure the ClientRpc is called from server context.
    /// </summary>
    public void SetDropped() {
        if(!EnsureServerAuthority(nameof(SetDropped))) return;
        IsEquipped = false;
        IsDropped = true;
        _equippedController = null; // Clear controller reference when dropped
        HolderController = null;
        FlowLog.Emit(FlowEventIds.HopballHoldStateChanged,
            ("hopballNetId", NetworkObjectId),
            ("isEquipped", false),
            ("holder", "None"));

        // Get drop position from current transform (server has already set it in DropHopballAtPosition)
        var hopballTransform = transform;
        var dropPosition = hopballTransform.position;
        var dropRotation = hopballTransform.rotation;

        // If dissolving, ensure real hopball is shown and continues dissolving
        // Don't enable target indicator during dissolve - wait until respawn
        if(IsDissolving) {
            BroadcastStateUpdate(new HopballStateUpdate {
                Flags = HopballStateFlags.CleanupVisuals | HopballStateFlags.ShowRealDropped,
                TargetStateSpecified = true,
                TargetEnabled = false,
                PositionSpecified = true,
                Position = dropPosition,
                Rotation = dropRotation
            });
            // Don't enable target - ball is dissolving, not naturally dropped
            return;
        }

        BroadcastStateUpdate(new HopballStateUpdate {
            Flags = HopballStateFlags.CleanupVisuals | HopballStateFlags.ShowRealDropped,
            PositionSpecified = true,
            Position = dropPosition,
            Rotation = dropRotation
        });
    }

    private void SetupDroppedVisuals(bool isDrop = false) {
        // World model: ShadowsOnly for everyone when dropped
        // Note: FP visual is destroyed separately by HopballController
        meshRenderer.enabled = true;
        meshRenderer.shadowCastingMode = ShadowCastingMode.On;

        // Disable godray effect when dropped (only enabled on respawn or OOB reposition)
        // Use isDrop parameter instead of IsDropped since IsDropped is only set on server
        if(isDrop && godrayEffect != null) {
            godrayEffect.SetActive(false);
        }

        // Also ensure root GameObject and all children are active
        gameObject.SetActive(true);
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private void BroadcastStateUpdate(HopballStateUpdate update) {
        ApplyHopballState(update);
        if(HasHopballAuthority) {
            ApplyStateClientRpc(update);
        }
    }

    /// <summary>Applies the given hopball state on clients (authority already applied locally).</summary>
    [ClientRpc]
    private void ApplyStateClientRpc(HopballStateUpdate update) {
        if(HasHopballAuthority) return; // already applied on authority
        ApplyHopballState(update);
    }

    private void ApplyHopballState(HopballStateUpdate update) {
        if((update.Flags & HopballStateFlags.CleanupVisuals) != 0) {
            if(update.DissolveHolderClientIdSpecified && NetworkManager != null &&
               NetworkManager.LocalClientId == update.DissolveHolderClientId) {
                foreach(var controller in PlayerHopballController.Instances) {
                    if(controller != null && controller.OwnerClientId == update.DissolveHolderClientId) {
                        controller.RunCleanupAndRestoreWeaponsAfterDissolve();
                        break;
                    }
                }
            }
            foreach(var controller in PlayerHopballController.Instances) {
                if(controller == null) continue;
                controller.CleanupHopballVisuals();
            }
        }

        if(update.PositionSpecified) {
            var hopballTransform = transform;
            hopballTransform.position = update.Position;
            hopballTransform.rotation = update.Rotation;
        }

        if((update.Flags & HopballStateFlags.HideReal) != 0) {
            HideRealHopball();
        }

        if((update.Flags & HopballStateFlags.ShowRealDropped) != 0) {
            SetPlayerCollisionIgnored(true);
            ShowRealHopball();
            SetupDroppedVisuals(isDrop: true); // Explicitly indicate this is a drop
            if(!update.TargetStateSpecified) {
                target.enabled = !IsDissolving;
            }
        }

        if((update.Flags & HopballStateFlags.ShowRealImmediate) != 0) {
            SetPlayerCollisionIgnored(false);
            ShowRealHopball();
            SetupDroppedVisuals(isDrop: false); // Respawn/OOB reposition - keep godray enabled
            if(!update.TargetStateSpecified) {
                target.enabled = false;
            }
        }

        if(update.TargetStateSpecified) {
            target.enabled = update.TargetEnabled;
        }

        if((update.Flags & (HopballStateFlags.HideReal | HopballStateFlags.ShowRealDropped |
                            HopballStateFlags.ShowRealImmediate)) != 0
           || update.TargetStateSpecified) {
            NotifyVisualStateChanged(true);
        }
    }

    public void OnControllerRegistered(PlayerHopballController controller) {
        if(!_isIgnoringPlayerCollisions || hopballCollider == null) return;
        if(controller == null) return;
        var col = controller.PlayerCollider;
        if(col == null) return;
        if(_ignoredPlayerColliders.Add(col)) {
            Physics.IgnoreCollision(hopballCollider, col, true);
        }
    }

    public void OnControllerUnregistered(PlayerHopballController controller) {
        if(controller == null) return;
        var col = controller.PlayerCollider;
        if(col == null) return;
        if(_ignoredPlayerColliders.Remove(col) && hopballCollider != null) {
            Physics.IgnoreCollision(hopballCollider, col, false);
        }
    }

    private void SetPlayerCollisionIgnored(bool ignore) {
        if(hopballCollider == null) return;
        if(_isIgnoringPlayerCollisions == ignore) return;

        _isIgnoringPlayerCollisions = ignore;

        if(ignore) {
            foreach(var controller in PlayerHopballController.Instances) {
                if(controller == null) continue;
                var col = controller.PlayerCollider;
                if(col == null || !_ignoredPlayerColliders.Add(col)) continue;
                Physics.IgnoreCollision(hopballCollider, col, true);
            }
        } else {
            foreach(var col in _ignoredPlayerColliders) {
                if(col != null) Physics.IgnoreCollision(hopballCollider, col, false);
            }

            _ignoredPlayerColliders.Clear();
        }
    }

    private void NotifyVisualStateChanged(bool forceBroadcast) {
        // Always refresh CurrentVisualState, even if no listeners are registered yet.
        // Some visuals (e.g. HopballVisual.OnEnable) read CurrentVisualState immediately on enable.
        var state = new HopballVisualState(
            CurrentEffectScale,
            CurrentLightIntensity,
            CurrentEmissionIntensity,
            DissolveAmount,
            target != null && target.enabled
        );

        var changed = forceBroadcast || !ApproximatelyEquals(CurrentVisualState, state);
        CurrentVisualState = state;

        if(!changed) return;
        if(VisualStateChanged == null) return;
        VisualStateChanged.Invoke(state);
    }

    private static bool ApproximatelyEquals(HopballVisualState a, HopballVisualState b) {
        const float epsilon = 0.0001f;
        return (a.EffectScale - b.EffectScale).sqrMagnitude < epsilon &&
               Mathf.Abs(a.LightIntensity - b.LightIntensity) < epsilon &&
               Mathf.Abs(a.EmissionIntensity - b.EmissionIntensity) < epsilon &&
               Mathf.Abs(a.DissolveAmount - b.DissolveAmount) < epsilon &&
               a.TargetEnabled == b.TargetEnabled;
    }

    /// <summary>Sets effects scale and light to zero on clients before dissolve starts.</summary>
    [ClientRpc]
    private void SetEffectsScaleZeroClientRpc() {
        SetEffectsScaleToZero();
    }

    /// <summary>
    /// Sets effects scale and light intensity to 0 immediately.
    /// Called locally on clients and via ClientRpc from server.
    /// </summary>
    private void SetEffectsScaleToZero() {
        if(effects != null) {
            effects.localScale = Vector3.zero;
            _lastAppliedEffectScale = Vector3.zero;
        }

        if(effectLight != null) {
            effectLight.intensity = 0f;
            _lastAppliedLightIntensity = 0f;
        }

        NotifyVisualStateChanged(false);
    }

    /// <summary>
    /// Handles the dissolve effect when energy reaches zero.
    /// Lerps dissolveAmount from 0 to 1, then triggers respawn.
    /// </summary>
    private void HandleDissolve() {
        // Progress dissolve from current amount to 1
        DissolveAmount = Mathf.Lerp(DissolveAmount, 1f, DissolveSmoothing * Time.deltaTime);

        // Check if we've reached completion threshold (0.9f is visually complete)
        // Once threshold is reached, clamp to 1.0 to ensure immediate completion detection
        if(DissolveAmount >= 0.9f) {
            DissolveAmount = 1f;
            if(ApplyDissolveAmount(DissolveAmount)) {
                NotifyVisualStateChanged(false);
            }
            CompleteDissolve();
        } else {
            if(ApplyDissolveAmount(DissolveAmount)) {
                NotifyVisualStateChanged(false);
            }
        }
    }

    /// <summary>
    /// Handles the completion of the dissolve effect - removes from player and respawns.
    /// </summary>
    private void CompleteDissolve() {
        if(IsDissolving == false) return; // Prevent multiple calls
        FlowLog.Emit(FlowEventIds.HopballDissolveCompleted,
            ("hopballNetId", NetworkObjectId),
            ("respawnDelay", "ConfiguredBySpawnManager"));

        // If equipped, notify the holder client to clean up visuals and restore weapons (via state update; DA-compatible)
        var controller = _equippedController; // Cache reference before clearing
        if(IsEquipped && controller != null) {
            var ownerId = controller.OwnerClientId;
            BroadcastStateUpdate(new HopballStateUpdate {
                Flags = HopballStateFlags.CleanupVisuals,
                DissolveHolderClientIdSpecified = true,
                DissolveHolderClientId = ownerId
            });
        }

        // Clear equipped state to prevent any lingering references
        IsEquipped = false;
        _equippedController = null;

        // Ensure ball is marked as dropped
        if(!IsDropped) {
            SetDropped();
        }

        // Respawn at new location (server only)
        if(HasHopballAuthority && HopballSpawnManager.Instance != null) {
            HopballSpawnManager.Instance.RespawnAtNewLocation();
        }

        BroadcastStateUpdate(new HopballStateUpdate {
            TargetStateSpecified = true,
            TargetEnabled = false
        });

        IsDissolving = false;
    }

    public void PrepareForRespawnDelay() {
        IsAwaitingRespawn = true;
        HideRealHopball();
        if(target != null) {
            target.enabled = false;
        }
    }

    /// <summary>
    /// Hides the real hopball by disabling all visual components and collider.
    /// Used when equipped so only visuals are shown.
    /// </summary>
    private void HideRealHopball() {
        meshRenderer.enabled = false;
        effects.gameObject.SetActive(false);
        effectLight.enabled = false;
        hopballCollider.enabled = false;
        godrayEffect.SetActive(false);
    }

    private void ShowRealHopball() {
        meshRenderer.enabled = true;
        effects.gameObject.SetActive(true);
        effectLight.enabled = true;
        hopballCollider.enabled = true;
        godrayEffect.SetActive(true);
    }

    /// <summary>
    /// Resets hopball to initial spawn state (full energy, no dissolve, enabled components).
    /// Called on spawn and when respawning.
    /// </summary>
    private void ResetToInitialState() {
        if(HasHopballAuthority) {
            _networkEnergy.Value = MaxEnergy;
        }

        InvalidateVisualCache();
        _displayEnergy = _networkEnergy.Value;
        UpdateEffects(_displayEnergy);

        DissolveAmount = 0f;
        ApplyDissolveAmount(DissolveAmount);
        godrayEffect.SetActive(true);

        IsEquipped = false;
        IsDropped = false;
        _equippedController = null;
        _nextDrainAt = -1f;

        ShowRealHopball();
        SetupDroppedVisuals(isDrop: false); // Respawn - keep godray enabled
        target.enabled = false;

        // Temporarily set to non-kinematic to allow velocity changes, then set back to kinematic
        var wasKinematic = hopballRigidbody.isKinematic;
        if(wasKinematic) {
            hopballRigidbody.isKinematic = false;
        }
        hopballRigidbody.linearVelocity = Vector3.zero;
        hopballRigidbody.angularVelocity = Vector3.zero;
        hopballRigidbody.isKinematic = true;
        SetPlayerCollisionIgnored(false);
    }

    private static bool HasSignificantDelta(float lastValue, float nextValue) {
        return float.IsNaN(lastValue) || Mathf.Abs(lastValue - nextValue) > VisualWriteEpsilon;
    }

    private static bool HasSignificantDelta(Vector3 lastValue, Vector3 nextValue) {
        return float.IsNaN(lastValue.x) || (lastValue - nextValue).sqrMagnitude > VisualWriteEpsilon * VisualWriteEpsilon;
    }

    private bool ApplyDissolveAmount(float dissolveAmount) {
        if(meshRenderer == null || !HasSignificantDelta(_lastAppliedDissolveAmount, dissolveAmount)) {
            return false;
        }

        meshRenderer.material.SetFloat(DissolveAmountID, dissolveAmount);
        _lastAppliedDissolveAmount = dissolveAmount;
        return true;
    }

    /// <summary>Clears cached visual write values so the next update re-applies.</summary>
    private void InvalidateVisualCache() {
        _lastAppliedEffectScale = new Vector3(float.NaN, float.NaN, float.NaN);
        _lastAppliedLightIntensity = float.NaN;
        _lastAppliedEmissionIntensity = float.NaN;
        _lastAppliedDissolveAmount = float.NaN;
    }
    }
}
