using System.Collections.Generic;
using System.Linq;
using Game.Player;
using UnityEngine;

namespace Network.Singletons {
    /// <summary>
    /// Singleton manager that loads and provides access to player material packets from Resources.
    /// </summary>
    public class PlayerMaterialPacketManager : MonoBehaviour {
        public static PlayerMaterialPacketManager Instance { get; private set; }

        private const string ResourcesPath = "PlayerMaterialPackets";
        private List<PlayerMaterialPacket> _packets;
        private PlayerMaterialPacket _nonePacket;

        private void Awake() {
            if(Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);

            LoadAllPackets();
        }

        /// <summary>
        /// Loads all material packets from Resources and creates a "None" packet.
        /// </summary>
        private void LoadAllPackets() {
            var loadedPackets = Resources.LoadAll<PlayerMaterialPacket>(ResourcesPath).ToList();

            // Create "None" packet (index 0)
            _nonePacket = ScriptableObject.CreateInstance<PlayerMaterialPacket>();
            _nonePacket.packetName = "None";
            _nonePacket.useMetallicWorkflow = true;
            _nonePacket.defaultSmoothness = 0f;
            _nonePacket.defaultMetallic = 0f;
            _nonePacket.tiling = Vector2.one;
            _nonePacket.offset = Vector2.zero;
            _nonePacket.defaultEmissionEnabled = false;
            _nonePacket.defaultEmissionColor = new Color(0f, 0f, 0f, 1f);

            // Build packet list: None at index 0, then all loaded packets
            _packets = new List<PlayerMaterialPacket> { _nonePacket };
            _packets.AddRange(loadedPackets);

            Debug.Log($"[PlayerMaterialPacketManager] Loaded {_packets.Count - 1} material packets from Resources (plus 'None' packet). Total: {_packets.Count}");
        }

        /// <summary>
        /// Gets a material packet by index. Index 0 is always "None", 1+ are loaded packets.
        /// </summary>
        public PlayerMaterialPacket GetPacket(int index) {
            if(_packets == null || _packets.Count == 0) {
                Debug.LogWarning("[PlayerMaterialPacketManager] Packets not loaded yet. Returning None packet.");
                return _nonePacket ?? CreateNonePacket();
            }

            if(index >= 0 && index < _packets.Count) return _packets[index];
            Debug.LogWarning($"[PlayerMaterialPacketManager] Invalid packet index {index}. Returning None packet.");
            return _nonePacket ?? CreateNonePacket();
        }

        /// <summary>
        /// Returns the total number of available packets (including "None").
        /// </summary>
        public int GetPacketCount() {
            return _packets?.Count ?? 1;
        }

        /// <summary>
        /// Returns all available packets (for UI dropdowns, etc.).
        /// </summary>
        public List<PlayerMaterialPacket> GetAllPackets() {
            if(_packets == null || _packets.Count == 0) {
                return new List<PlayerMaterialPacket> { CreateNonePacket() };
            }

            return new List<PlayerMaterialPacket>(_packets);
        }

        /// <summary>
        /// Gets the "None" packet (index 0).
        /// </summary>
        public PlayerMaterialPacket GetNonePacket() {
            return _nonePacket ?? CreateNonePacket();
        }

        private static PlayerMaterialPacket CreateNonePacket() {
            var packet = ScriptableObject.CreateInstance<PlayerMaterialPacket>();
            packet.packetName = "None";
            packet.useMetallicWorkflow = true;
            packet.defaultSmoothness = 0f;
            packet.defaultMetallic = 0f;
            packet.tiling = Vector2.one;
            packet.offset = Vector2.zero;
            packet.defaultEmissionEnabled = false;
            packet.defaultEmissionColor = new Color(0f, 0f, 0f, 1f);
            return packet;
        }
    }
}

