using System;
using Game.Settings;
using Steamworks;

namespace Game.Social {
    /// <summary>
    /// Lightweight streamer/privacy mode.
    /// Enable via command line: -streamerMode
    /// </summary>
    public static class StreamerMode {
        private static bool initialized;
        private static bool enabledFromArgs;
        private static string localDisplayName;

        public static bool Enabled {
            get {
                EnsureInitialized();
                if(enabledFromArgs) return true;
                var data = GameSettings.Data;
                return data.social is { streamerModeEnabled: true };
            }
        }

        public static string LocalDisplayName {
            get {
                EnsureInitialized();
                return localDisplayName;
            }
        }

        public static string GetLocalDisplayName() {
            EnsureInitialized();

            var steamOnline = SteamClient.IsValid && SteamClient.IsLoggedOn;
            if(!steamOnline) return localDisplayName;

            return Enabled ? localDisplayName : SteamClient.Name;
        }

        private static void EnsureInitialized() {
            if(initialized) return;
            initialized = true;

            enabledFromArgs = HasArg("-streamerMode") || HasArg("-streamer");
            localDisplayName = GenerateName();
        }

        private static bool HasArg(string arg) {
            var args = Environment.GetCommandLineArgs();
            foreach(var t in args) {
                if(t == arg) return true;
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
            var rng = new Random(seed);
            var adj = adjectives[rng.Next(0, adjectives.Length)];
            var noun = nouns[rng.Next(0, nouns.Length)];
            var number = rng.Next(10, 100);
            return $"{adj}{noun}{number}";
        }
    }
}

