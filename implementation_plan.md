# Steamworks Integration Implementation Plan

**Goal**: Replace Unity Game Services (Relay) with Steamworks P2P Lobbies and Networking. Implement "Quick Play" matchmaking and Steam Friends integration.

## 1. Dependencies & Setup

### Install Facepunch.Steamworks
We need the C# wrapper for the Steamworks API.
*   **Action**: Add `com.facepunch.steamworks` (or verified fork) to `Packages/manifest.json`.
*   **Verification**: Ensure `SteamClient.Init(480)` succeeds in a test script.

### Install Netcode Transport
We need a `NetworkTransport` that communicates via Steam P2P.
*   **Action**: Install `Netcode.Transports.Facepunch` (Community standard) or implement a wrapper around `SteamNetworkingSockets`.
*   **Action**: Update `CustomNetworkManager` prefab to use the new Transport component instead of `UnityTransport`.

## 2. Core Architecture (Session Management)

### Refactor `SessionManager.cs`
We will likely create a new `SteamSessionManager` (or refactor the existing one) to handle the Steam lifecycle.

*   **Initialize**: `SteamClient.Init(480)`.
*   **Update Loop**: Call `SteamClient.RunCallbacks()`.
*   **Hosting**:
    *   `SteamMatchmaking.CreateLobbyAsync(MaxPlayers)`.
    *   On Success: `Lobby.SetData("HostAddress", SteamClient.SteamId.ToString())`.
    *   Start `NetworkManager.Singleton.StartHost()`.
*   **Joining**:
    *   `SteamMatchmaking.JoinLobbyAsync(LobbyId)`.
    *   On Success: Read "HostAddress".
    *   `SteamTransport.targetSteamId = HostAddress`.
    *   Start `NetworkManager.Singleton.StartClient()`.

### "Queue" Logic (Matchmaking)
Logic for the **Find Game** button:
1.  **Search**: `SteamMatchmaking.LobbyList.WithKeyValue("gamemode", "any").RequestAsync()`.
2.  **Filter**: Find lobbies with `MemberCount < MaxPlayers`.
3.  **Action**:
    *   If Lobby Found -> Join it.
    *   If None Found -> Create a Public Lobby (`LobbyType.Public`) and wait.

## 3. UI Implementation

### Main Menu Updates
*   **Play Button**: Rename to "Private Lobby" (Created as `LobbyType.FriendsOnly`).
*   **Find Game Button**: New button. Triggers the "Queue" logic above.
*   **Match Status**: Text indicator ("Searching...", "Creating Lobby...", "Starting...").

### Party UI (Top Right)
*   **Visuals**: Horizontal list of player icons.
*   **Data**: Poll `Lobby.Members` to display avatars.
*   **Invite Button**: Calls `SteamFriends.OpenGameInviteOverlay(Lobby.Id)`.
*   **Behavior**: When a member joins, their avatar appears.

## 4. Game Logic Adjustments

### Gamemode Selection
*   **Shelved**: Advanced voting/selection.
*   **Implementation**: Host picks a random supported gamemode (e.g., KOTH) and random map (DevMap) when starting the session.

### Player Limits
*   Enforce limits based on Gamemode (e.g., 6 for KOTH) in `SteamSessionManager`.

## 5. Migration Strategy
1.  **Backup**: Ensure current `SessionManager` is backed up (or git committed).
2.  **Parallel Dev**: Create `SteamSessionManager` alongside `SessionManager` to test without breaking the project immediately?
    *   *Decision*: Better to replace `SessionManager` internals since it's a Singleton used everywhere. We will use `#if UNITY_EDITOR` toggles or just full replacement if committed.

## 6. Gamemode Configuration & Party Logic
*   **Gamemode Definitions**:
    *   Create `GamemodeDef` struct/class in `MatchSettingsManager` (MinPlayers, MaxPlayers, MaxPartySize).
    *   Update `MatchSettingsManager` to store definitions for all modes (Deathmatch, TDM, Hopball, KOTH, Gun Tag).
*   **UI Constraints**:
    *   Update `MainMenuSessionManager` to enforce:
        - Disable "Play" if PartySize > Gamemode.MaxPartySize (Public only).
        - Hide/Disable "Invite" if PartySize >= 10.
        - Check "Private" overrides (ignore min players).
*   **Matchmaking**:
    *   Update `SessionManager.FindGameAsync` to filter lobbies by available slots >= PartySize.

## Risks
*   **Steam DLLs**: Sometimes Unity has trouble loading the native `.dll` if not placed correctly.
*   **Firewall**: P2P usually punches through, but testing required.
