using System.Collections.Generic;
using UnityEngine;

namespace Network.AntiCheat {
    public static class RpcRateLimiter {
        public struct RpcRateLimitRequest {
            public ulong ClientId { get; set; }
            public string Key { get; set; }
            public int MaxCalls { get; set; }
            public float WindowSeconds { get; set; }
        }

        public static class Keys {
            public const string Damage = "DamageRPC";
            public const string WorldSfx = "WorldSfxRPC";
            public const string WeaponSwitch = "WeaponSwitchRPC";
        }

        private sealed class Entry {
            internal float WindowStart;
            internal int Count;
        }

        private static readonly Dictionary<ulong, Dictionary<string, Entry>> Cache = new();

        public static bool TryConsume(in RpcRateLimitRequest request) {
            if(request.MaxCalls <= 0 || request.WindowSeconds <= 0f) return true;

            if(!Cache.TryGetValue(request.ClientId, out var bucket)) {
                bucket = new Dictionary<string, Entry>();
                Cache[request.ClientId] = bucket;
            }

            if(!bucket.TryGetValue(request.Key, out var entry)) {
                entry = new Entry { WindowStart = Time.unscaledTime, Count = 0 };
                bucket[request.Key] = entry;
            }

            var now = Time.unscaledTime;
            if(now - entry.WindowStart > request.WindowSeconds) {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            if(entry.Count >= request.MaxCalls) {
                return false;
            }

            entry.Count++;
            return true;
        }
    }
}