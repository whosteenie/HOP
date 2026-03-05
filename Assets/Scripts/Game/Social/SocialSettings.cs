using System;
using System.Collections.Generic;
using Network.Events;
using Game.Settings;
using UnityEngine;

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
        private const int MaxMutedPlayers = 200; // Cap to prevent unbounded growth

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
                return s == null ? "Default" : s.voiceInputDevice;
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
                return s == null ? 1f : s.voiceVolume;
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
                return s == null ? 1f : s.voiceInputVolume;
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
                return s is { profanityFilterEnabled: true };
            }
            set {
                var s = GameSettings.Data.social;
                if(s == null) return;
                s.profanityFilterEnabled = value;
                Save();
            }
        }

        // --- Block/Mute ---
        private static List<string> mutedList; // Use List to maintain order for LRU-style cap
        private static HashSet<string> mutedCache;
        private static HashSet<string> blockedCache;

        public static bool IsMuted(string playerId) {
            if(mutedCache == null) LoadLists();
            return mutedCache != null && (mutedCache.Contains(playerId) || IsBlocked(playerId)); // Block implies mute
        }

        public static bool IsBlocked(string playerId) {
            if(blockedCache == null) LoadLists();
            return blockedCache != null && blockedCache.Contains(playerId);
        }

        public static void SetMuted(string playerId, bool muted) {
            if(mutedCache == null) LoadLists();
            
            if(muted) {
                if (mutedCache != null && !mutedCache.Contains(playerId)) {
                    mutedList.Add(playerId);
                    mutedCache.Add(playerId);
                    
                    // Cap the list to prevent unbounded growth
                    while (mutedList.Count > MaxMutedPlayers) {
                        var oldest = mutedList[0];
                        mutedList.RemoveAt(0);
                        mutedCache.Remove(oldest);
                    }
                }
            } else {
                mutedList.Remove(playerId);
                if(mutedCache != null) mutedCache.Remove(playerId);
            }
            
            SaveLists();
            EventBus.Publish(new PlayerMuteChangedEvent(playerId, muted));
        }

        public static void SetBlocked(string playerId, bool blocked) {
            if(blockedCache == null) LoadLists();
            if(blocked) {
                if(blockedCache != null) blockedCache.Add(playerId);
                SetMuted(playerId, true); // Also mute them
            } else {
                if(blockedCache != null) blockedCache.Remove(playerId);
            }
            SaveLists();
        }

        private static void LoadLists() {
            var s = GameSettings.Data.social;
            if(s == null) {
                mutedList = new List<string>();
                mutedCache = new HashSet<string>();
                blockedCache = new HashSet<string>();
                return;
            }

            mutedList = s.mutedPlayers != null ? new List<string>(s.mutedPlayers) : new List<string>();
            mutedCache = new HashSet<string>(mutedList);
            blockedCache = s.blockedPlayers != null ? new HashSet<string>(s.blockedPlayers) : new HashSet<string>();
        }

        private static void SaveLists() {
            var s = GameSettings.Data.social;
            if(s == null) return;
            if(s.mutedPlayers == null) s.mutedPlayers = new List<string>();
            if(s.blockedPlayers == null) s.blockedPlayers = new List<string>();

            s.mutedPlayers.Clear();
            foreach(var t in mutedList) {
                s.mutedPlayers.Add(t);
            }

            s.blockedPlayers.Clear();
            foreach(var id in blockedCache) {
                s.blockedPlayers.Add(id);
            }
            Save();
        }

        private static void Save() {
            GameSettings.Save();
            EventBus.Publish(new SocialSettingsChangedEvent());
        }
    }
}
