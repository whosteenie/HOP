using UnityEngine;
using UnityEngine.Rendering;

namespace Game.Hopball {
    /// <summary>
    /// Visual-only representation of the hopball for first-person view.
    /// Syncs its visual effects (emission, light, particle scale) with the real hopball's energy state.
    /// Does not track state itself - just displays what the world hopball is doing.
    /// </summary>
    public class HopballVisual : MonoBehaviour {
    [Header("Visual Components")]
    [SerializeField] private MeshRenderer meshRenderer;

    [SerializeField] private Transform effects;
    [SerializeField] private Light effectLight;
    
    private RendererStateTracker _stateTracker;

    [Header("Smoothing")]
    [SerializeField] private float scaleLerpSpeed = 8f;

    [SerializeField] private float lightLerpSpeed = 8f;
    [SerializeField] private float emissionLerpSpeed = 10f;
    [SerializeField] private float dissolveLerpSpeed = 6f;

    private Vector3 _targetScale;
    private float _targetLightIntensity;
    private float _targetEmission;
    private float _targetDissolve;
    private bool _hasState;

    private Material _materialInstance;

    private void Awake() {
        if(meshRenderer != null) {
            _materialInstance = meshRenderer.material;
        }
    }

    private void OnEnable() {
        HopballController.VisualStateChanged += OnHopballVisualStateChanged;
        ApplyCurrentState();
    }

    private void OnDisable() {
        HopballController.VisualStateChanged -= OnHopballVisualStateChanged;
    }

    private void Update() {
        if(!_hasState) return;

        var dt = Time.deltaTime;

        if(effects != null && effects.gameObject.activeSelf) {
            effects.localScale = Vector3.Lerp(effects.localScale, _targetScale,
                1f - Mathf.Exp(-scaleLerpSpeed * dt));
        }

        if(effectLight != null && effectLight.gameObject.activeSelf && effectLight.enabled) {
            effectLight.intensity = Mathf.Lerp(effectLight.intensity, _targetLightIntensity,
                1f - Mathf.Exp(-lightLerpSpeed * dt));
        }

        if(_materialInstance == null) return;
        var hopball = HopballController.Instance;
        if(hopball == null) return;
        var newEmission = Mathf.Lerp(_materialInstance.GetFloat(hopball.IntensityID), _targetEmission,
            1f - Mathf.Exp(-emissionLerpSpeed * dt));
        var newDissolve = Mathf.Lerp(_materialInstance.GetFloat(hopball.DissolveAmountID), _targetDissolve,
            1f - Mathf.Exp(-dissolveLerpSpeed * dt));

        _materialInstance.SetFloat(hopball.IntensityID, newEmission);
        _materialInstance.SetFloat(hopball.DissolveAmountID, newDissolve);
    }

    private void ApplyCurrentState() {
        var hopball = HopballController.Instance;
        if(hopball == null) return;
        OnHopballVisualStateChanged(hopball.CurrentVisualState);
        // Force immediate application for initial state
        if(effects != null && effects.gameObject.activeSelf) {
            effects.localScale = _targetScale;
        }

        if(effectLight != null && effectLight.gameObject.activeSelf && effectLight.enabled) {
            effectLight.intensity = _targetLightIntensity;
        }

        if(_materialInstance == null || hopball == null) return;
        _materialInstance.SetFloat(hopball.IntensityID, _targetEmission);
        _materialInstance.SetFloat(hopball.DissolveAmountID, _targetDissolve);
    }

    private void OnHopballVisualStateChanged(HopballController.HopballVisualState state) {
        _targetScale = state.EffectScale;
        _targetLightIntensity = state.LightIntensity;
        _targetEmission = state.EmissionIntensity;
        _targetDissolve = state.DissolveAmount;
        _hasState = true;
    }

    /// <summary>
    /// Disables effects and light for owner (they see FP visual instead).
    /// </summary>
    public void DisableEffectsForOwner() {
        if(effects != null) {
            effects.gameObject.SetActive(false);
        }

        if(effectLight != null) {
            effectLight.enabled = false;
        }

        if(meshRenderer != null) {
            meshRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }
    }

    /// <summary>
    /// Ensures the mesh renderer is enabled. Used for world visuals when non-owners view the holder.
    /// </summary>
    public void EnsureMeshRendererEnabled() {
        if(meshRenderer == null) return;
        // Add state tracker dynamically for debugging
        if(_stateTracker == null) {
            _stateTracker = meshRenderer.gameObject.GetComponent<RendererStateTracker>();
            if(_stateTracker == null) {
                _stateTracker = meshRenderer.gameObject.AddComponent<RendererStateTracker>();
            }
            if(_stateTracker != null) {
                _stateTracker.StartTracking();
            }
        }
            
        meshRenderer.enabled = true;
        meshRenderer.shadowCastingMode = ShadowCastingMode.On;
    }
    }
}