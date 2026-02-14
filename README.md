# HOP

HOP is a multiplayer movement shooter developed in Unity 6.3. The project implements high-speed traversal mechanics and team-based objectives within a networked environment.

## Technical Details
The game utilizes the Unity Netcode for GameObjects framework for synchronization and state management. Networking is handled via Unity Gaming Services, providing support for P2P lobbies and relay connectivity.

### Traversal Systems
Movement is handled through a modular controller architecture. Key components include:
* **Wall Running:** Surface detection and velocity redirection for horizontal wall movement.
* **Grappling Physics:** Mechanics for pulling your character to a point.
* **Ledge Mantling:** Automated detection and climbing of vertical ledge obstacles.
* **Momentum Management:** Handling of air strafing, bunny hopping, and speed preservation.

### Core Mechanics
* **Hopball:** A team-based objective mode focused on ball possession.
* **Deathmatch / Team Deathmatch:** Standard individual and team-based elimination modes.
* **King of the Hill:** A territory control mode where the capture point wanders throughout the match.
* **Tag:** A tag gamemode where the "It" player has to shoot another to tag them.
* **Combat:** A weapon management system supporting various fire modes and projectile types.
* **Social Systems:** Integration with Steam for player names, lobbies, and chat functionality.

### Architecture and Dependencies
The project follows a component-based design where player capabilities are isolated into individual controllers. This separation allows for easier debugging and extension of movement or combat logic.

**Key Dependencies:**
* Unity 6.3 (6000.3.6f1)
* Netcode for GameObjects
* Unity Gaming Services (Lobby, Relay, Vivox)
* Steamworks.NET
* UniTask for asynchronous operations

## Repository Structure
* **Assets/Scripts/Game:** Player controllers, health systems, and core gameplay modes.
* **Assets/Scripts/Network:** Session management, Steam integration, and network synchronization logic.
* **Assets/Scripts/UI:** HUD, menus, and communication tools.
