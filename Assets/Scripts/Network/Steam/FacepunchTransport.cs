using System;
using System.Collections.Generic;
using Diagnostics;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

namespace Network.Steam {
    /// <summary>
    /// A Transport for Netcode for GameObjects that uses SteamNetworkingSockets via Facepunch.Steamworks.
    /// Supports P2P connections via SteamID.
    /// </summary>
    public class FacepunchTransport : NetworkTransport {
        public ulong targetSteamId;

        private SocketManager _socketManager;
        private ConnectionManager _connectionManager;

        private readonly Dictionary<ulong, Connection> _connectedClients = new();

        public override bool IsSupported => true;

        public override void Initialize(NetworkManager networkManager = null) {
            // Steam should be initialized by SteamManager
        }

        public override void Shutdown() {
            if(_socketManager != null) {
                _socketManager.Close();
            }
            _socketManager = null;
            if(_connectionManager != null) {
                _connectionManager.Close();
            }
            _connectionManager = null;
            _connectedClients.Clear();
            DevLog.Log("[FacepunchTransport] Shutdown.");
        }

        // ================= SERVER =================

        public override bool StartServer() {
            try {
                _socketManager = SteamNetworkingSockets.CreateRelaySocket<FacepunchSocketManager>();
                ((FacepunchSocketManager)_socketManager).Transport = this;
                DevLog.Log("[FacepunchTransport] Server started.");
                return true;
            } catch(Exception e) {
                DevLog.LogError($"[FacepunchTransport] Failed to start server: {e.Message}");
                return false;
            }
        }

        private void OnConnectionCreated(Connection connection, ConnectionInfo info) {
            // connection.ID is a uint handle. We can use it as ClientId.
            ulong clientId = connection.Id;
            DevLog.Log($"[FacepunchTransport] Client connecting: {info.Identity.SteamId} (Handle: {clientId})");
            connection.Accept();

            // Just map handle
            _connectedClients.TryAdd(clientId, connection);
        }

        private void OnConnectionDisconnected(Connection connection, ConnectionInfo info) {
            ulong clientId = connection.Id;
            DevLog.Log($"[FacepunchTransport] Client disconnected: {info.Identity.SteamId} (Handle: {clientId})");
            _connectedClients.Remove(clientId);

            // Queue disconnect event for NGO
            // (Handled in PollEvent usually, or we can just let PollEvent pick it up if we queue an internal event)
            // But SocketManager OnDisconnected is called during Update loop mainly.
        }

        // ================= CLIENT =================

        public override bool StartClient() {
            try {
                if(targetSteamId == 0) {
                    DevLog.LogError("[FacepunchTransport] Target SteamID is 0!");
                    return false;
                }

                var identity = (SteamId)targetSteamId;
                _connectionManager = SteamNetworkingSockets.ConnectRelay<FacepunchConnectionManager>(identity);
                DevLog.Log($"[FacepunchTransport] Connecting to {targetSteamId}...");
                return true;
            } catch(Exception e) {
                DevLog.LogError($"[FacepunchTransport] Failed to start client: {e.Message}");
                return false;
            }
        }

        public override ulong ServerClientId => 0; // Usually 0 in NGO for server

        public override void DisconnectRemoteClient(ulong clientId) {
            if(!_connectedClients.TryGetValue(clientId, out var connection)) return;
            connection.Close();
            _connectedClients.Remove(clientId);
        }

        public override void DisconnectLocalClient() {
            if(_connectionManager != null) {
                _connectionManager.Connection.Close();
            }
            _connectionManager = null;
        }

        public override ulong GetCurrentRtt(ulong clientId) {
            // For Server: Look up client connection
            if (_socketManager != null) {
                if(!_connectedClients.TryGetValue(clientId, out var connection)) return 0;
                var status = connection.QuickStatus();
                return (ulong)Mathf.Max(0, status.Ping);
            }
            // For Client: Return ping to server

            if(_connectionManager == null || clientId != ServerClientId) return 0;
            {
                var status = _connectionManager.Connection.QuickStatus();
                return (ulong)Mathf.Max(0, status.Ping);
            }

        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery) {
            // Send to specific client (Server -> Client)
            if(_socketManager != null) {
                if(_connectedClients.TryGetValue(clientId, out var connection)) {
                    SendToConnection(connection, payload, delivery);
                }
            }
            // Send to server (Client -> Server)
            else if(_connectionManager is { Connected: true }) {
                SendToConnection(_connectionManager.Connection, payload, delivery);
            }
        }

        private static void SendToConnection(Connection connection, ArraySegment<byte> payload, NetworkDelivery delivery) {
            var sendType = delivery == NetworkDelivery.Reliable ? SendType.Reliable : SendType.Unreliable;

            // Copy payload to straight byte array or usage appropriate pointer
            // Optimization: Steamworks accepts IntPtr or byte[]. 
            // ArraySegment needs to be correctly handled.

            if(payload.Array == null) return;

            // Quick copy if offset is non-zero
            byte[] data;
            if(payload.Offset == 0 && payload.Count == payload.Array.Length) {
                data = payload.Array;
            } else {
                data = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, data, 0, payload.Count);
            }

            // connection.SendMessage(data, sendType); // Old API?
            // Updated Facepunch API 2.4.0+:
            var res = connection.SendMessage(data, sendType);
            if(res != Result.OK) {
                // DevLog.LogWarning($"Send failed: {res}");
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload,
            out float receiveTime) {
            clientId = 0;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;

            // --- Server Polling ---
            if(_socketManager != null) {
                // Use the custom SocketManager to queue events?
                // Actually, standard pattern is to have the Manager override OnMessage and queue it up.
                ((FacepunchSocketManager)_socketManager)
                    .Update(); // Manually pump if needed or relies on SteamClient callback? 
                // Wait, Sockets define their own polling.

                // We need to pop from our internal queue that handlers filled
                if(!((FacepunchSocketManager)_socketManager).TryDequeue(out var evt)) return NetworkEvent.Nothing;
                clientId = evt.ClientId;
                payload = evt.Payload;
                return evt.Type;
            }
            // --- Client Polling ---

            if(_connectionManager == null) return NetworkEvent.Nothing;
            {
                ((FacepunchConnectionManager)_connectionManager).Update();
                if(!((FacepunchConnectionManager)_connectionManager).TryDequeue(out var evt))
                    return NetworkEvent.Nothing;
                clientId = 0; // From Server
                payload = evt.Payload;
                return evt.Type;
            }

        }

        // --- Internal Classes to handle Callbacks ---

        private struct TransportEvent {
            public NetworkEvent Type;
            public ulong ClientId;
            public ArraySegment<byte> Payload;
        }

        private class FacepunchSocketManager : SocketManager {
            public FacepunchTransport Transport;
            private readonly Queue<TransportEvent> _eventQueue = new();

            public override void OnConnecting(Connection connection, ConnectionInfo data) {
                Transport.OnConnectionCreated(connection, data);
            }

            public override void OnConnected(Connection connection, ConnectionInfo data) {
                // Connection fully established
                // NGO needs a "Connect" event.
                _eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Connect,
                    ClientId = connection.Id
                });
            }

            public override void OnDisconnected(Connection connection, ConnectionInfo data) {
                Transport.OnConnectionDisconnected(connection, data);
                _eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Disconnect,
                    ClientId = connection.Id
                });
            }

            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size,
                long messageNum, long recvTime, int channel) {
                // Copy data
                var buffer = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);

                _eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Data,
                    ClientId = connection.Id,
                    Payload = new ArraySegment<byte>(buffer)
                });
            }

            public bool TryDequeue(out TransportEvent evt) {
                if(_eventQueue.Count > 0) {
                    evt = _eventQueue.Dequeue();
                    return true;
                }

                evt = default;
                return false;
            }

            public void Update() {
                Receive(); // Process messages
            }
        }

        private class FacepunchConnectionManager : ConnectionManager {
            private readonly Queue<TransportEvent> _eventQueue = new();

            public override void OnConnected(ConnectionInfo data) {
                // Connected to server
                _eventQueue.Enqueue(new TransportEvent { Type = NetworkEvent.Connect });
            }

            public override void OnDisconnected(ConnectionInfo data) {
                _eventQueue.Enqueue(new TransportEvent { Type = NetworkEvent.Disconnect });
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
                var buffer = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);

                _eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Data,
                    Payload = new ArraySegment<byte>(buffer)
                });
            }

            public bool TryDequeue(out TransportEvent evt) {
                if(_eventQueue.Count > 0) {
                    evt = _eventQueue.Dequeue();
                    return true;
                }

                evt = default;
                return false;
            }

            public void Update() {
                Receive(); // Pump
            }
        }
    }
}