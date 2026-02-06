using System;
using System.Collections.Generic;
using Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Social {
    public enum VoiceInputMode {
        PushToTalk = 0,
        OpenMic = 1
    }

    /// <summary>
    /// Manages persistent settings for Social features (Voice/Chat).
    /// Saves to settings.json.
    /// </summary>
    public static class SocialSettings {
        private const int MAX_MUTED_PLAYERS = 200; // Cap to prevent unbounded growth

        public static event Action OnSettingsChanged;
        public static event Action<string, bool> OnPlayerMuteChanged; // (playerId, isMuted)

        // --- Voice Output ---
        public static VoiceInputMode InputMode {
            get {
                var s = GameSettings.Data.social;
                if(s == null) return VoiceInputMode.PushToTalk;
                return (VoiceInputMode)s.voiceInputMode;
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.voiceInputMode = (int)value;
                Save();
            }
        }

        public static string InputDevice {
            get {
                var s = GameSettings.Data.social;
                if(s == null) return "Default";
                return s.voiceInputDevice;
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.voiceInputDevice = value;
                Save();
            }
        }

        /// <summary>
        /// Voice output volume (how loud you hear others). 0.0 to 1.0.
        /// </summary>
        public static float VoiceVolume {
            get {
                var s = GameSettings.Data.social;
                if(s == null) return 1f;
                return s.voiceVolume;
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.voiceVolume = Mathf.Clamp01(value);
                Save();
            }
        }
        
        /// <summary>
        /// Voice input volume (your microphone sensitivity). 0.0 to 1.0.
        /// </summary>
        public static float VoiceInputVolume {
            get {
                var s = GameSettings.Data.social;
                if(s == null) return 1f;
                return s.voiceInputVolume;
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.voiceInputVolume = Mathf.Clamp01(value);
                Save();
            }
        }

        // --- Chat ---
        public static bool ProfanityFilterEnabled {
            get {
                var s = GameSettings.Data.social;
                if(s == null) return false;
                return s.profanityFilterEnabled;
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.profanityFilterEnabled = value;
                Save();
            }
        }

        // --- Block/Mute ---
        private static List<string> _mutedList; // Use List to maintain order for LRU-style cap
        private static HashSet<string> _mutedCache;
        private static HashSet<string> _blockedCache;

        public static bool IsMuted(string playerId) {
            if(_mutedCache == null) LoadLists();
            return _mutedCache.Contains(playerId) || IsBlocked(playerId); // Block implies mute
        }

        public static bool IsBlocked(string playerId) {
            if(_blockedCache == null) LoadLists();
            return _blockedCache.Contains(playerId);
        }

        public static void SetMuted(string playerId, bool muted) {
            if(_mutedCache == null) LoadLists();
            
            if(muted) {
                if (!_mutedCache.Contains(playerId)) {
                    _mutedList.Add(playerId);
                    _mutedCache.Add(playerId);
                    
                    // Cap the list to prevent unbounded growth
                    while (_mutedList.Count > MAX_MUTED_PLAYERS) {
                        var oldest = _mutedList[0];
                        _mutedList.RemoveAt(0);
                        _mutedCache.Remove(oldest);
                    }
                }
            } else {
                _mutedList.Remove(playerId);
                _mutedCache.Remove(playerId);
            }
            
            SaveLists();
            OnPlayerMuteChanged?.Invoke(playerId, muted);
        }

        public static void SetBlocked(string playerId, bool blocked) {
            if(_blockedCache == null) LoadLists();
            if(blocked) {
                _blockedCache.Add(playerId);
                SetMuted(playerId, true); // Also mute them
            } else {
                _blockedCache.Remove(playerId);
            }
            SaveLists();
        }

        private static void LoadLists() {
            var s = GameSettings.Data.social;
            if(s == null) {
                _mutedList = new List<string>();
                _mutedCache = new HashSet<string>();
                _blockedCache = new HashSet<string>();
                return;
            }

            _mutedList = s.mutedPlayers != null ? new List<string>(s.mutedPlayers) : new List<string>();
            _mutedCache = new HashSet<string>(_mutedList);
            _blockedCache = s.blockedPlayers != null ? new HashSet<string>(s.blockedPlayers) : new HashSet<string>();
        }

        private static void SaveLists() {
            var s = GameSettings.Data.social;
            if(s == null) return;
            if(s.mutedPlayers == null) s.mutedPlayers = new List<string>();
            if(s.blockedPlayers == null) s.blockedPlayers = new List<string>();

            s.mutedPlayers.Clear();
            for(var i = 0; i < _mutedList.Count; i++) {
                s.mutedPlayers.Add(_mutedList[i]);
            }

            s.blockedPlayers.Clear();
            foreach(var id in _blockedCache) {
                s.blockedPlayers.Add(id);
            }
            Save();
        }

        private static void Save() {
            GameSettings.Save();
            OnSettingsChanged?.Invoke();
        }
    }
}
