using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Diagnostics;
using Events;
using Unity.Services.Vivox;
using UnityEngine;
using SessionManager = Network.Session.SessionManager;

namespace Game.Social {
    public struct ChatMessage {
        public ulong SenderSteamId;
        public string SenderName;
        public string MessageContent;
        public bool IsSystemMessage;
    }

    public class ChatManager : MonoBehaviour {
        public static ChatManager Instance { get; private set; }

        private const int VivoxMaxMessageBytes = 320;
        public const int MaxChatInputBytes = 960;

        private const string ChunkMarker = "l";
        private const string ChatLanguageTag = "en-US";
        private const int ChunkAssemblyExpirySeconds = 30;
        private const int PendingSelfEchoExpirySeconds = 15;
        private const int SoftWrapLongTokenLength = 24;
        private bool _isVivoxBound;
        private readonly Dictionary<string, ChunkAssemblyState> _chunkAssemblies = new();
        private readonly Queue<PendingSelfEchoState> _pendingSelfEchoes = new();
        private long _nextPendingSelfEchoId = 1;

        [Serializable]
        private sealed class ChunkEnvelope {
            internal string K;
            internal string ID;
            internal int I;
            internal bool E;
            internal string B;
        }

        private sealed class ChunkAssemblyState {
            internal readonly SortedDictionary<int, string> Chunks = new();
            internal int EndIndex = -1;
            internal float LastUpdatedTime;
        }

        private sealed class PendingSelfEchoState {
            internal long Id;
            internal float LastUpdatedTime;
        }

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
        }

        private void OnEnable() {
            TryBindVivoxEvents();
        }

        private void Update() {
            if(_isVivoxBound) return;
            TryBindVivoxEvents();
        }

        private void OnDisable() {
            UnbindVivoxEvents();
        }

        private void OnDestroy() {
            if(Instance == this) {
                Instance = null;
            }

            UnbindVivoxEvents();
            _chunkAssemblies.Clear();
            _pendingSelfEchoes.Clear();
        }

        private void TryBindVivoxEvents() {
            if(_isVivoxBound) return;
            if(VivoxService.Instance == null) return;
            VivoxService.Instance.ChannelMessageReceived += HandleVivoxChannelMessage;
            _isVivoxBound = true;
        }

        private void UnbindVivoxEvents() {
            if(_isVivoxBound == false) return;
            if(VivoxService.Instance != null) {
                VivoxService.Instance.ChannelMessageReceived -= HandleVivoxChannelMessage;
            }

            _isVivoxBound = false;
        }

        public void SendChatMessage(string message) {
            if(string.IsNullOrWhiteSpace(message)) return;
            _ = SendChatMessageAsync(ClampToUtf8ByteLimit(message.Trim(), MaxChatInputBytes));
        }

        public static void SendLobbyPresenceMessage(string playerName, bool joined) {
            var resolvedName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName.Trim();
            SendSystemMessage($"{resolvedName} {(joined ? "hopped on" : "hopped off")}");
        }

        private async Task SendChatMessageAsync(string message) {
            var trackedPendingEcho = false;
            long pendingEchoId = 0;
            try {
                if(string.IsNullOrWhiteSpace(message)) return;
                message = ClampToUtf8ByteLimit(message, MaxChatInputBytes);
                if(string.IsNullOrWhiteSpace(message)) return;

                if(VivoxService.Instance == null || VoiceManager.Instance == null || !VoiceManager.Instance.IsLoggedIn) {
                    if(!Application.isEditor && Debug.isDebugBuild) {
                        DevLog.LogWarning("[HOPFLOW][VIVOX] VIVOX_TEXT_BLOCKED_NOT_READY reason=NotLoggedInOrServiceMissing");
                    }
                    SendSystemMessage("Chat unavailable.");
                    return;
                }

                string channelName;
                if(SessionManager.Instance != null &&
                   SessionManager.Instance.TryGetActiveVoiceChannelName(out var canonicalChannelName) &&
                   !string.IsNullOrEmpty(canonicalChannelName)) {
                    var canonicalJoined =
                        await VoiceManager.Instance.EnsureChannelJoinedAsync(canonicalChannelName, context: "ChatSend");
                    if(canonicalJoined == false) {
                        if(!Application.isEditor && Debug.isDebugBuild) {
                            DevLog.LogWarning(
                                $"[HOPFLOW][VIVOX] VIVOX_TEXT_BLOCKED_NOT_READY reason=CanonicalJoinFailed channel={canonicalChannelName}");
                        }
                        SendSystemMessage("Chat channel unavailable.");
                        return;
                    }

                    channelName = canonicalChannelName;
                } else if(VoiceManager.Instance.TryGetJoinedChannelName(out channelName) == false) {
                    var joined = await TryEnsureActiveChannelAsync();
                    if(joined == false || VoiceManager.Instance.TryGetJoinedChannelName(out channelName) == false) {
                        if(!Application.isEditor && Debug.isDebugBuild) {
                            DevLog.LogWarning(
                                "[HOPFLOW][VIVOX] VIVOX_TEXT_BLOCKED_NOT_READY reason=NoJoinedChannelAfterEnsure");
                        }
                        SendSystemMessage("Chat channel unavailable.");
                        return;
                    }
                }

                PublishLocalEcho(message);
                pendingEchoId = TrackPendingSelfEcho();
                trackedPendingEcho = true;

                var messageOptions = new MessageOptions {
                    Language = ChatLanguageTag
                };

                if(Encoding.UTF8.GetByteCount(message) <= VivoxMaxMessageBytes) {
                    await VivoxService.Instance.SendChannelTextMessageAsync(channelName, message, messageOptions);
                    return;
                }

                await SendChunkedMessageAsync(channelName, message, messageOptions);
            } catch(Exception ex) {
                if(trackedPendingEcho) {
                    UntrackPendingSelfEcho(pendingEchoId);
                }
                DevLog.LogWarning($"[ChatManager] Failed to send Vivox text message: {ex.Message}");
            }
        }

        private static async Task SendChunkedMessageAsync(string channelName, string fullMessage, MessageOptions messageOptions) {
            if(VivoxService.Instance == null) return;

            var bytes = Encoding.UTF8.GetBytes(fullMessage);
            var messageId = Guid.NewGuid().ToString("N")[..12];
            var offset = 0;
            var chunkIndex = 0;

            while(offset < bytes.Length) {
                var remaining = bytes.Length - offset;
                var chunkLength = remaining;
                string payload;

                while(true) {
                    var isFinalChunk = chunkLength == remaining;
                    payload = BuildChunkPayload(new ChatChunkRequest {
                        MessageId = messageId,
                        ChunkIndex = chunkIndex,
                        IsFinalChunk = isFinalChunk,
                        SourceBytes = bytes,
                        Offset = offset,
                        Length = chunkLength
                    });
                    if(Encoding.UTF8.GetByteCount(payload) <= VivoxMaxMessageBytes) {
                        break;
                    }

                    chunkLength--;
                    if(chunkLength <= 0) {
                        throw new InvalidOperationException("Could not split chat chunk within Vivox byte limit.");
                    }
                }

                await VivoxService.Instance.SendChannelTextMessageAsync(channelName, payload, messageOptions);
                offset += chunkLength;
                chunkIndex++;
            }
        }

        private struct ChatChunkRequest {
            public string MessageId { get; set; }
            public int ChunkIndex { get; set; }
            public bool IsFinalChunk { get; set; }
            public byte[] SourceBytes { get; set; }
            public int Offset { get; set; }
            public int Length { get; set; }
        }

        private static string BuildChunkPayload(in ChatChunkRequest request) {
            return JsonUtility.ToJson(new ChunkEnvelope {
                K = ChunkMarker,
                ID = request.MessageId,
                I = request.ChunkIndex,
                E = request.IsFinalChunk,
                B = Convert.ToBase64String(request.SourceBytes, request.Offset, request.Length)
            });
        }

        public static string ClampToUtf8ByteLimit(string message, int maxBytes) {
            if(string.IsNullOrEmpty(message)) return string.Empty;
            if(maxBytes <= 0) return string.Empty;
            if(Encoding.UTF8.GetByteCount(message) <= maxBytes) return message;

            var end = message.Length;
            while(end > 0) {
                end--;
                var candidate = message[..end];
                if(Encoding.UTF8.GetByteCount(candidate) <= maxBytes) {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static async Task<bool> TryEnsureActiveChannelAsync() {
            if(VoiceManager.Instance == null || SessionManager.Instance == null) return false;
            if(SessionManager.Instance.TryGetActiveVoiceChannelName(out var channelName) == false) return false;
            return await VoiceManager.Instance.EnsureChannelJoinedAsync(channelName, context: "ChatEnsureActive");
        }

        private void HandleVivoxChannelMessage(VivoxMessage vivoxMessage) {
            if(vivoxMessage == null || string.IsNullOrWhiteSpace(vivoxMessage.MessageText)) return;

            if(VoiceManager.Instance != null && VoiceManager.Instance.TryGetJoinedChannelName(out var activeChannel)) {
                if(string.Equals(vivoxMessage.ChannelName, activeChannel, StringComparison.Ordinal) == false) {
                    return;
                }
            }

            var senderId = vivoxMessage.SenderPlayerId;
            if(vivoxMessage.FromSelf == false &&
               string.IsNullOrEmpty(senderId) == false &&
               SocialSettings.IsBlocked(senderId)) {
                return;
            }

            var resolvedMessage = TryResolveChunkedMessage(vivoxMessage);
            if(resolvedMessage == null) {
                return;
            }
            if(vivoxMessage.FromSelf && TryConsumePendingSelfEcho()) {
                return;
            }

            ulong senderSteamId = 0;
            if(string.IsNullOrEmpty(senderId) == false) {
                ulong.TryParse(senderId, out senderSteamId);
            }

            var senderName = string.IsNullOrWhiteSpace(vivoxMessage.SenderDisplayName)
                ? "Player"
                : vivoxMessage.SenderDisplayName;
            var messageText = resolvedMessage;
            if(SocialSettings.ProfanityFilterEnabled) {
                messageText = ChatProfanityFilter.Censor(messageText);
            }

            var chatMsg = new ChatMessage {
                SenderSteamId = senderSteamId,
                SenderName = senderName,
                MessageContent = messageText,
                IsSystemMessage = false
            };

            NotifyMessageReceived(chatMsg);
        }

        private string TryResolveChunkedMessage(VivoxMessage vivoxMessage) {
            CleanupExpiredChunkAssemblies();

            ChunkEnvelope envelope;
            try {
                envelope = JsonUtility.FromJson<ChunkEnvelope>(vivoxMessage.MessageText);
            } catch {
                return vivoxMessage.MessageText;
            }

            if(envelope == null ||
               string.Equals(envelope.K, ChunkMarker, StringComparison.Ordinal) == false ||
               string.IsNullOrWhiteSpace(envelope.ID) ||
               envelope.I < 0 ||
               string.IsNullOrWhiteSpace(envelope.B)) {
                return vivoxMessage.MessageText;
            }

            string chunkText;
            try {
                chunkText = Encoding.UTF8.GetString(Convert.FromBase64String(envelope.B));
            } catch {
                return vivoxMessage.MessageText;
            }

            var senderKey = string.IsNullOrWhiteSpace(vivoxMessage.SenderPlayerId)
                ? "unknown"
                : vivoxMessage.SenderPlayerId;
            var assemblyKey = $"{senderKey}:{envelope.ID}";

            if(_chunkAssemblies.TryGetValue(assemblyKey, out var state) == false) {
                state = new ChunkAssemblyState();
                _chunkAssemblies[assemblyKey] = state;
            }

            state.Chunks[envelope.I] = chunkText;
            state.LastUpdatedTime = Time.unscaledTime;
            if(envelope.E) {
                state.EndIndex = envelope.I;
            }

            if(state.EndIndex < 0) {
                return null;
            }

            for(var idx = 0; idx <= state.EndIndex; idx++) {
                if(state.Chunks.ContainsKey(idx) == false) {
                    return null;
                }
            }

            var builder = new StringBuilder();
            for(var idx = 0; idx <= state.EndIndex; idx++) {
                builder.Append(state.Chunks[idx]);
            }

            _chunkAssemblies.Remove(assemblyKey);
            return builder.ToString();
        }

        private void CleanupExpiredChunkAssemblies() {
            if(_chunkAssemblies.Count == 0) return;

            var now = Time.unscaledTime;
            var staleKeys = new List<string>();
            foreach(var kvp in _chunkAssemblies) {
                if(now - kvp.Value.LastUpdatedTime > ChunkAssemblyExpirySeconds) {
                    staleKeys.Add(kvp.Key);
                }
            }

            foreach(var t in staleKeys) {
                _chunkAssemblies.Remove(t);
            }
        }

        private static void PublishLocalEcho(string message) {
            var displayMessage = message;
            if(SocialSettings.ProfanityFilterEnabled) {
                displayMessage = ChatProfanityFilter.Censor(displayMessage);
            }

            var senderName = "You";
            ulong senderSteamId = 0;
            try {
                senderName = string.IsNullOrWhiteSpace(Steamworks.SteamClient.Name) ? "You" : Steamworks.SteamClient.Name;
                senderSteamId = Steamworks.SteamClient.SteamId;
            } catch {
                // Steam can be unavailable in editor/offline contexts.
            }

            NotifyMessageReceived(new ChatMessage {
                SenderSteamId = senderSteamId,
                SenderName = senderName,
                MessageContent = displayMessage,
                IsSystemMessage = false
            });
        }

        private long TrackPendingSelfEcho() {
            CleanupPendingSelfEchoes();
            var id = _nextPendingSelfEchoId++;
            _pendingSelfEchoes.Enqueue(new PendingSelfEchoState {
                Id = id,
                LastUpdatedTime = Time.unscaledTime
            });
            return id;
        }

        private bool TryConsumePendingSelfEcho() {
            CleanupPendingSelfEchoes();
            if(_pendingSelfEchoes.Count == 0) return false;
            _pendingSelfEchoes.Dequeue();
            return true;
        }

        private void CleanupPendingSelfEchoes() {
            if(_pendingSelfEchoes.Count == 0) return;

            var now = Time.unscaledTime;
            while(_pendingSelfEchoes.Count > 0) {
                var peek = _pendingSelfEchoes.Peek();
                if(now - peek.LastUpdatedTime <= PendingSelfEchoExpirySeconds) break;
                _pendingSelfEchoes.Dequeue();
            }
        }

        private void UntrackPendingSelfEcho(long pendingEchoId) {
            if(pendingEchoId <= 0 || _pendingSelfEchoes.Count == 0) return;

            var retained = new Queue<PendingSelfEchoState>(_pendingSelfEchoes.Count);
            while(_pendingSelfEchoes.Count > 0) {
                var entry = _pendingSelfEchoes.Dequeue();
                if(entry.Id == pendingEchoId) continue;
                retained.Enqueue(entry);
            }

            while(retained.Count > 0) {
                _pendingSelfEchoes.Enqueue(retained.Dequeue());
            }
        }

        public static string InsertSoftWrapBreaks(string message) {
            if(string.IsNullOrEmpty(message)) return string.Empty;

            var builder = new StringBuilder(message.Length + message.Length / 4);
            var runBuilder = new StringBuilder();

            foreach(var c in message) {
                if(char.IsWhiteSpace(c)) {
                    FlushRun();
                    builder.Append(c);
                    continue;
                }

                runBuilder.Append(c);
            }

            FlushRun();
            return builder.ToString();

            void FlushRun() {
                switch(runBuilder.Length) {
                    case 0:
                        return;
                    case >= SoftWrapLongTokenLength: {
                        for(var j = 0; j < runBuilder.Length; j++) {
                            builder.Append(runBuilder[j]);
                            builder.Append('\u200B');
                        }

                        break;
                    }
                    default:
                        builder.Append(runBuilder);
                        break;
                }

                runBuilder.Length = 0;
            }
        }

        private static void SendSystemMessage(string message) {
            var chatMsg = new ChatMessage {
                SenderName = "SYSTEM",
                MessageContent = message,
                IsSystemMessage = true
            };
            NotifyMessageReceived(chatMsg);
        }

        private static void NotifyMessageReceived(ChatMessage message) {
            EventBus.Publish(new ChatMessageReceivedEvent(
                message.SenderSteamId,
                message.SenderName,
                message.MessageContent,
                message.IsSystemMessage));
        }
    }
}
