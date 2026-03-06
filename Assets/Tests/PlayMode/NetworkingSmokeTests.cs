using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests.PlayMode {
    public class NetworkingSmokeTests {
        private GameObject _hostObject;
        private GameObject _clientObject;
        private NetworkManager _hostManager;
        private NetworkManager _clientManager;
        private GameObject _replicatedPrefab;

        private class ReplicatedIntBehaviour : NetworkBehaviour {
            public readonly NetworkVariable<int> IntValue = new();
        }

        private class RpcRoundtripBehaviour : NetworkBehaviour {
            public int LastAckPayload { get; private set; } = -1;
            public int LastAckOrder { get; private set; } = -1;
            public bool AckReceived { get; private set; }

            [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
            public void SubmitPayloadRpc(int payload, int expectedOrder) {
                AckClientRpc(payload, expectedOrder);
            }

            [Rpc(SendTo.Everyone)]
            private void AckClientRpc(int payload, int order) {
                LastAckPayload = payload;
                LastAckOrder = order;
                AckReceived = true;
            }
        }

        private class AuthorityProbeBehaviour : NetworkBehaviour {
            public static int OwnerOnlyRpcCalls;
            public static int EveryoneRpcCalls;

            [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Owner)]
            public void OwnerOnlyRpc() {
                OwnerOnlyRpcCalls++;
            }

            [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
            public void EveryoneRpc() {
                EveryoneRpcCalls++;
            }

            public static void ResetCounters() {
                OwnerOnlyRpcCalls = 0;
                EveryoneRpcCalls = 0;
            }
        }

        [TearDown]
        public void TearDown() {
            if(_clientManager != null && _clientManager.IsListening) {
                _clientManager.Shutdown();
            }

            if(_hostManager != null && _hostManager.IsListening) {
                _hostManager.Shutdown();
            }

            if(_clientObject != null) {
                Object.DestroyImmediate(_clientObject);
            }

            if(_hostObject != null) {
                Object.DestroyImmediate(_hostObject);
            }

            if(_replicatedPrefab != null) {
                Object.DestroyImmediate(_replicatedPrefab);
            }

            _clientManager = null;
            _hostManager = null;
            _clientObject = null;
            _hostObject = null;
            _replicatedPrefab = null;

            AuthorityProbeBehaviour.ResetCounters();
        }

        [UnityTest]
        public IEnumerator Host_Starts_And_ShutsDown_Cleanly() {
            var port = (ushort)Random.Range(19000, 22000);
            _hostManager = CreateManager("PlayModeHostOnly", port);

            var started = false;
            void OnServerStarted() => started = true;
            _hostManager.OnServerStarted += OnServerStarted;

            Assert.That(_hostManager.StartHost(), Is.True, "NetworkManager should start host in PlayMode smoke test.");

            var timeoutAt = Time.realtimeSinceStartup + 8f;
            while(!started && Time.realtimeSinceStartup < timeoutAt) {
                yield return null;
            }

            _hostManager.OnServerStarted -= OnServerStarted;

            Assert.That(started, Is.True, "Host should raise OnServerStarted callback.");
            Assert.That(_hostManager.IsListening, Is.True);
            Assert.That(_hostManager.IsServer, Is.True);

            _hostManager.Shutdown();
            yield return null;

            Assert.That(_hostManager.IsListening, Is.False);
        }

        [UnityTest]
        public IEnumerator HostAndClient_Connect_Cleanly() {
            yield return StartHostAndClient();

            var timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                var hostHasTwoConnections = _hostManager.ConnectedClients.Count >= 2;
                var clientConnected = _clientManager.IsConnectedClient;
                if(hostHasTwoConnections && clientConnected) {
                    break;
                }
                yield return null;
            }

            Assert.That(_hostManager.ConnectedClients.Count, Is.GreaterThanOrEqualTo(2),
                "Host should have itself + one remote client connected.");
            Assert.That(_clientManager.IsConnectedClient, Is.True, "Client should report connected state.");
        }

        [UnityTest]
        public IEnumerator SpawnedNetworkObject_ReplicatesToClient() {
            SetupReplicatedPrefab();
            yield return StartHostAndClient();

            var hostInstance = Object.Instantiate(_replicatedPrefab);
            var hostNetworkObject = hostInstance.GetComponent<NetworkObject>();
            Assert.That(hostNetworkObject, Is.Not.Null);
            hostNetworkObject.Spawn();

            var spawnedId = hostNetworkObject.NetworkObjectId;
            var timeoutAt = Time.realtimeSinceStartup + 10f;
            var foundOnClient = false;

            while(Time.realtimeSinceStartup < timeoutAt) {
                if(_clientManager.SpawnManager.SpawnedObjects.ContainsKey(spawnedId)) {
                    foundOnClient = true;
                    break;
                }
                yield return null;
            }

            Assert.That(foundOnClient, Is.True, "Client should observe host-spawned network object.");
        }

        [UnityTest]
        public IEnumerator NetworkVariable_ReplicatesFromHostToClient() {
            SetupReplicatedPrefab();
            yield return StartHostAndClient();

            var hostInstance = Object.Instantiate(_replicatedPrefab);
            var hostBehaviour = hostInstance.GetComponent<ReplicatedIntBehaviour>();
            var hostNetworkObject = hostInstance.GetComponent<NetworkObject>();
            Assert.That(hostBehaviour, Is.Not.Null);
            Assert.That(hostNetworkObject, Is.Not.Null);

            hostNetworkObject.Spawn();
            hostBehaviour.IntValue.Value = 42;

            var spawnedId = hostNetworkObject.NetworkObjectId;
            var timeoutAt = Time.realtimeSinceStartup + 10f;
            var replicated = false;

            while(Time.realtimeSinceStartup < timeoutAt) {
                if(_clientManager.SpawnManager.SpawnedObjects.TryGetValue(spawnedId, out var clientObject)) {
                    var clientBehaviour = clientObject.GetComponent<ReplicatedIntBehaviour>();
                    if(clientBehaviour != null && clientBehaviour.IntValue.Value == 42) {
                        replicated = true;
                        break;
                    }
                }
                yield return null;
            }

            Assert.That(replicated, Is.True, "Client should receive updated NetworkVariable value from host.");
        }

        [UnityTest]
        public IEnumerator ServerRpc_ClientRpc_Roundtrip_PreservesPayloadAndOrdering() {
            SetupReplicatedPrefab();
            yield return StartHostAndClient();

            var hostInstance = Object.Instantiate(_replicatedPrefab);
            var hostNetworkObject = hostInstance.GetComponent<NetworkObject>();
            hostNetworkObject.Spawn();
            var spawnedId = hostNetworkObject.NetworkObjectId;

            RpcRoundtripBehaviour clientRoundtrip = null;
            var timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                if(_clientManager.SpawnManager.SpawnedObjects.TryGetValue(spawnedId, out var clientObject)) {
                    clientRoundtrip = clientObject.GetComponent<RpcRoundtripBehaviour>();
                    if(clientRoundtrip != null) {
                        break;
                    }
                }
                yield return null;
            }

            Assert.That(clientRoundtrip, Is.Not.Null, "Client should resolve RpcRoundtripBehaviour on spawned object.");

            const int payload = 1337;
            const int order = 1;
            clientRoundtrip.SubmitPayloadRpc(payload, order);

            timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt && !clientRoundtrip.AckReceived) {
                yield return null;
            }

            Assert.That(clientRoundtrip.AckReceived, Is.True, "Client should receive RPC acknowledgement.");
            Assert.That(clientRoundtrip.LastAckPayload, Is.EqualTo(payload), "RPC payload should roundtrip unchanged.");
            Assert.That(clientRoundtrip.LastAckOrder, Is.EqualTo(order), "RPC ordering token should be preserved.");
        }

        [UnityTest]
        public IEnumerator OwnerOnlyRpc_RejectsNonOwner_AndAllowsOwner() {
            AuthorityProbeBehaviour.ResetCounters();
            SetupReplicatedPrefab();
            yield return StartHostAndClient();

            var hostInstance = Object.Instantiate(_replicatedPrefab);
            var hostNetworkObject = hostInstance.GetComponent<NetworkObject>();
            var hostAuthorityProbe = hostInstance.GetComponent<AuthorityProbeBehaviour>();
            hostNetworkObject.Spawn();
            var spawnedId = hostNetworkObject.NetworkObjectId;

            AuthorityProbeBehaviour clientProbe = null;
            var timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                if(_clientManager.SpawnManager.SpawnedObjects.TryGetValue(spawnedId, out var clientObject)) {
                    clientProbe = clientObject.GetComponent<AuthorityProbeBehaviour>();
                    if(clientProbe != null) {
                        break;
                    }
                }
                yield return null;
            }

            Assert.That(clientProbe, Is.Not.Null, "Client should resolve AuthorityProbeBehaviour.");
            Assert.That(clientProbe.IsOwner, Is.False, "Client should not own host-spawned probe object.");

            Assert.Throws<RpcException>(() => clientProbe.OwnerOnlyRpc(),
                "NGO should reject owner-only RPC when invoked by non-owner.");
            yield return null;

            Assert.That(AuthorityProbeBehaviour.OwnerOnlyRpcCalls, Is.EqualTo(0),
                "Non-owner should not be able to invoke owner-only server RPC.");

            hostAuthorityProbe.OwnerOnlyRpc(); // Host owns this object, should succeed.
            yield return null;
            Assert.That(AuthorityProbeBehaviour.OwnerOnlyRpcCalls, Is.EqualTo(1));

            clientProbe.EveryoneRpc(); // Non-owner allowed for everyone-permission RPC.
            yield return null;
            Assert.That(AuthorityProbeBehaviour.EveryoneRpcCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator Client_Disconnects_And_Reconnects_Cleanly() {
            var port = (ushort)Random.Range(28001, 32000);
            _hostManager = CreateManager("PlayModeHost", port);
            _clientManager = CreateManager("PlayModeClient", port);

            var remoteConnectedClientIds = new HashSet<ulong>();
            var remoteDisconnectedClientIds = new HashSet<ulong>();
            void OnConnected(ulong clientId) {
                if(clientId != _hostManager.LocalClientId) {
                    remoteConnectedClientIds.Add(clientId);
                }
            }
            void OnDisconnected(ulong clientId) {
                if(clientId != _hostManager.LocalClientId) {
                    remoteDisconnectedClientIds.Add(clientId);
                }
            }
            _hostManager.OnClientConnectedCallback += OnConnected;
            _hostManager.OnClientDisconnectCallback += OnDisconnected;

            Assert.That(_hostManager.StartHost(), Is.True);
            Assert.That(_clientManager.StartClient(), Is.True);

            var timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt &&
                  (_hostManager.ConnectedClients.Count < 2 || !_clientManager.IsConnectedClient)) {
                yield return null;
            }
            Assert.That(_hostManager.ConnectedClients.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(_clientManager.IsConnectedClient, Is.True, "Client should report connected after initial join.");
            Assert.That(remoteConnectedClientIds.Count, Is.GreaterThanOrEqualTo(1),
                "Host should observe remote client connect callback.");

            _clientManager.Shutdown();
            yield return null;

            timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt &&
                  (_hostManager.ConnectedClients.Count > 1 || _clientManager.IsConnectedClient)) {
                yield return null;
            }
            Assert.That(_hostManager.ConnectedClients.Count, Is.EqualTo(1), "Host should only have itself after client disconnect.");
            Assert.That(_clientManager.IsConnectedClient, Is.False, "Client should report disconnected after shutdown.");
            Assert.That(remoteDisconnectedClientIds.Count, Is.GreaterThanOrEqualTo(1),
                "Host should observe remote client disconnect callback.");

            if(_clientObject != null) {
                Object.DestroyImmediate(_clientObject);
                _clientObject = null;
            }
            _clientManager = CreateManager("PlayModeClientReconnect", port);
            Assert.That(_clientManager.StartClient(), Is.True);

            timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt &&
                  (_hostManager.ConnectedClients.Count < 2 || !_clientManager.IsConnectedClient)) {
                yield return null;
            }

            _hostManager.OnClientConnectedCallback -= OnConnected;
            _hostManager.OnClientDisconnectCallback -= OnDisconnected;

            Assert.That(_hostManager.ConnectedClients.Count, Is.GreaterThanOrEqualTo(2));
            Assert.That(_clientManager.IsConnectedClient, Is.True, "Reconnected client should report connected.");
            Assert.That(remoteConnectedClientIds.Count, Is.GreaterThanOrEqualTo(2),
                "Expected at least two distinct remote connect callbacks across connect/reconnect cycle.");
            Assert.That(remoteDisconnectedClientIds.Count, Is.GreaterThanOrEqualTo(1),
                "Expected at least one remote disconnect callback during reconnect cycle.");
        }

        private void SetupReplicatedPrefab() {
            if(_replicatedPrefab != null) return;

            _replicatedPrefab = new GameObject("ReplicatedPrefab");
            var networkObject = _replicatedPrefab.AddComponent<NetworkObject>();
            _replicatedPrefab.AddComponent<ReplicatedIntBehaviour>();
            _replicatedPrefab.AddComponent<RpcRoundtripBehaviour>();
            _replicatedPrefab.AddComponent<AuthorityProbeBehaviour>();

            // Runtime-created prefabs do not get editor-generated global object hashes.
            // Assign a deterministic non-zero hash so NGO can resolve spawn messages in tests.
            var hashField = typeof(NetworkObject).GetField("GlobalObjectIdHash",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(hashField, Is.Not.Null);
            hashField.SetValue(networkObject, 0x51A7E123u);
        }

        private IEnumerator StartHostAndClient() {
            var port = (ushort)Random.Range(22001, 28000);
            _hostManager = CreateManager("PlayModeHost", port);
            _clientManager = CreateManager("PlayModeClient", port);

            if(_replicatedPrefab != null) {
                RegisterPrefab(_hostManager, _replicatedPrefab);
                RegisterPrefab(_clientManager, _replicatedPrefab);
            }

            Assert.That(_hostManager.StartHost(), Is.True, "Host failed to start.");
            Assert.That(_clientManager.StartClient(), Is.True, "Client failed to start.");

            var timeoutAt = Time.realtimeSinceStartup + 10f;
            while(Time.realtimeSinceStartup < timeoutAt) {
                if(_hostManager.IsListening && _clientManager.IsConnectedClient) {
                    yield break;
                }
                yield return null;
            }

            Assert.Fail("Host/client startup timed out.");
        }

        private static void RegisterPrefab(NetworkManager manager, GameObject prefab) {
            var added = manager.NetworkConfig.Prefabs.Add(new NetworkPrefab { Prefab = prefab });
            Assert.That(added || manager.NetworkConfig.Prefabs.Contains(prefab), Is.True,
                "Prefab should be registered for network spawning.");
        }

        private NetworkManager CreateManager(string name, ushort port) {
            var go = new GameObject(name);
            var manager = go.AddComponent<NetworkManager>();
            var transport = go.AddComponent<UnityTransport>();

            if(manager.NetworkConfig == null) {
                manager.NetworkConfig = new NetworkConfig();
            }

            // Keep these tests focused on transport/spawn/replication only.
            // Scene synchronization can introduce unrelated in-scene prefab hash requirements.
            manager.NetworkConfig.EnableSceneManagement = false;
            manager.NetworkConfig.PlayerPrefab = null;
            manager.NetworkConfig.NetworkTransport = transport;
            transport.SetConnectionData("127.0.0.1", port, "127.0.0.1");

            if(name.Contains("Host")) {
                _hostObject = go;
            } else {
                _clientObject = go;
            }

            return manager;
        }
    }
}
