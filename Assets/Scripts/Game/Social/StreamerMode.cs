using System;
using Game.Settings;
using UnityEngine;
using Steamworks;

namespace Game.Social {
    /// <summary>
    /// Lightweight streamer/privacy mode.
    /// Enable via command line: -streamerMode
    /// </summary>
    public static class StreamerMode {
        private static bool _initialized;
        private static bool _enabledFromArgs;
        private static string _localDisplayName;

        public static bool Enabled {
            get {
                EnsureInitialized();
                if(_enabledFromArgs) return true;
                var data = GameSettings.Data;
                if(data.social == null) return false;
                return data.social.streamerModeEnabled;
            }
        }

        public static string LocalDisplayName {
            get {
                EnsureInitialized();
                return _localDisplayName;
            }
        }

        public static string GetLocalDisplayName() {
            EnsureInitialized();

            var steamOnline = SteamClient.IsValid && SteamClient.IsLoggedOn;
            if(!steamOnline) return _localDisplayName;

            if(Enabled) return _localDisplayName;
            return SteamClient.Name;
        }

        private static void EnsureInitialized() {
            if(_initialized) return;
            _initialized = true;

            _enabledFromArgs = HasArg("-streamerMode") || HasArg("-streamer");
            _localDisplayName = GenerateName();
        }

        private static bool HasArg(string arg) {
            var args = Environment.GetCommandLineArgs();
            for(var i = 0; i < args.Length; i++) {
                if(args[i] == arg) return true;
            }
            return false;
        }

        private static string GenerateName() {
            // Keep lists small and safe. Expand later if desired.
            var adjectives = new[] {
                "Brisk", "Clever", "Cosmic", "Dapper", "Electric", "Fuzzy", "Gentle", "Icy", "Lucky", "Neon",
                "Nimble", "Rusty", "Silent", "Swift", "Vivid", "Wild"
            };
            var nouns = new[] {
                "Falcon", "Comet", "Warden", "Ranger", "Fox", "Viper", "Otter", "Hawk", "Ghost", "Nova",
                "Circuit", "Beacon", "Glider", "Drifter", "Pilot", "Sprinter"
            };

            // Deterministic per-launch.
            var seed = (int)(DateTime.UtcNow.Ticks & 0x7fffffff);
            var rng = new System.Random(seed);
            var adj = adjectives[rng.Next(0, adjectives.Length)];
            var noun = nouns[rng.Next(0, nouns.Length)];
            var number = rng.Next(10, 100);
            return $"{adj}{noun}{number}";
        }
    }
}

