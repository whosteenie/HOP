using Events;
using Game.Match;
using Network.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Game.Player.Core {
    internal sealed class PlayerOutOfBounds {
        private const float TriggerOutOfBoundsCountdownSeconds = 3f;

        private readonly PlayerController _player;
        private float _lastDeathTime;
        private float _ignoreOutOfBoundsUntilTime;
        private int _cachedOobSceneHandle = -1;
        private float _cachedOutOfBoundsY;
        private bool _cachedUseYLevelOutOfBoundsKill = true;
        private bool _cachedUseTriggerOutOfBoundsKill;
        private Collider _cachedOutOfBoundsTriggerCollider;
        private bool _triggerOobCountdownActiveServer;
        private float _triggerOobDeadlineServerTime;
        private bool _triggerOobCountdownVisibleOwner;
        private float _triggerOobDeadlineOwnerTime;

        public PlayerOutOfBounds(PlayerController player) {
            _player = player;
        }

        public void SetOutOfBoundsGraceWindow(float seconds) {
            if(!NetworkAuthority.HasGlobalAuthority(_player)) return;
            var duration = Mathf.Max(0f, seconds);
            _ignoreOutOfBoundsUntilTime = Mathf.Max(_ignoreOutOfBoundsUntilTime, Time.time + duration);
        }

        public float GetOutOfBoundsKillY() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedOutOfBoundsY;
        }

        public bool IsYLevelOutOfBoundsKillEnabled() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedUseYLevelOutOfBoundsKill;
        }

        private bool IsTriggerOutOfBoundsKillEnabled() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedUseTriggerOutOfBoundsKill;
        }

        private Collider GetOutOfBoundsTriggerCollider() {
            RefreshOutOfBoundsCacheIfNeeded();
            return _cachedOutOfBoundsTriggerCollider;
        }

        public void HandleOutOfBoundsChecks(Vector3 authPos) {
            if(!NetworkAuthority.HasGlobalAuthority(_player)) return;

            var aliveAndControllable = !_player.NetIsDead.Value &&
                _player.CharacterController != null &&
                _player.CharacterController.enabled;
            if(!aliveAndControllable) {
                ClearTriggerOobCountdownServer();
                return;
            }

            if(Time.time < _ignoreOutOfBoundsUntilTime) {
                ClearTriggerOobCountdownServer();
                return;
            }

            if(IsYLevelOutOfBoundsKillEnabled() && authPos.y <= GetOutOfBoundsKillY()) {
                if(Time.time - _lastDeathTime < 4f) return;
                _lastDeathTime = Time.time;
                ClearTriggerOobCountdownServer();
                if(_player.CombatController != null) {
                    _player.CombatController.ApplyDamageServer_Auth(1000f, _player.PlayerTransform.position, Vector3.up,
                        ulong.MaxValue);
                }
                return;
            }

            if(!IsTriggerOutOfBoundsKillEnabled()) {
                ClearTriggerOobCountdownServer();
                return;
            }

            var triggerCollider = GetOutOfBoundsTriggerCollider();
            if(triggerCollider == null || !triggerCollider.enabled || !triggerCollider.gameObject.activeInHierarchy) {
                ClearTriggerOobCountdownServer();
                return;
            }

            if(IsPositionInsideTrigger(triggerCollider, authPos)) {
                ClearTriggerOobCountdownServer();
                return;
            }

            if(!_triggerOobCountdownActiveServer) {
                _triggerOobCountdownActiveServer = true;
                _triggerOobDeadlineServerTime = Time.time + TriggerOutOfBoundsCountdownSeconds;
                _player.ShowTriggerOobCountdownOwnerRpc(TriggerOutOfBoundsCountdownSeconds);
                return;
            }

            if(Time.time < _triggerOobDeadlineServerTime) return;
            if(Time.time - _lastDeathTime < 4f) return;

            _lastDeathTime = Time.time;
            ClearTriggerOobCountdownServer();
            if(_player.CombatController != null) {
                _player.CombatController.ApplyDamageServer_Auth(1000f, _player.PlayerTransform.position, Vector3.up,
                    ulong.MaxValue);
            }
        }

        /// <summary>Clears the trigger OOB countdown on the server.</summary>
        public void ClearTriggerOobCountdownServer() {
            if(!NetworkAuthority.HasGlobalAuthority(_player) || !_triggerOobCountdownActiveServer) return;
            _triggerOobCountdownActiveServer = false;
            _triggerOobDeadlineServerTime = 0f;
            _player.HideTriggerOobCountdownOwnerRpc();
        }

        /// <summary>Updates the trigger OOB countdown UI for the owner.</summary>
        public void UpdateTriggerOobCountdownUi() {
            if(!_player.IsOwner || !_triggerOobCountdownVisibleOwner) return;

            var aliveAndControllable = !_player.NetIsDead.Value &&
                _player.CharacterController != null &&
                _player.CharacterController.enabled;
            if(!aliveAndControllable) {
                HideTriggerOobCountdownLocal();
                return;
            }

            var remaining = Mathf.Max(0f, _triggerOobDeadlineOwnerTime - Time.unscaledTime);
            EventBus.Publish(new UpdateOutOfBoundsCountdownEvent(true, remaining));
        }

        public void ShowTriggerOobCountdownOwner(float countdownSeconds) {
            _triggerOobCountdownVisibleOwner = true;
            _triggerOobDeadlineOwnerTime = Time.unscaledTime + Mathf.Max(0f, countdownSeconds);
        }

        public void HideTriggerOobCountdownLocal() {
            _triggerOobCountdownVisibleOwner = false;
            _triggerOobDeadlineOwnerTime = 0f;
            EventBus.Publish(new UpdateOutOfBoundsCountdownEvent(false));
        }

        private void RefreshOutOfBoundsCacheIfNeeded() {
            var activeScene = SceneManager.GetActiveScene();
            if(_cachedOobSceneHandle == activeScene.handle) return;

            _cachedOobSceneHandle = activeScene.handle;
            _cachedOutOfBoundsY = _player.DefaultOutOfBoundsY;
            _cachedUseYLevelOutOfBoundsKill = MatchMapService.IsOobKillEnabled(activeScene.name);
            _cachedUseTriggerOutOfBoundsKill = MatchMapService.IsTriggerOobKillEnabled(activeScene.name);
            _cachedOutOfBoundsTriggerCollider = null;
            _triggerOobCountdownActiveServer = false;
            _triggerOobDeadlineServerTime = 0f;
            if(_player.IsOwner) {
                HideTriggerOobCountdownLocal();
            }

            Transform marker = null;
            if(!string.IsNullOrWhiteSpace(_player.OutOfBoundsMarkerTag)) {
                try {
                    var taggedObjects = GameObject.FindGameObjectsWithTag(_player.OutOfBoundsMarkerTag);
                    foreach(var taggedObject in taggedObjects) {
                        if(taggedObject == null) continue;
                        if(marker == null) {
                            marker = taggedObject.transform;
                        }

                        if(!_cachedUseTriggerOutOfBoundsKill || _cachedOutOfBoundsTriggerCollider != null) continue;
                        if(taggedObject.TryGetComponent<Collider>(out var taggedCollider) && taggedCollider != null &&
                           taggedCollider.isTrigger) {
                            _cachedOutOfBoundsTriggerCollider = taggedCollider;
                        }
                    }
                } catch(UnityException) {
                }
            }

            if(marker == null && !string.IsNullOrWhiteSpace(_player.OutOfBoundsMarkerName)) {
                var namedObject = GameObject.Find(_player.OutOfBoundsMarkerName);
                if(namedObject != null) {
                    marker = namedObject.transform;
                }
            }

            if(_cachedUseTriggerOutOfBoundsKill && _cachedOutOfBoundsTriggerCollider == null && marker != null) {
                if(marker.TryGetComponent<Collider>(out var markerCollider) && markerCollider != null &&
                   markerCollider.isTrigger) {
                    _cachedOutOfBoundsTriggerCollider = markerCollider;
                }
            }

            if(marker != null) {
                _cachedOutOfBoundsY = marker.position.y;
            }
        }

        private static bool IsPositionInsideTrigger(Collider triggerCollider, Vector3 worldPosition) {
            var closest = triggerCollider.ClosestPoint(worldPosition);
            return (closest - worldPosition).sqrMagnitude <= 0.0001f;
        }
    }
}
