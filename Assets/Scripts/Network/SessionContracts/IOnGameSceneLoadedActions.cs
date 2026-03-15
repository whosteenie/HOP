using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Lobbies.Models;

namespace Network.SessionContracts {
    /// <summary>
    /// Actions used by SessionSceneFlowService when the gameplay scene has loaded (mode sync, voice, lobby state, fade-in).
    /// Implemented by SessionManager.
    /// </summary>
    public interface IOnGameSceneLoadedActions {
        bool TryGetRuntimeMode(out string mode, out string source);
        void TryJoinVoiceForActiveMatch(string context);
        UniTask TrySetMatchLobbyStateAsync(string lobbyState, DataObject.VisibilityOptions visibility, string context);
        UniTask RefreshBackfillEligibilityAsync(bool force);
        UniTask UnsubscribeMatchLobbyAsync(string context);
        bool TryGetNetworkManager(out NetworkManager networkManager);
        void EnableGameplaySpawningIfHost();
        int StartGameScenePresentation();
        bool IsCurrentGameScenePresentation(int serial);
        bool IsMatchLobbyPublic();
    }
}
