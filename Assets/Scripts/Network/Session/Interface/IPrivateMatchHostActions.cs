using Cysharp.Threading.Tasks;

namespace Network.Session.Interface {
    /// <summary>
    /// Actions used by SessionPartyService when orchestrating the private match host flow.
    /// Implemented by SessionManager.
    /// </summary>
    public interface IPrivateMatchHostActions {
        UniTask PreFadePrivateHostAsync();
        UniTask<string> CreateDaSessionAsync(int maxPlayers, bool isPrivateMatch, string contextLabel);
        UniTask<bool> TrySetMatchLobbyStateAsync(string lobbyState, Unity.Services.Lobbies.Models.DataObject.VisibilityOptions visibility, string context);
        bool TryLoadGameplaySceneAsHost(string contextLabel);
        UniTask LeaveToMainMenuAsync(bool skipFadeOut = false);
    }
}
