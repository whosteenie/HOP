using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Social {
    public enum VoiceInputMode {
        PushToTalk = 0,
        OpenMic = 1
    }

    /// <summary>
    /// Manages persistent settings for Social features (Voice/Chat).
    /// Saves to PlayerPrefs.
    /// </summary>
    public static class SocialSettings {
        private const string PREF_VOICE_INPUT_MODE = "Social_VoiceInputMode";
        private const string PREF_VOICE_VOLUME = "Social_VoiceVolume"; // Output volume 0.0 to 1.0
        private const string PREF_VOICE_INPUT_VOLUME = "Social_VoiceInputVolume"; // Input volume 0.0 to 1.0
        private const string PREF_VOICE_INPUT_DEVICE = "Social_VoiceInputDevice";
        private const string PREF_PROFANITY_FILTER = "Social_ProfanityFilter";
        private const string PREF_MUTED_PLAYERS = "Social_MutedPlayers"; // CSV of IDs
        private const string PREF_BLOCKED_PLAYERS = "Social_BlockedPlayers"; // CSV of IDs
        
        private const int MAX_MUTED_PLAYERS = 200; // Cap to prevent unbounded growth

        public static event Action OnSettingsChanged;
        public static event Action<string, bool> OnPlayerMuteChanged; // (playerId, isMuted)

        // --- Voice Output ---
        public static VoiceInputMode InputMode {
            get => (VoiceInputMode)PlayerPrefs.GetInt(PREF_VOICE_INPUT_MODE, (int)VoiceInputMode.PushToTalk);
            set {
                PlayerPrefs.SetInt(PREF_VOICE_INPUT_MODE, (int)value);
                Save();
            }
        }

        public static string InputDevice {
            get => PlayerPrefs.GetString(PREF_VOICE_INPUT_DEVICE, "Default");
            set {
                PlayerPrefs.SetString(PREF_VOICE_INPUT_DEVICE, value);
                Save();
            }
        }

        /// <summary>
        /// Voice output volume (how loud you hear others). 0.0 to 1.0.
        /// </summary>
        public static float VoiceVolume {
            get => PlayerPrefs.GetFloat(PREF_VOICE_VOLUME, 1.0f);
            set {
                PlayerPrefs.SetFloat(PREF_VOICE_VOLUME, Mathf.Clamp01(value));
                Save();
            }
        }
        
        /// <summary>
        /// Voice input volume (your microphone sensitivity). 0.0 to 1.0.
        /// </summary>
        public static float VoiceInputVolume {
            get => PlayerPrefs.GetFloat(PREF_VOICE_INPUT_VOLUME, 1.0f);
            set {
                PlayerPrefs.SetFloat(PREF_VOICE_INPUT_VOLUME, Mathf.Clamp01(value));
                Save();
            }
        }

        // --- Chat ---
        public static bool ProfanityFilterEnabled {
            get => PlayerPrefs.GetInt(PREF_PROFANITY_FILTER, 0) == 1; // Default Off
            set {
                PlayerPrefs.SetInt(PREF_PROFANITY_FILTER, value ? 1 : 0);
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
            var mutedStr = PlayerPrefs.GetString(PREF_MUTED_PLAYERS, "");
            _mutedList = new List<string>(mutedStr.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries));
            _mutedCache = new HashSet<string>(_mutedList);
            _blockedCache = new HashSet<string>(PlayerPrefs.GetString(PREF_BLOCKED_PLAYERS, "").Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries));
        }

        private static void SaveLists() {
            PlayerPrefs.SetString(PREF_MUTED_PLAYERS, string.Join(",", _mutedList));
            PlayerPrefs.SetString(PREF_BLOCKED_PLAYERS, string.Join(",", _blockedCache));
            Save();
        }

        private static void Save() {
            PlayerPrefs.Save();
            OnSettingsChanged?.Invoke();
        }
    }
}
