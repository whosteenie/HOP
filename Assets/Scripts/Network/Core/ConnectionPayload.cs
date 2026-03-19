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

        public static byte[] Encode(ConnectionPayload payload) {
            if(payload == null) return Array.Empty<byte>();
            var json = JsonUtility.ToJson(payload);
            return string.IsNullOrEmpty(json) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        }

        public static ConnectionPayload Decode(byte[] data) {
            if(data == null) return null;
            if(data.Length == 0) return null;

            try {
                var json = Encoding.UTF8.GetString(data);
                return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<ConnectionPayload>(json);
            } catch {
                return null;
            }
        }
    }
}

