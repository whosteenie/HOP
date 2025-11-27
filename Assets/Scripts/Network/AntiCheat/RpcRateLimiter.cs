using System.Collections.Generic;
using UnityEngine;

namespace Network.AntiCheat {
    public static class RpcRateLimiter {
        public static class Keys {
            public const string Damage = "DamageRPC";
            public const string WorldSfx = "WorldSfxRPC";
            public const string WeaponSwitch = "WeaponSwitchRPC";
        }

        private class Entry {
            public float WindowStart;
            public int Count;
        }

        private static readonly Dictionary<ulong, Dictionary<string, Entry>> Cache = new();

        public static bool TryConsume(ulong clientId, string key, int maxCalls, float windowSeconds) {
            if(maxCalls <= 0 || windowSeconds <= 0f) return true;

            if(!Cache.TryGetValue(clientId, out var bucket)) {
                bucket = new Dictionary<string, Entry>();
                Cache[clientId] = bucket;
            }

            if(!bucket.TryGetValue(key, out var entry)) {
                entry = new Entry { WindowStart = Time.unscaledTime, Count = 0 };
                bucket[key] = entry;
            }

            var now = Time.unscaledTime;
            if(now - entry.WindowStart > windowSeconds) {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            if(entry.Count >= maxCalls) {
                return false;
            }

            entry.Count++;
            return true;
        }
    }
}