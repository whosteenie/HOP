using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Lobby = Steamworks.Data.Lobby;

namespace Network.SessionContracts {
    /// <summary>
    /// Read/write and orchestration surface for session state. Implemented by SessionManager;
    /// services receive this to avoid holding a direct reference to the manager.
    /// </summary>
    public interface ISessionContext {
        // --- State (read) ---
        SessionPhase Phase { get; }
        float PhaseStartTime { get; }
        Lobby? CurrentLobby { get; }
        string CurrentPartyId { get; }
        bool IsPartyLeader { get; }
        string SelectedGameMode { get; }
        string SelectedMapId { get; }
        string SelectedMapSceneName { get; }
        Unity.Services.Lobbies.Models.Lobby UgsPartyLobby { get; }
        Unity.Services.Lobbies.Models.Lobby UgsMatchLobby { get; }
        bool IsInGameplay { get; }
        bool IsLeaving { get; }
        bool IsShuttingDown { get; }
        bool IsExpectedDisconnect { get; }
        bool IsSearching { get; }
        bool IsSessionBusy { get; }
        int ExpectedGamePlayerCount { get; }
        CancellationToken SessionLifetimeToken { get; }
        float MatchmakingStartTime { get; }

        // --- State (write) ---
        void SetPhase(SessionPhase value);
        void SetCurrentLobby(Lobby? value);
        void SetCurrentPartyId(string value);
        void SetIsPartyLeader(bool value);
        void SetUgsPartyLobby(Unity.Services.Lobbies.Models.Lobby value);
        void SetUgsMatchLobby(Unity.Services.Lobbies.Models.Lobby value);
        void SetIsInGameplay(bool value);
        void SetIsExpectedDisconnect(bool value);
        void SetPrivateMatchMapPreset(bool value);
        void SetMatchmakingStartTime(float value);
        void SetNextUgsHeartbeatTime(float value);

        // --- Infrastructure ---
        void LaunchSessionTask(UniTask task, string label);
        bool TryGetNetworkManager(string operationName, out NetworkManager networkManager);
        bool TryGetUnityTransport(string operationName, out NetworkManager networkManager, out UnityTransport transport);
        bool TryBeginSessionOperation(string name);
        void EndSessionOperation();

        // --- Orchestration ---
        void SetFrontStatus(SessionPhase phase, string message);
        void SetExpectedGamePlayerCount(int count, string source);
        void ApplyRuntimeMode(string mode, string source, bool refreshUi = true);
        void LeaveLobby();
        UniTask LeaveToMainMenuAsync(bool skipFadeOut = false);
        UniTask EnsureSignedInAsync();
        void NotifyPartyStateChanged();
        void UpdateSteamRichPresence();
        void UpdateLocalDisplayNameInLobby();
    }

    public enum SessionPhase {
        Menu,
        Searching,
        CreatingLobby,
        JoiningLobby,
        LobbyReady,
        StartingHost,
        StartingClient,
        SynchronizingLoad,
        LoadingScene,
        InGame,
        Error
    }
}
