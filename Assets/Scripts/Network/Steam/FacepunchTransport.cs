using System;
using System.Collections.Generic;
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
        public ulong targetSteamId = 0;

        private SocketManager _socketManager;
        private ConnectionManager _connectionManager;

        // Map internal NGO ClientID (ulong) -> Steam Connection
        // Warning: NGO ClientIDs are assigned by the Server Transport. 
        // We need to map ConnectionId (uint) from Steam to ClientId (ulong) for NGO.
        // Actually, NGO lets Transport define the ClientID. We can usually use the SteamID or valid uint.
        // However, Steam Connections are internal uint handles.

        private readonly Dictionary<ulong, Connection> _connectedClients = new Dictionary<ulong, Connection>();
        private readonly Dictionary<Connection, ulong> _steamToNgoId = new Dictionary<Connection, ulong>();

        public override bool IsSupported => true;

        public override void Initialize(NetworkManager networkManager = null) {
            // Steam should be initialized by SteamManager
        }

        public override void Shutdown() {
            _socketManager?.Close();
            _socketManager = null;
            _connectionManager?.Close();
            _connectionManager = null;
            _connectedClients.Clear();
            _steamToNgoId.Clear();
            Debug.Log("[FacepunchTransport] Shutdown.");
        }

        // ================= SERVER =================

        public override bool StartServer() {
            try {
                _socketManager = SteamNetworkingSockets.CreateRelaySocket<FacepunchSocketManager>(0);
                ((FacepunchSocketManager)_socketManager).Transport = this;
                Debug.Log("[FacepunchTransport] Server started.");
                return true;
            } catch (Exception e) {
                Debug.LogError($"[FacepunchTransport] Failed to start server: {e.Message}");
                return false;
            }
        }

        public void OnConnectionCreated(Connection connection, ConnectionInfo info) {
            // connection.Id is a uint handle. We can use it as ClientId.
            ulong clientId = connection.Id;
            Debug.Log($"[FacepunchTransport] Client connecting: {info.Identity.SteamId} (Handle: {clientId})");
            connection.Accept();
            
            // Just map handle
            if (!_connectedClients.ContainsKey(clientId)) {
                _connectedClients.Add(clientId, connection);
            }
        }

        public void OnConnectionDisconnected(Connection connection, ConnectionInfo info) {
            ulong clientId = connection.Id;
            Debug.Log($"[FacepunchTransport] Client disconnected: {info.Identity.SteamId} (Handle: {clientId})");
            _connectedClients.Remove(clientId);
            
            // Queue disconnect event for NGO
            // (Handled in PollEvent usually, or we can just let PollEvent pick it up if we queue an internal event)
            // But SocketManager OnDisconnected is called during Update loop mainly.
        }

        // ================= CLIENT =================

        public override bool StartClient() {
            try {
                if (targetSteamId == 0) {
                    Debug.LogError("[FacepunchTransport] Target SteamID is 0!");
                    return false;
                }

                var identity = (SteamId)targetSteamId;
                _connectionManager = SteamNetworkingSockets.ConnectRelay<FacepunchConnectionManager>(identity, 0);
                ((FacepunchConnectionManager)_connectionManager).Transport = this;
                Debug.Log($"[FacepunchTransport] Connecting to {targetSteamId}...");
                return true;
            } catch (Exception e) {
                Debug.LogError($"[FacepunchTransport] Failed to start client: {e.Message}");
                return false;
            }
        }

        public override ulong ServerClientId => 0; // Usually 0 in NGO for server

        public override void DisconnectRemoteClient(ulong clientId) {
            if (_connectedClients.TryGetValue(clientId, out var connection)) {
                connection.Close();
                _connectedClients.Remove(clientId);
            }
        }

        public override void DisconnectLocalClient() {
            _connectionManager?.Connection.Close();
            _connectionManager = null;
        }

        public override ulong GetCurrentRtt(ulong clientId) {
            // Facepunch/Steamworks might exposes Ping.
            // For now return 0 or look up connection info.
            return 0; // TODO impl
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery delivery) {
            // Send to specific client (Server -> Client)
            if (_socketManager != null) {
                if (_connectedClients.TryGetValue(clientId, out var connection)) {
                    SendToConnection(connection, payload, delivery);
                }
            }
            // Send to server (Client -> Server)
            else if (_connectionManager != null && _connectionManager.Connected) {
                SendToConnection(_connectionManager.Connection, payload, delivery);
            }
        }

        private void SendToConnection(Connection connection, ArraySegment<byte> payload, NetworkDelivery delivery) {
            var sendType = delivery == NetworkDelivery.Reliable ? SendType.Reliable : SendType.Unreliable;
            
            // Copy payload to straight byte array or usage appropriate pointer
            // Optimization: Steamworks accepts IntPtr or byte[]. 
            // ArraySegment needs to be correctly handled.
            
            if (payload.Array == null) return;
            
            // Quick copy if offset is non-zero
            byte[] data;
            if (payload.Offset == 0 && payload.Count == payload.Array.Length) {
                data = payload.Array;
            } else {
                data = new byte[payload.Count];
                Buffer.BlockCopy(payload.Array, payload.Offset, data, 0, payload.Count);
            }

            // connection.SendMessage(data, sendType); // Old API?
            // Updated Facepunch API 2.4.0+:
             Result res = connection.SendMessage(data, sendType);
             if (res != Result.OK) {
                 // Debug.LogWarning($"Send failed: {res}");
             }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime) {
            clientId = 0;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;

            // --- Server Polling ---
            if (_socketManager != null) {
                // Use the custom SocketManager to queue events?
                // Actually, standard pattern is to have the Manager override OnMessage and queue it up.
                ((FacepunchSocketManager)_socketManager).Update(); // Manually pump if needed or relies on SteamClient callback? 
                // Wait, Sockets define their own polling.
                
                // We need to pop from our internal queue that handlers filled
                if (((FacepunchSocketManager)_socketManager).TryDequeue(out var evt)) {
                    clientId = evt.ClientId;
                    payload = evt.Payload;
                    return evt.Type;
                }
            }
            // --- Client Polling ---
            else if (_connectionManager != null) {
                 ((FacepunchConnectionManager)_connectionManager).Update();
                 if (((FacepunchConnectionManager)_connectionManager).TryDequeue(out var evt)) {
                     clientId = 0; // From Server
                     payload = evt.Payload;
                     return evt.Type;
                 }
            }

            return NetworkEvent.Nothing;
        }

        // --- Internal Classes to handle Callbacks ---
        
        public struct TransportEvent {
            public NetworkEvent Type;
            public ulong ClientId;
            public ArraySegment<byte> Payload;
        }

        public class FacepunchSocketManager : SocketManager {
            public FacepunchTransport Transport;
            private Queue<TransportEvent> eventQueue = new Queue<TransportEvent>();

            public override void OnConnecting(Connection connection, ConnectionInfo data) {
                 Transport.OnConnectionCreated(connection, data);
            }

            public override void OnConnected(Connection connection, ConnectionInfo data) {
                 // Connection fully established
                 // NGO needs a "Connect" event.
                 eventQueue.Enqueue(new TransportEvent {
                     Type = NetworkEvent.Connect,
                     ClientId = connection.Id
                 });
            }

            public override void OnDisconnected(Connection connection, ConnectionInfo data) {
                Transport.OnConnectionDisconnected(connection, data);
                eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Disconnect,
                    ClientId = connection.Id
                });
            }

            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data, int size, long messageNum, long recvTime, int channel) {
                // Copy data
                byte[] buffer = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);
                
                eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Data,
                    ClientId = connection.Id,
                    Payload = new ArraySegment<byte>(buffer)
                });
            }

            public bool TryDequeue(out TransportEvent evt) {
                if (eventQueue.Count > 0) {
                    evt = eventQueue.Dequeue();
                    return true;
                }
                evt = default;
                return false;
            }
            
            public void Update() {
                Receive(); // Process messages
            }
        }

        public class FacepunchConnectionManager : ConnectionManager {
            public FacepunchTransport Transport;
            private Queue<TransportEvent> eventQueue = new Queue<TransportEvent>();

            public override void OnConnected(ConnectionInfo data) {
                // Connected to server
                eventQueue.Enqueue(new TransportEvent { Type = NetworkEvent.Connect });
            }

            public override void OnDisconnected(ConnectionInfo data) {
                eventQueue.Enqueue(new TransportEvent { Type = NetworkEvent.Disconnect });
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel) {
                byte[] buffer = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(data, buffer, 0, size);

                eventQueue.Enqueue(new TransportEvent {
                    Type = NetworkEvent.Data,
                    Payload = new ArraySegment<byte>(buffer)
                });
            }

            public bool TryDequeue(out TransportEvent evt) {
                if (eventQueue.Count > 0) {
                    evt = eventQueue.Dequeue();
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
