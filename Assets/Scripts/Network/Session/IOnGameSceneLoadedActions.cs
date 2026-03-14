using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Lobbies.Models;

namespace Network.Session {
    /// <summary>
    /// Actions used by SessionSceneFlowService when the gameplay scene has loaded (mode sync, voice, lobby state, fade-in).
    /// Implemented by SessionManager.
    /// </summary>
    public interface IOnGameSceneLoadedActions {
        bool TryGetAuthoritativeRuntimeMode(out string mode, out string source);
        void TryJoinVoiceForActiveMatch(string context);
        UniTask TrySetMatchLobbyStateAsync(string lobbyState, DataObject.VisibilityOptions visibility, string context);
        UniTask RefreshPublicMatchBackfillEligibilityAsync(bool force);
        UniTask UnsubscribeMatchLobbyEventsAsync(string context);
        bool TryGetNetworkManager(out NetworkManager networkManager);
        void EnableGameplaySpawningAndSpawnAllIfHost();
        int StartGameScenePresentation();
        bool IsCurrentGameScenePresentation(int serial);
        bool IsMatchLobbyPublic();
    }
}
