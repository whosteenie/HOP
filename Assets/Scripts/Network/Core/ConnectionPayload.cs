using System;
using System.Text;
using UnityEngine;

namespace Network.Core {
    [Serializable]
    public sealed class ConnectionPayload {
        public string partyId;
        public bool isPrivateMatch;
        public ulong steamId;
        public string ugsPlayerId;
        public string displayName;

        public static byte[] Encode(ConnectionPayload payload) {
            if(payload == null) return Array.Empty<byte>();
            var json = JsonUtility.ToJson(payload);
            if(string.IsNullOrEmpty(json)) return Array.Empty<byte>();
            return Encoding.UTF8.GetBytes(json);
        }

        public static ConnectionPayload Decode(byte[] data) {
            if(data == null) return null;
            if(data.Length == 0) return null;

            try {
                var json = Encoding.UTF8.GetString(data);
                if(string.IsNullOrEmpty(json)) return null;
                return JsonUtility.FromJson<ConnectionPayload>(json);
            } catch {
                return null;
            }
        }
    }
}

