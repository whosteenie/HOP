using System;
using System.Collections.Generic;
using Game.Social;
using Game.UI.Core;
using Steamworks;
using UnityEngine;
using UnityEngine.UIElements;

namespace Game.UI.Screens {
    /// <summary>
    /// Manages the in-game player context menu (right-click on scoreboard/chat).
    /// Handles showing/hiding, positioning, and action callbacks.
    /// </summary>
    public class InGameContextMenuManager : UIElementBase {
        public static InGameContextMenuManager Instance { get; private set; }
        
        private VisualElement _contextMenu;
        private VisualElement _backdrop;
        private Button _steamProfileButton;
        private Button _muteVoiceButton;
        private Button _blockButton;
        
        private ulong _targetSteamId;
        private string _targetPlayerId;
        
        public bool IsOpen => _contextMenu != null && !_contextMenu.ClassListContains("hidden");
        
        protected override void Awake() {
            base.Awake();
            if (Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }
        
        protected override void OnInitialize() {
            _backdrop = QOptional<VisualElement>("context-menu-backdrop");
            _contextMenu = QOptional<VisualElement>("player-context-menu");
            _steamProfileButton = QOptional<Button>("ctx-steam-profile");
            _muteVoiceButton = QOptional<Button>("ctx-mute-voice");
            _blockButton = QOptional<Button>("ctx-block");
            
            // Backdrop click closes menu
            if (_backdrop != null) {
                EventCallback<ClickEvent> backdropClick = _ => Hide();
                _backdrop.RegisterCallback(backdropClick);
                RegisterCleanup(() => _backdrop.UnregisterCallback(backdropClick));
            }
            
            // Button clicks
            if (_steamProfileButton != null) {
                EventCallback<ClickEvent> handler = _ => OnSteamProfile();
                _steamProfileButton.RegisterCallback(handler);
                RegisterCleanup(() => _steamProfileButton.UnregisterCallback(handler));
            }
            
            if (_muteVoiceButton != null) {
                EventCallback<ClickEvent> handler = _ => OnMuteVoice();
                _muteVoiceButton.RegisterCallback(handler);
                RegisterCleanup(() => _muteVoiceButton.UnregisterCallback(handler));
            }

            if(_blockButton == null) return;
            {
                EventCallback<ClickEvent> handler = _ => OnBlock();
                _blockButton.RegisterCallback(handler);
                RegisterCleanup(() => _blockButton.UnregisterCallback(handler));
            }
        }
        
        protected override Dictionary<string, Type> GetRequiredElements() {
            return new Dictionary<string, Type>();
        }
        
        /// <summary>
        /// Show the context menu at the specified position for the given player.
        /// </summary>
        public void Show(ulong steamId, Vector2 screenPosition) {
            if (_contextMenu == null || _backdrop == null) return;
            
            _targetSteamId = steamId;
            _targetPlayerId = steamId.ToString();
            
            // Update button text based on current mute state
            UpdateMuteButtonText();
            UpdateBlockButtonText();
            
            // Position the menu
            _contextMenu.style.left = screenPosition.x;
            _contextMenu.style.top = screenPosition.y;
            
            // Show
            _backdrop.RemoveFromClassList("hidden");
            _contextMenu.RemoveFromClassList("hidden");
            _contextMenu.BringToFront();
        }
        
        public void Hide() {
            _backdrop?.AddToClassList("hidden");
            _contextMenu?.AddToClassList("hidden");
            _targetSteamId = 0;
            _targetPlayerId = null;
        }
        
        private void UpdateMuteButtonText() {
            if (_muteVoiceButton == null || string.IsNullOrEmpty(_targetPlayerId)) return;
            var isMuted = SocialSettings.IsMuted(_targetPlayerId);
            _muteVoiceButton.text = isMuted ? "Unmute Voice" : "Mute Voice";
        }
        
        private void UpdateBlockButtonText() {
            if (_blockButton == null || string.IsNullOrEmpty(_targetPlayerId)) return;
            var isBlocked = SocialSettings.IsBlocked(_targetPlayerId);
            _blockButton.text = isBlocked ? "Unblock Player" : "Block Player";
        }
        
        private void OnSteamProfile() {
            if (_targetSteamId == 0) return;
            
            // Open Steam overlay to the player's profile
            SteamFriends.OpenUserOverlay(_targetSteamId, "steamid");
            Hide();
        }
        
        private void OnMuteVoice() {
            if (string.IsNullOrEmpty(_targetPlayerId)) return;
            
            var currentlyMuted = SocialSettings.IsMuted(_targetPlayerId);
            SocialSettings.SetMuted(_targetPlayerId, !currentlyMuted);
            Hide();
        }
        
        private void OnBlock() {
            if (string.IsNullOrEmpty(_targetPlayerId)) return;
            
            var currentlyBlocked = SocialSettings.IsBlocked(_targetPlayerId);
            SocialSettings.SetBlocked(_targetPlayerId, !currentlyBlocked);
            Hide();
        }
    }
}
