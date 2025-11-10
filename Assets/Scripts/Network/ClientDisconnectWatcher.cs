using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace Network {
    public class ClientDisconnectWatcher : MonoBehaviour
    {
        private void OnEnable()
        {
            var nm = NetworkManager.Singleton;
            if (!nm) return;

            nm.OnClientStopped += OnClientStopped;

            if (nm.NetworkConfig.NetworkTransport is UnityTransport utp)
                utp.OnTransportEvent += OnTransportEvent;
        }

        private void OnDisable()
        {
            var nm = NetworkManager.Singleton;
            if (!nm) return;
            nm.OnClientStopped -= OnClientStopped;

            if (nm.NetworkConfig.NetworkTransport is UnityTransport utp)
                utp.OnTransportEvent -= OnTransportEvent;
        }

        private void OnClientStopped(bool _)
        {
            LoadMenuIfNeeded();
        }

        private void OnTransportEvent(NetworkEvent evt, ulong _, System.ArraySegment<byte> __, float ___)
        {
            if (evt == NetworkEvent.Disconnect)
                LoadMenuIfNeeded();
        }

        private void LoadMenuIfNeeded()
        {
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainMenu")
                UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }
    }
}
