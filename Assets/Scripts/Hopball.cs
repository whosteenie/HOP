using System.Collections.Generic;
using Game.Player;
using Network.Singletons;
using OSI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

public class Hopball : NetworkBehaviour {
    public static Hopball Instance { get; private set; }

    public readonly int IntensityID = Shader.PropertyToID("_EmissionIntensity");
    public readonly int DissolveAmountID = Shader.PropertyToID("_DissolveAmount");

    private const float EnergySmoothing = 1.75f;
    private const float DissolveSmoothing = 2f;
    private const float MaxEnergy = 20;
    private readonly Vector3 _maxEffectScale = new(0.45f, 0.45f, 0.45f);
    private readonly Vector3 _minEffectScale = new(0.23f, 0.23f, 0.23f);

    // Network-synced energy (server-authoritative)
    private readonly NetworkVariable<float> _networkEnergy = new(value: MaxEnergy);

    private int _lastDrainTime = -1; // Initialize to -1 to track first drain
    private bool _isDissolving; // Track if dissolve is in progress

    [Header("World Model Components (on this prefab)")]
    [SerializeField] private MeshRenderer meshRenderer;

    [SerializeField] private Transform effects;
    [SerializeField] private Light effectLight;
    [SerializeField] private Target target;
    [SerializeField] private Collider hopballCollider; // Collider to disable when equipped
    [SerializeField] private Rigidbody hopballRigidbody;

    private HopballController _equippedController; // Store reference to controller when equipped
    private readonly HashSet<Collider> _ignoredPlayerColliders = new();
    private bool _isIgnoringPlayerCollisions;
    private HopballVisualState _lastBroadcastedState;

    public float Energy => _networkEnergy.Value;
    public bool IsEquipped { get; private set; }
    public bool IsDropped { get; private set; }

    public PlayerController HolderController { get; private set; }
    public Rigidbody Rigidbody => hopballRigidbody;

    public float DissolveAmount { get; private set; }
    public HopballVisualState CurrentVisualState => _lastBroadcastedState;

    /// <summary>
    /// Gets the current emission intensity from the world hopball material.
    /// Returns 0 if material is not available or renderer is disabled.
    /// </summary>
    public float CurrentEmissionIntensity => meshRenderer.material.GetFloat(IntensityID);

    /// <summary>
    /// Gets the current effect scale from the world hopball effects transform.
    /// Returns zero vector if effects are not available or if dissolving.
    /// During dissolve, returns zero to ensure FP visuals don't show effects.
    /// </summary>
    public Vector3 CurrentEffectScale => _isDissolving ? Vector3.zero : effects.localScale;

    /// <summary>
    /// Gets the current light intensity from the world hopball light.
    /// Returns 0 if light is not available, disabled, or if dissolving.
    /// During dissolve, returns zero to ensure FP visuals don't show effects.
    /// </summary>
    public float CurrentLightIntensity => _isDissolving ? 0f : effectLight.intensity;

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

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
            serializer.SerializeValue(ref Flags);
            serializer.SerializeValue(ref TargetStateSpecified);
            serializer.SerializeValue(ref TargetEnabled);
            serializer.SerializeValue(ref PositionSpecified);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
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

        // Set singleton instance
        Instance = this;

        _networkEnergy.OnValueChanged += OnEnergyChanged;

        // Reset all state to initial spawn state
        ResetToInitialState();

        // Ensure root GameObject is active
        gameObject.SetActive(true);

        // Initialize energy display
        UpdateEffects(_networkEnergy.Value);

        // Set up dropped visuals initially (since hopball spawns dropped)
        SetupDroppedVisuals();

        foreach(var controller in HopballController.Instances) {
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
    }

    private void OnEnergyChanged(float previous, float current) {
        UpdateEffects(current);

        // Award scoring points as energy depletes (server only, while equipped)
        if(!IsServer || !IsEquipped || !(previous > current)) return;
        var energyDepleted = previous - current;
        HopballSpawnManager.Instance.OnEnergyDepleted(_equippedController.OwnerClientId, energyDepleted);
    }

    private void Update() {
        if(!IsSpawned) return;

        // Server handles energy drain (only while equipped, unless dissolving)
        // If dissolving, continue draining even if dropped to complete the dissolve
        if(IsServer && (IsEquipped || _isDissolving)) {
            var currentTime = MatchTimerManager.Instance.TimeRemainingSeconds;

            // Initialize last drain time on first frame
            if(_lastDrainTime < 0) {
                _lastDrainTime = currentTime;
            }

            // Drain energy every 2 seconds
            if(_lastDrainTime - currentTime >= 2) {
                var newEnergy = Mathf.Max(0f, _networkEnergy.Value - 1f);
                _networkEnergy.Value = newEnergy;
                _lastDrainTime = currentTime;
            }
        }

        // Update effects on all clients every frame (for visual syncing)
        // Effects now only update when state actually changes; no per-frame visual polling required

        // Handle dissolve effect when energy is 0
        if(_networkEnergy.Value <= 0 && !_isDissolving) {
            _isDissolving = true;
            // Set effects scale to 0 immediately before starting dissolve
            // This ensures effects recede into the ball surface and aren't visible during dissolve
            // Only call ClientRpc from server (ClientRpcs can only be called from server)
            if(IsServer) {
                SetEffectsScaleToZeroClientRpc();
            } else {
                // On clients, set effects scale to 0 locally
                SetEffectsScaleToZero();
            }
        }

        if(_isDissolving) {
            HandleDissolve();
        } else if(DissolveAmount > 0f) {
            DissolveAmount = 0f;
            meshRenderer.material.SetFloat(DissolveAmountID, DissolveAmount);
            NotifyVisualStateChanged(false);
        }
    }

    private void UpdateEffects(float energy) {
        var energyRatio = energy > 0 ? energy / MaxEnergy : 0f;
        var targetScale = Vector3.Lerp(_minEffectScale, _maxEffectScale, energyRatio);

        effects.localScale = targetScale;
        effectLight.intensity = energyRatio;
        meshRenderer.material.SetFloat(IntensityID, energyRatio);

        NotifyVisualStateChanged(false);
    }

    /// <summary>
    /// Called by HopballController when ball is equipped.
    /// </summary>
    public void SetEquipped(bool equipped, bool isOwner, HopballController controller = null) {
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
            meshRenderer.material.SetFloat(DissolveAmountID, DissolveAmount);

            _isDissolving = false;
            _equippedController = controller;
            HolderController = controller != null ? controller.GetComponent<PlayerController>() : null;
            _lastDrainTime = -1;
        } else {
            _equippedController = null;
            HolderController = null;
        }
    }

    /// <summary>
    /// Respawns the hopball at a new location with full energy.
    /// Called by HopballSpawnManager after dissolve completes.
    /// </summary>
    public void RespawnAtLocation(Vector3 position, Quaternion rotation) {
        // Clear equipped state FIRST to ensure controllers know they're not holding it
        // Also disable the previous holder's Target indicator and clean up visuals
        if(_equippedController != null) {
            var controller = _equippedController; // Cache before clearing
            controller.ClearHopballReference();
            // Disable the holder's Target indicator on all clients
            controller.DisablePlayerTargetClientRpc();
            // Clean up visuals on the owner client
            controller.CleanupVisualsAndRestoreWeaponsAfterDissolveClientRpc();
            _equippedController = null;
            HolderController = null;
        }

        IsEquipped = false;

        // Position at new location
        transform.position = position;
        transform.rotation = rotation;

        // Ensure unparented
        transform.SetParent(null);

        ResetToInitialState();

        BroadcastStateUpdate(new HopballStateUpdate {
            Flags = HopballStateFlags.CleanupVisuals | HopballStateFlags.ShowRealImmediate,
            TargetStateSpecified = true,
            TargetEnabled = false,
            PositionSpecified = true,
            Position = position,
            Rotation = rotation
        });
    }

    /// <summary>
    /// Repositions the hopball at a location (for OOB handling).
    /// Retains current energy.
    /// </summary>
    public void RepositionAtLocation(Vector3 position, Quaternion rotation) {
        // Just move it, don't reset energy
        transform.position = position;
        transform.rotation = rotation;

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

        hopballRigidbody.isKinematic = true;
        hopballRigidbody.linearVelocity = Vector3.zero;
        hopballRigidbody.angularVelocity = Vector3.zero;
    }


    /// <summary>
    /// Called by HopballController when ball is dropped.
    /// This method is called directly from owner, so we need to ensure the ClientRpc is called from server context.
    /// </summary>
    public void SetDropped() {
        IsEquipped = false;
        IsDropped = true;
        _equippedController = null; // Clear controller reference when dropped

        // Get drop position from current transform (server has already set it in DropHopballAtPosition)
        var dropPosition = transform.position;
        var dropRotation = transform.rotation;

        // If dissolving, ensure real hopball is shown and continues dissolving
        // Don't enable target indicator during dissolve - wait until respawn
        if(_isDissolving) {
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

    private void SetupDroppedVisuals() {
        // World model: ShadowsOnly for everyone when dropped
        // Note: FP visual is destroyed separately by HopballController
        meshRenderer.enabled = true;
        meshRenderer.shadowCastingMode = ShadowCastingMode.On;

        // Also ensure root GameObject and all children are active
        gameObject.SetActive(true);
    }

    // ========================================================================
    // Helper Methods
    // ========================================================================

    private void BroadcastStateUpdate(HopballStateUpdate update) {
        ApplyHopballState(update);
        if(IsServer) {
            ApplyHopballStateClientRpc(update);
        }
    }

    [ClientRpc]
    private void ApplyHopballStateClientRpc(HopballStateUpdate update) {
        if(IsServer) return; // already applied on server
        ApplyHopballState(update);
    }

    private void ApplyHopballState(HopballStateUpdate update) {
        if((update.Flags & HopballStateFlags.CleanupVisuals) != 0) {
            foreach(var controller in HopballController.Instances) {
                controller?.CleanupHopballVisuals();
            }
        }

        if(update.PositionSpecified) {
            transform.position = update.Position;
            transform.rotation = update.Rotation;
        }

        if((update.Flags & HopballStateFlags.HideReal) != 0) {
            HideRealHopball();
        }

        if((update.Flags & HopballStateFlags.ShowRealDropped) != 0) {
            SetPlayerCollisionIgnored(true);
            ShowRealHopball();
            SetupDroppedVisuals();
            if(!update.TargetStateSpecified) {
                target.enabled = !_isDissolving;
            }
        }

        if((update.Flags & HopballStateFlags.ShowRealImmediate) != 0) {
            SetPlayerCollisionIgnored(false);
            ShowRealHopball();
            SetupDroppedVisuals();
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

    public void OnControllerRegistered(HopballController controller) {
        if(!_isIgnoringPlayerCollisions || hopballCollider == null) return;
        var col = controller?.PlayerCollider;
        if(col == null) return;
        if(_ignoredPlayerColliders.Add(col)) {
            Physics.IgnoreCollision(hopballCollider, col, true);
        }
    }

    public void OnControllerUnregistered(HopballController controller) {
        var col = controller?.PlayerCollider;
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
            foreach(var controller in HopballController.Instances) {
                var col = controller?.PlayerCollider;
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
        if(VisualStateChanged == null) return;

        var state = new HopballVisualState(
            CurrentEffectScale,
            CurrentLightIntensity,
            CurrentEmissionIntensity,
            DissolveAmount,
            target != null && target.enabled
        );

        if(!forceBroadcast && ApproximatelyEquals(_lastBroadcastedState, state)) {
            return;
        }

        _lastBroadcastedState = state;
        VisualStateChanged?.Invoke(state);
    }

    private static bool ApproximatelyEquals(HopballVisualState a, HopballVisualState b) {
        const float epsilon = 0.0001f;
        return (a.EffectScale - b.EffectScale).sqrMagnitude < epsilon &&
               Mathf.Abs(a.LightIntensity - b.LightIntensity) < epsilon &&
               Mathf.Abs(a.EmissionIntensity - b.EmissionIntensity) < epsilon &&
               Mathf.Abs(a.DissolveAmount - b.DissolveAmount) < epsilon &&
               a.TargetEnabled == b.TargetEnabled;
    }

    /// <summary>
    /// ClientRpc to set effects scale to 0 immediately before dissolve starts.
    /// Ensures effects recede into the ball surface and aren't visible during dissolve.
    /// </summary>
    [ClientRpc]
    private void SetEffectsScaleToZeroClientRpc() {
        SetEffectsScaleToZero();
    }

    /// <summary>
    /// Sets effects scale and light intensity to 0 immediately.
    /// Called locally on clients and via ClientRpc from server.
    /// </summary>
    private void SetEffectsScaleToZero() {
        if(effects != null) {
            effects.localScale = Vector3.zero;
        }

        if(effectLight != null) {
            effectLight.intensity = 0f;
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

        // Check if we've reached completion threshold (0.99f is visually complete)
        // Once threshold is reached, clamp to 1.0 to ensure immediate completion detection
        if(DissolveAmount >= 0.99f) {
            DissolveAmount = 1f;
            meshRenderer.material.SetFloat(DissolveAmountID, DissolveAmount);
            CompleteDissolve();
        } else {
            meshRenderer.material.SetFloat(DissolveAmountID, DissolveAmount);
        }
    }

    /// <summary>
    /// Handles the completion of the dissolve effect - removes from player and respawns.
    /// </summary>
    private void CompleteDissolve() {
        if(_isDissolving == false) return; // Prevent multiple calls

        // If equipped, notify the owner client to clean up visuals and restore weapons
        var controller = _equippedController; // Cache reference before clearing
        if(IsEquipped && controller != null) {
            // Notify owner client to clean up visuals and restore weapons
            controller.CleanupVisualsAndRestoreWeaponsAfterDissolveClientRpc();
            // Disable player Target indicator (ball dissolved, no longer holding)
            controller.DisablePlayerTargetClientRpc();
        }

        // Clear equipped state to prevent any lingering references
        IsEquipped = false;
        _equippedController = null;

        // Ensure ball is marked as dropped
        if(!IsDropped) {
            SetDropped();
        }

        // Respawn at new location (server only)
        if(IsServer && HopballSpawnManager.Instance != null) {
            HopballSpawnManager.Instance.RespawnHopballAtNewLocation();
        }

        BroadcastStateUpdate(new HopballStateUpdate {
            TargetStateSpecified = true,
            TargetEnabled = false
        });

        _isDissolving = false;
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
    }

    private void ShowRealHopball() {
        meshRenderer.enabled = true;
        effects.gameObject.SetActive(true);
        effectLight.enabled = true;
        hopballCollider.enabled = true;
    }

    /// <summary>
    /// Resets hopball to initial spawn state (full energy, no dissolve, enabled components).
    /// Called on spawn and when respawning.
    /// </summary>
    private void ResetToInitialState() {
        if(IsServer) {
            _networkEnergy.Value = MaxEnergy;
        }

        DissolveAmount = 0f;
        meshRenderer.material.SetFloat(DissolveAmountID, DissolveAmount);

        IsEquipped = false;
        IsDropped = false;
        _equippedController = null;
        _lastDrainTime = -1;

        ShowRealHopball();
        SetupDroppedVisuals();
        target.enabled = false;

        hopballRigidbody.isKinematic = true;
        hopballRigidbody.linearVelocity = Vector3.zero;
        hopballRigidbody.angularVelocity = Vector3.zero;
        SetPlayerCollisionIgnored(false);
    }
}