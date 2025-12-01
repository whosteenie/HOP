using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI {
    public class SniperOverlayManager : MonoBehaviour {
        public static SniperOverlayManager Instance { get; private set; }

        #region Serialized Fields

        [Header("Sniper Overlay")]
        [SerializeField, Range(0.05f, 0.45f)] private float sniperOverlayHoleRadius = 0.3f;

        [SerializeField, Range(0.01f, 0.3f)] private float sniperOverlayFeather = 0.08f;
        [SerializeField] private int sniperOverlayTextureResolution = 1024;
        [SerializeField] private Color sniperOverlayColor = Color.black;

        #endregion

        #region Private Fields

        private VisualElement _sniperOverlay;
        private Texture2D _sniperOverlayTexture;
        private bool _sniperOverlayGeometryHooked;
        private VisualElement _crosshairContainer;
        private VisualElement _grappleIndicator;
        private Visibility _crosshairPrevVisibility = Visibility.Visible;
        private Visibility _grapplePrevVisibility = Visibility.Visible;
        private bool _sniperHudHidden;
        private float _cachedOverlayWidth = -1f;
        private float _cachedOverlayHeight = -1f;
        private int _cachedTextureWidth = -1;
        private int _cachedTextureHeight = -1;

        #endregion

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        /// <summary>
        /// Initializes the sniper overlay manager with UI element references.
        /// </summary>
        public void Initialize(VisualElement root) {
            // Find UI elements
            _sniperOverlay = root.Q<VisualElement>("sniper-overlay");
            _crosshairContainer = root.Q<VisualElement>("crosshair-container");
            _grappleIndicator = root.Q<VisualElement>("grapple-indicator");

            InitializeSniperOverlayVisual();
        }

        private void OnDisable() {
            CleanupSniperOverlayTexture();

            if(_sniperOverlay != null && _sniperOverlayGeometryHooked) {
                _sniperOverlay.UnregisterCallback<GeometryChangedEvent>(OnSniperOverlayGeometryChanged);
                _sniperOverlayGeometryHooked = false;
            }

            SetSniperOverlayHudHidden(false);
        }

        public void ToggleSniperOverlay(bool show) {
            if(_sniperOverlay == null) return;

            if(show && _sniperOverlayTexture == null) {
                GenerateSniperOverlayTexture();
            }

            if(show) {
                _sniperOverlay.RemoveFromClassList("hidden");
            } else {
                _sniperOverlay.AddToClassList("hidden");
            }

            SetSniperOverlayHudHidden(show);
        }

        private void InitializeSniperOverlayVisual() {
            if(_sniperOverlay == null) return;

            if(_sniperOverlayGeometryHooked) {
                _sniperOverlay.UnregisterCallback<GeometryChangedEvent>(OnSniperOverlayGeometryChanged);
                _sniperOverlayGeometryHooked = false;
            }

            _sniperOverlay.RegisterCallback<GeometryChangedEvent>(OnSniperOverlayGeometryChanged);
            _sniperOverlayGeometryHooked = true;
        }

        private void OnSniperOverlayGeometryChanged(GeometryChangedEvent evt) {
            if(evt.newRect.width <= 0f || evt.newRect.height <= 0f) return;
            
            // Only regenerate if dimensions actually changed
            // This prevents regeneration when just toggling visibility
            if(Mathf.Approximately(_cachedOverlayWidth, evt.newRect.width) && 
               Mathf.Approximately(_cachedOverlayHeight, evt.newRect.height)) {
                return; // Dimensions haven't changed, skip regeneration
            }
            
            _cachedOverlayWidth = evt.newRect.width;
            _cachedOverlayHeight = evt.newRect.height;
            GenerateSniperOverlayTexture();
        }

        private void GenerateSniperOverlayTexture() {
            if(_sniperOverlay == null) return;

            // Use cached dimensions if available, otherwise get from resolvedStyle
            float overlayWidth, overlayHeight;
            if(_cachedOverlayWidth > 0f && _cachedOverlayHeight > 0f) {
                overlayWidth = _cachedOverlayWidth;
                overlayHeight = _cachedOverlayHeight;
            } else {
                overlayWidth = _sniperOverlay.resolvedStyle.width;
                overlayHeight = _sniperOverlay.resolvedStyle.height;
                if(float.IsNaN(overlayWidth) || overlayWidth <= 0f) overlayWidth = Screen.width;
                if(float.IsNaN(overlayHeight) || overlayHeight <= 0f) overlayHeight = Screen.height;
                
                _cachedOverlayWidth = overlayWidth;
                _cachedOverlayHeight = overlayHeight;
            }

            overlayWidth = Mathf.Max(1f, overlayWidth);
            overlayHeight = Mathf.Max(1f, overlayHeight);

            var aspect = overlayWidth / overlayHeight;
            var baseResolution = Mathf.Clamp(sniperOverlayTextureResolution, 256, 4096);
            var texHeight = Mathf.Max(1, Mathf.RoundToInt(baseResolution / aspect));
            texHeight = Mathf.Clamp(texHeight, 256, 4096);

            // Check if texture needs to be recreated
            var needsRecreation = _sniperOverlayTexture == null ||
                                  _sniperOverlayTexture.width != baseResolution ||
                                  _sniperOverlayTexture.height != texHeight;

            // Only regenerate pixels if texture was recreated (new texture needs pixels)
            // or if cached texture dimensions don't match (shouldn't happen, but safety check)
            var needsPixelRegeneration = needsRecreation || 
                                        _cachedTextureWidth != baseResolution ||
                                        _cachedTextureHeight != texHeight;

            if(needsRecreation) {
                if(_sniperOverlayTexture != null) {
                    Destroy(_sniperOverlayTexture);
                }

                _sniperOverlayTexture = new Texture2D(baseResolution, texHeight, TextureFormat.RGBA32, false) {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
            }

            // Only regenerate pixels if texture was recreated or dimensions changed
            if(needsPixelRegeneration) {
                // Update cached texture dimensions
                _cachedTextureWidth = baseResolution;
                _cachedTextureHeight = texHeight;
                var pixels = new Color32[_sniperOverlayTexture.width * _sniperOverlayTexture.height];
                var radius = Mathf.Clamp(sniperOverlayHoleRadius, 0.05f, 0.45f);
                var feather = Mathf.Clamp(sniperOverlayFeather, 0.01f, 0.35f);
                var maxRadius = Mathf.Clamp01(radius + feather);

                var color = sniperOverlayColor;
                color.a = 1f;
                var transparent = new Color32(0, 0, 0, 0);

                var widthInv = 1f / _sniperOverlayTexture.width;
                var heightInv = 1f / _sniperOverlayTexture.height;

                for(var y = 0; y < _sniperOverlayTexture.height; y++) {
                    var ny = (y + 0.5f) * heightInv - 0.5f;
                    for(var x = 0; x < _sniperOverlayTexture.width; x++) {
                        var nx = (x + 0.5f) * widthInv - 0.5f;
                        var scaledX = nx * aspect;
                        var dist = Mathf.Sqrt(scaledX * scaledX + ny * ny);
                        var t = Mathf.InverseLerp(radius, maxRadius, dist);
                        t = Mathf.Clamp01(t);
                        var pixelColor = Color.Lerp(transparent, color, t);
                        pixels[y * _sniperOverlayTexture.width + x] = pixelColor;
                    }
                }

                _sniperOverlayTexture.SetPixels32(pixels);
                _sniperOverlayTexture.Apply();
            }

            // Always set backgroundImage (cheap operation, just assigns reference)
            _sniperOverlay.style.backgroundImage = new StyleBackground(_sniperOverlayTexture);
        }

        private void CleanupSniperOverlayTexture() {
            if(_sniperOverlayTexture == null) return;
            Destroy(_sniperOverlayTexture);
            _sniperOverlayTexture = null;
            _cachedOverlayWidth = -1f;
            _cachedOverlayHeight = -1f;
            _cachedTextureWidth = -1;
            _cachedTextureHeight = -1;
        }

        private void SetSniperOverlayHudHidden(bool hidden) {
            if(hidden) {
                if(!_sniperHudHidden) {
                    if(_crosshairContainer != null) {
                        _crosshairPrevVisibility = _crosshairContainer.resolvedStyle.visibility;
                    }

                    if(_grappleIndicator != null) {
                        _grapplePrevVisibility = _grappleIndicator.resolvedStyle.visibility;
                    }
                }

                if(_crosshairContainer != null) {
                    _crosshairContainer.style.visibility = Visibility.Hidden;
                }

                if(_grappleIndicator != null) {
                    _grappleIndicator.style.visibility = Visibility.Hidden;
                }

                _sniperHudHidden = true;
            } else if(_sniperHudHidden) {
                if(_crosshairContainer != null) {
                    _crosshairContainer.style.visibility = _crosshairPrevVisibility;
                }

                if(_grappleIndicator != null) {
                    _grappleIndicator.style.visibility = _grapplePrevVisibility;
                }

                _sniperHudHidden = false;
            }
        }
    }
}