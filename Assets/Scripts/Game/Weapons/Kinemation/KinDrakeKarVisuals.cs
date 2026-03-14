using Game.Weapons.Core;
using UnityEngine;

namespace Game.Weapons.Kinemation {
    /// <summary>Drake shell and Kar loop-bullet suppression/restore for the KIN viewmodel.</summary>
    internal sealed class KinDrakeKarVisuals {
        private const float DrakeTopShellHideOffset = 0.75f;
        private const float KarLoopBulletHideOffset = 0.55f;

        private readonly KinActiveWeaponResolver _resolver;

        // Drake top shell
        private Transform _suppressedDrakeTopShellTransform;
        private Vector3 _suppressedDrakeTopShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeTopShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeTopShellOriginalLocalScale;
        private bool _hasSuppressedDrakeTopShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeTopShellRenderers;
        private bool[] _suppressedDrakeTopShellRendererEnabledStates;
        private bool _isDrakeTopShellSuppressionApplied;

        // Drake bottom shell
        private Transform _suppressedDrakeBottomShellTransform;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalPosition;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalPosition;
        private Vector3 _suppressedDrakeBottomShellOriginalLocalScale;
        private bool _hasSuppressedDrakeBottomShellOriginalLocalScale;
        private Renderer[] _suppressedDrakeBottomShellRenderers;
        private bool[] _suppressedDrakeBottomShellRendererEnabledStates;
        private bool _isDrakeBottomShellSuppressionApplied;

        // Kar loop bullet
        private Transform _karLoopBulletTransform;
        private Vector3 _karLoopBulletOriginalLocalPosition;
        private bool _hasKarLoopBulletOriginalLocalPosition;
        private Vector3 _karLoopBulletOriginalLocalScale;
        private bool _hasKarLoopBulletOriginalLocalScale;
        private Renderer[] _karLoopBulletRenderers;
        private bool[] _karLoopBulletRendererEnabledStates;
        private bool _isKarLoopBulletHidden;

        public KinDrakeKarVisuals(KinActiveWeaponResolver resolver) {
            _resolver = resolver;
        }

        private bool IsDrake => _resolver.GetActiveWeaponSpecialHandling() == WeaponData.KinemationSpecialHandling.DrakeShell;
        private bool IsKar => _resolver.GetActiveWeaponSpecialHandling() == WeaponData.KinemationSpecialHandling.KarLoopBullet;

        public bool IsDrakeTopShellSuppressionApplied => _isDrakeTopShellSuppressionApplied;
        public bool IsDrakeBottomShellSuppressionApplied => _isDrakeBottomShellSuppressionApplied;
        public bool IsKarLoopBulletHidden => _isKarLoopBulletHidden;

        public void SuppressDrakeTopShellForReloadStart() {
            if(_resolver.ActiveWeapon == null || !IsDrake) return;
            if(!EnsureDrakeTopShellTarget()) return;
            ApplyDrakeTopShellSuppressionNow();
        }

        public void SuppressDrakeBottomShellForReloadStart() {
            if(_resolver.ActiveWeapon == null || !IsDrake) return;
            if(!EnsureDrakeBottomShellTarget()) return;
            ApplyDrakeBottomShellSuppressionNow();
        }

        public void HideKarLoopBulletForReloadLoop() {
            if(_resolver.ActiveWeapon == null || !IsKar) return;
            if(!EnsureKarLoopBulletTarget()) return;
            ApplyKarLoopBulletHiddenNow();
        }

        public void ApplySuppressedDrakeTopShellPose() {
            if(!_isDrakeTopShellSuppressionApplied || _suppressedDrakeTopShellTransform == null) return;
            if(_hasSuppressedDrakeTopShellOriginalLocalPosition)
                _suppressedDrakeTopShellTransform.localPosition = _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            if(_hasSuppressedDrakeTopShellOriginalLocalScale) _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            if(_suppressedDrakeTopShellRenderers != null) {
                foreach(var r in _suppressedDrakeTopShellRenderers) { if(r != null && r.enabled) r.enabled = false; }
            }
        }

        public void ApplySuppressedDrakeBottomShellPose() {
            if(!_isDrakeBottomShellSuppressionApplied || _suppressedDrakeBottomShellTransform == null) return;
            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition)
                _suppressedDrakeBottomShellTransform.localPosition = _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            if(_suppressedDrakeBottomShellRenderers != null) {
                foreach(var r in _suppressedDrakeBottomShellRenderers) { if(r != null && r.enabled) r.enabled = false; }
            }
        }

        public void ApplyHiddenKarLoopBulletPose() {
            if(!_isKarLoopBulletHidden || _karLoopBulletTransform == null) return;
            if(_hasKarLoopBulletOriginalLocalPosition)
                _karLoopBulletTransform.localPosition = _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            if(_hasKarLoopBulletOriginalLocalScale) _karLoopBulletTransform.localScale = Vector3.zero;
            if(_karLoopBulletRenderers != null) {
                foreach(var r in _karLoopBulletRenderers) { if(r != null && r.enabled) r.enabled = false; }
            }
        }

        public void RestoreDrakeTopShellImmediate() {
            if(_suppressedDrakeTopShellRenderers != null && _suppressedDrakeTopShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeTopShellRenderers.Length, _suppressedDrakeTopShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    if(_suppressedDrakeTopShellRenderers[i] != null)
                        _suppressedDrakeTopShellRenderers[i].enabled = _suppressedDrakeTopShellRendererEnabledStates[i];
                }
            }
            if(_suppressedDrakeTopShellTransform != null) {
                if(_hasSuppressedDrakeTopShellOriginalLocalPosition) _suppressedDrakeTopShellTransform.localPosition = _suppressedDrakeTopShellOriginalLocalPosition;
                if(_hasSuppressedDrakeTopShellOriginalLocalScale) _suppressedDrakeTopShellTransform.localScale = _suppressedDrakeTopShellOriginalLocalScale;
            }
            ClearDrakeTopShellState();
        }

        public void RestoreDrakeBottomShellImmediate() {
            if(_suppressedDrakeBottomShellRenderers != null && _suppressedDrakeBottomShellRendererEnabledStates != null) {
                var limit = Mathf.Min(_suppressedDrakeBottomShellRenderers.Length, _suppressedDrakeBottomShellRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    if(_suppressedDrakeBottomShellRenderers[i] != null)
                        _suppressedDrakeBottomShellRenderers[i].enabled = _suppressedDrakeBottomShellRendererEnabledStates[i];
                }
            }
            if(_suppressedDrakeBottomShellTransform != null) {
                if(_hasSuppressedDrakeBottomShellOriginalLocalPosition) _suppressedDrakeBottomShellTransform.localPosition = _suppressedDrakeBottomShellOriginalLocalPosition;
                if(_hasSuppressedDrakeBottomShellOriginalLocalScale) _suppressedDrakeBottomShellTransform.localScale = _suppressedDrakeBottomShellOriginalLocalScale;
            }
            ClearDrakeBottomShellState();
        }

        public void RestoreKarLoopBulletImmediate() {
            if(_karLoopBulletRenderers != null && _karLoopBulletRendererEnabledStates != null) {
                var limit = Mathf.Min(_karLoopBulletRenderers.Length, _karLoopBulletRendererEnabledStates.Length);
                for(var i = 0; i < limit; i++) {
                    if(_karLoopBulletRenderers[i] != null)
                        _karLoopBulletRenderers[i].enabled = _karLoopBulletRendererEnabledStates[i];
                }
            }
            if(_karLoopBulletTransform != null) {
                if(_hasKarLoopBulletOriginalLocalPosition) _karLoopBulletTransform.localPosition = _karLoopBulletOriginalLocalPosition;
                if(_hasKarLoopBulletOriginalLocalScale) _karLoopBulletTransform.localScale = _karLoopBulletOriginalLocalScale;
            }
            ClearKarLoopBulletState();
        }

        public void OnAmmoEjectEvent() {
            if(!IsDrake) return;
            if(_isDrakeBottomShellSuppressionApplied) RestoreDrakeBottomShellImmediate();
        }

        public void OnShellShowEvent() {
            if(IsDrake) {
                RestoreDrakeTopShellImmediate();
                RestoreDrakeBottomShellImmediate();
            }
            if(IsKar) RestoreKarLoopBulletImmediate();
        }

        public void OnReloadCompleteEvent() {
            if(IsDrake) {
                RestoreDrakeTopShellImmediate();
                RestoreDrakeBottomShellImmediate();
            }
            if(IsKar) RestoreKarLoopBulletImmediate();
        }

        private bool EnsureDrakeTopShellTarget() {
            if(_suppressedDrakeTopShellTransform != null) return true;
            if(!_resolver.TryResolveDrakeTopShell(out var t) || t == null) return false;
            _suppressedDrakeTopShellTransform = t;
            _suppressedDrakeTopShellOriginalLocalPosition = t.localPosition;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = true;
            _suppressedDrakeTopShellOriginalLocalScale = t.localScale;
            _hasSuppressedDrakeTopShellOriginalLocalScale = true;
            _isDrakeTopShellSuppressionApplied = false;
            var renderers = t.GetComponentsInChildren<Renderer>(true);
            if(renderers is not { Length: > 0 }) return true;
            _suppressedDrakeTopShellRenderers = renderers;
            _suppressedDrakeTopShellRendererEnabledStates = new bool[renderers.Length];
            for(var i = 0; i < renderers.Length; i++)
                if(renderers[i] != null) _suppressedDrakeTopShellRendererEnabledStates[i] = renderers[i].enabled;
            return true;
        }

        private void ApplyDrakeTopShellSuppressionNow() {
            if(_suppressedDrakeTopShellTransform == null) return;
            if(_hasSuppressedDrakeTopShellOriginalLocalPosition)
                _suppressedDrakeTopShellTransform.localPosition = _suppressedDrakeTopShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            if(_hasSuppressedDrakeTopShellOriginalLocalScale) _suppressedDrakeTopShellTransform.localScale = Vector3.zero;
            if(_suppressedDrakeTopShellRenderers != null)
                foreach(var r in _suppressedDrakeTopShellRenderers) { if(r != null) r.enabled = false; }
            _isDrakeTopShellSuppressionApplied = true;
        }

        private void ClearDrakeTopShellState() {
            _suppressedDrakeTopShellTransform = null;
            _suppressedDrakeTopShellRenderers = null;
            _suppressedDrakeTopShellRendererEnabledStates = null;
            _suppressedDrakeTopShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeTopShellOriginalLocalPosition = false;
            _suppressedDrakeTopShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeTopShellOriginalLocalScale = false;
            _isDrakeTopShellSuppressionApplied = false;
        }

        private bool EnsureDrakeBottomShellTarget() {
            if(_suppressedDrakeBottomShellTransform != null) return true;
            if(!_resolver.TryResolveDrakeBottomShell(out var t) || t == null) return false;
            _suppressedDrakeBottomShellTransform = t;
            _suppressedDrakeBottomShellOriginalLocalPosition = t.localPosition;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = true;
            _suppressedDrakeBottomShellOriginalLocalScale = t.localScale;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = true;
            _isDrakeBottomShellSuppressionApplied = false;
            var renderers = t.GetComponentsInChildren<Renderer>(true);
            if(renderers is not { Length: > 0 }) return true;
            _suppressedDrakeBottomShellRenderers = renderers;
            _suppressedDrakeBottomShellRendererEnabledStates = new bool[renderers.Length];
            for(var i = 0; i < renderers.Length; i++)
                if(renderers[i] != null) _suppressedDrakeBottomShellRendererEnabledStates[i] = renderers[i].enabled;
            return true;
        }

        private void ApplyDrakeBottomShellSuppressionNow() {
            if(_suppressedDrakeBottomShellTransform == null) return;
            if(_hasSuppressedDrakeBottomShellOriginalLocalPosition)
                _suppressedDrakeBottomShellTransform.localPosition = _suppressedDrakeBottomShellOriginalLocalPosition + Vector3.down * DrakeTopShellHideOffset;
            if(_hasSuppressedDrakeBottomShellOriginalLocalScale) _suppressedDrakeBottomShellTransform.localScale = Vector3.zero;
            if(_suppressedDrakeBottomShellRenderers != null)
                foreach(var r in _suppressedDrakeBottomShellRenderers) { if(r != null) r.enabled = false; }
            _isDrakeBottomShellSuppressionApplied = true;
        }

        private void ClearDrakeBottomShellState() {
            _suppressedDrakeBottomShellTransform = null;
            _suppressedDrakeBottomShellRenderers = null;
            _suppressedDrakeBottomShellRendererEnabledStates = null;
            _suppressedDrakeBottomShellOriginalLocalPosition = Vector3.zero;
            _hasSuppressedDrakeBottomShellOriginalLocalPosition = false;
            _suppressedDrakeBottomShellOriginalLocalScale = Vector3.one;
            _hasSuppressedDrakeBottomShellOriginalLocalScale = false;
            _isDrakeBottomShellSuppressionApplied = false;
        }

        private bool EnsureKarLoopBulletTarget() {
            if(_karLoopBulletTransform != null) return true;
            if(!_resolver.TryResolveKarLoopBullet(out var t) || t == null) return false;
            _karLoopBulletTransform = t;
            _karLoopBulletOriginalLocalPosition = t.localPosition;
            _hasKarLoopBulletOriginalLocalPosition = true;
            _karLoopBulletOriginalLocalScale = t.localScale;
            _hasKarLoopBulletOriginalLocalScale = true;
            _isKarLoopBulletHidden = false;
            var renderers = t.GetComponentsInChildren<Renderer>(true);
            if(renderers is not { Length: > 0 }) return true;
            _karLoopBulletRenderers = renderers;
            _karLoopBulletRendererEnabledStates = new bool[renderers.Length];
            for(var i = 0; i < renderers.Length; i++)
                if(renderers[i] != null) _karLoopBulletRendererEnabledStates[i] = renderers[i].enabled;
            return true;
        }

        private void ApplyKarLoopBulletHiddenNow() {
            if(_karLoopBulletTransform == null) return;
            if(_hasKarLoopBulletOriginalLocalPosition)
                _karLoopBulletTransform.localPosition = _karLoopBulletOriginalLocalPosition + Vector3.down * KarLoopBulletHideOffset;
            if(_hasKarLoopBulletOriginalLocalScale) _karLoopBulletTransform.localScale = Vector3.zero;
            if(_karLoopBulletRenderers != null)
                foreach(var r in _karLoopBulletRenderers) { if(r != null) r.enabled = false; }
            _isKarLoopBulletHidden = true;
        }

        private void ClearKarLoopBulletState() {
            _karLoopBulletTransform = null;
            _karLoopBulletRenderers = null;
            _karLoopBulletRendererEnabledStates = null;
            _karLoopBulletOriginalLocalPosition = Vector3.zero;
            _hasKarLoopBulletOriginalLocalPosition = false;
            _karLoopBulletOriginalLocalScale = Vector3.one;
            _hasKarLoopBulletOriginalLocalScale = false;
            _isKarLoopBulletHidden = false;
        }
    }
}
