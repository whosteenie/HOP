using UnityEngine;
using UnityEngine.Rendering;

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

    private void OnEnable() {
        Hopball.VisualStateChanged += OnHopballVisualStateChanged;
        ApplyCurrentState();
    }

    private void OnDisable() {
        Hopball.VisualStateChanged -= OnHopballVisualStateChanged;
    }

    private void ApplyCurrentState() {
        var hopball = Hopball.Instance;
        if(hopball == null) return;
        OnHopballVisualStateChanged(hopball.CurrentVisualState);
    }

    private void OnHopballVisualStateChanged(Hopball.HopballVisualState state) {
        if(effects != null && effects.gameObject.activeSelf) {
            effects.localScale = state.EffectScale;
        }

        if(effectLight != null && effectLight.gameObject.activeSelf && effectLight.enabled) {
            effectLight.intensity = state.LightIntensity;
        }

        if(meshRenderer != null) {
            var mat = meshRenderer.material;
            var hopball = Hopball.Instance;
            if(hopball != null) {
                mat.SetFloat(hopball.IntensityID, state.EmissionIntensity);
                mat.SetFloat(hopball.DissolveAmountID, state.DissolveAmount);
            }
        }
    }
    
    /// <summary>
    /// Disables effects and light for owner (they see FP visual instead).
    /// </summary>
    public void DisableEffectsForOwner() {
        effects.gameObject.SetActive(false);
        effectLight.enabled = false;
        meshRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
    }
}

