using UnityEngine;

namespace Events {
    /// <summary>
    /// Event published when a player dies.
    /// </summary>
    public class PlayerDiedEvent : GameEvent {
        public readonly ulong PlayerId;
        public readonly ulong KillerId;
        public readonly string BodyPart;

        public PlayerDiedEvent(ulong playerId, ulong killerId, string bodyPart = null) {
            PlayerId = playerId;
            KillerId = killerId;
            BodyPart = bodyPart;
        }
    }

    /// <summary>
    /// Event published when a player takes damage.
    /// </summary>
    public class PlayerDamagedEvent : GameEvent {
        public readonly ulong PlayerId;
        public readonly float Damage;
        public readonly Vector3 HitPoint;

        public PlayerDamagedEvent(ulong playerId, float damage, Vector3 hitPoint) {
            PlayerId = playerId;
            Damage = damage;
            HitPoint = hitPoint;
        }
    }

    /// <summary>
    /// Event published when a player respawns.
    /// </summary>
    public class PlayerRespawnedEvent : GameEvent {
        public readonly ulong PlayerId;

        public PlayerRespawnedEvent(ulong playerId) {
            PlayerId = playerId;
        }
    }

    /// <summary>
    /// Event published when a player switches weapons.
    /// </summary>
    public class WeaponSwitchedEvent : GameEvent {
        public readonly int WeaponIndex;

        public WeaponSwitchedEvent(int weaponIndex) {
            WeaponIndex = weaponIndex;
        }
    }

    /// <summary>
    /// Event published when a player starts grappling.
    /// </summary>
    public class GrappleStartedEvent : GameEvent {
        public readonly Vector3 TargetPosition;
        public readonly bool UseFirstPersonAnimation;

        public GrappleStartedEvent(Vector3 targetPosition, bool useFirstPersonAnimation = true) {
            TargetPosition = targetPosition;
            UseFirstPersonAnimation = useFirstPersonAnimation;
        }
    }

    /// <summary>
    /// Event published when a player ends grappling.
    /// </summary>
    public class GrappleEndedEvent : GameEvent {
    }

    /// <summary>
    /// Event published from grapple animation when the first frame is reached.
    /// Used to defer grapple mesh display until the hand is in the correct pose.
    /// </summary>
    public class GrappleAnimFirstFrameEvent : GameEvent {
    }

    /// <summary>
    /// Event published from grapple animation when the hand returns (HideGrapple event).
    /// Hides the grapple line immediately if still visible.
    /// </summary>
    public class GrappleAnimHideEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a player picks up the hopball.
    /// </summary>
    public class HopballPickedUpEvent : GameEvent {
        public readonly ulong PlayerId;

        public HopballPickedUpEvent(ulong playerId) {
            PlayerId = playerId;
        }
    }

    /// <summary>
    /// Event published when a player drops the hopball.
    /// </summary>
    public class HopballDroppedEvent : GameEvent {
        public readonly ulong PlayerId;

        public HopballDroppedEvent(ulong playerId) {
            PlayerId = playerId;
        }
    }

    /// <summary>
    /// Event published when hopball-held energy is depleted and should award score to the current holder.
    /// </summary>
    public class HopballEnergyDepletedEvent : GameEvent {
        public readonly ulong PlayerId;
        public readonly float EnergyDepleted;

        public HopballEnergyDepletedEvent(ulong playerId, float energyDepleted) {
            PlayerId = playerId;
            EnergyDepleted = energyDepleted;
        }
    }

    /// <summary>
    /// Event published when the active hopball should be respawned to a fresh spawn point.
    /// </summary>
    public class HopballRespawnRequestedEvent : GameEvent {
        public readonly ulong HopballNetworkObjectId;
        public readonly string Reason;

        public HopballRespawnRequestedEvent(ulong hopballNetworkObjectId, string reason) {
            HopballNetworkObjectId = hopballNetworkObjectId;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event published when a player network object is spawned and registered.
    /// Payload is the owning client id so Events remains agnostic to concrete player types.
    /// </summary>
    public class PlayerNetworkSpawnedEvent : GameEvent {
        public readonly ulong ClientId;

        public PlayerNetworkSpawnedEvent(ulong clientId) {
            ClientId = clientId;
        }
    }

    /// <summary>
    /// Event published when a player network object is despawned and unregistered.
    /// </summary>
    public class PlayerNetworkDespawnedEvent : GameEvent {
        public readonly ulong ClientId;

        public PlayerNetworkDespawnedEvent(ulong clientId) {
            ClientId = clientId;
        }
    }

    /// <summary>
    /// Event published when the local owner player is fully network-spawned and ready.
    /// </summary>
    public class LocalPlayerReadyEvent : GameEvent {
        public readonly ulong ClientId;

        public LocalPlayerReadyEvent(ulong clientId) {
            ClientId = clientId;
        }
    }

    /// <summary>
    /// Event published when the in-game HUD/menu manager instance is initialized.
    /// </summary>
    public class GameMenuReadyEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the match timer manager instance is initialized.
    /// </summary>
    public class MatchTimerReadyEvent : GameEvent {
    }
}

