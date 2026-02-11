using System;
using Unity.Netcode;
using Game.Player;

namespace Game.Social {
    public struct ChatMessage : INetworkSerializable {
        public ulong SenderClientId;
        public ulong SenderSteamId;
        public string SenderName;
        public string MessageContent;
        public bool IsSystemMessage;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter {
            serializer.SerializeValue(ref SenderClientId);
            serializer.SerializeValue(ref SenderSteamId);
            serializer.SerializeValue(ref SenderName);
            serializer.SerializeValue(ref MessageContent);
            serializer.SerializeValue(ref IsSystemMessage);
        }
    }

    public class ChatManager : NetworkBehaviour {
        public static ChatManager Instance { get; private set; }

        public event Action<ChatMessage> OnMessageReceived;

        private void Awake() {
            if (Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        public void SendChatMessage(string message) {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (!IsSpawned) return;

            ulong mySteamId = Steamworks.SteamClient.SteamId; 
            // Send to server
            SendChatMessageServerRpc(mySteamId, message);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void SendChatMessageServerRpc(ulong steamId, string message, RpcParams rpcParams = default) {
            var senderId = rpcParams.Receive.SenderClientId;
            
            // Get Sender Name (Assuming PlayerController or Identity exists)
            var senderName = $"Player {senderId}";
            
            // Try to find actual name from NetworkManager or Player objects
            if (NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out var client)) {
                var playerObj = client.PlayerObject;
                if(playerObj != null) {
                    // Try to get actual name from PlayerController NetworkVariable
                    senderName = playerObj.TryGetComponent<PlayerController>(out var pc) ? pc.PlayerName.Value.ToString() : playerObj.name;
                }
            }

            // Broadcast to all
            ReceiveChatMessageClientRpc(senderId, steamId, senderName, message);
        }

        [ClientRpc]
        private void ReceiveChatMessageClientRpc(ulong senderClientId, ulong senderSteamId, string senderName, string message) {
            // Check Blocked
            if(SocialSettings.IsBlocked(senderSteamId.ToString())) return;
            
            // Profanity Filter
            var displayMessage = message;
            if (SocialSettings.ProfanityFilterEnabled) {
                displayMessage = ApplyProfanityFilter(message);
            }

            var chatMsg = new ChatMessage {
                SenderClientId = senderClientId,
                SenderSteamId = senderSteamId,
                SenderName = senderName,
                MessageContent = displayMessage,
                IsSystemMessage = false
            };

            OnMessageReceived?.Invoke(chatMsg);
        }

        public void SendSystemMessage(string message) {
            // Local only system message
            var chatMsg = new ChatMessage {
                SenderClientId = 0,
                SenderName = "SYSTEM",
                MessageContent = message,
                IsSystemMessage = true
            };
            OnMessageReceived?.Invoke(chatMsg);
        }

        private static string ApplyProfanityFilter(string input) {
            // Very basic placeholder filter
            string[] badWords = { "badword", "swear" }; 
            var filtered = input;
            foreach (var word in badWords) {
                var replacement = new string('*', word.Length);
                filtered = filtered.Replace(word, replacement, StringComparison.OrdinalIgnoreCase);
            }
            return filtered;
        }
    }
}
