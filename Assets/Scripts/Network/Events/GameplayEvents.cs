using UnityEngine;

namespace Network.Events {
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

        public GrappleStartedEvent(Vector3 targetPosition) {
            TargetPosition = targetPosition;
        }
    }

    /// <summary>
    /// Event published when a player ends grappling.
    /// </summary>
    public class GrappleEndedEvent : GameEvent {
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
}

