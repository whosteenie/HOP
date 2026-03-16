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
    /// Event published synchronously when a weapon switch is being requested so other held-item systems can react.
    /// Subscribers may mutate the flags to communicate whether they consumed a carry-item transition.
    /// </summary>
    public class WeaponSwitchRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;
        public readonly int RequestedWeaponIndex;
        public bool WasHoldingHopball;
        public bool WasRestoringAfterDissolve;

        public WeaponSwitchRequestedEvent(ulong playerNetworkObjectId, int requestedWeaponIndex) {
            PlayerNetworkObjectId = playerNetworkObjectId;
            RequestedWeaponIndex = requestedWeaponIndex;
        }
    }

    /// <summary>
    /// Event published when player combat/lifecycle systems need the hopball holder to drop on death-like transitions.
    /// </summary>
    public class PlayerHopballDeathDropRequestedEvent : GameEvent {
        public readonly ulong PlayerOwnerClientId;
        public readonly string Reason;

        public PlayerHopballDeathDropRequestedEvent(ulong playerOwnerClientId, string reason) {
            PlayerOwnerClientId = playerOwnerClientId;
            Reason = reason;
        }
    }

    /// <summary>
    /// Event published when player systems want hopball pickup logic to run for a specific player.
    /// </summary>
    public class PlayerHopballPickupRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;

        public PlayerHopballPickupRequestedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
        }
    }

    /// <summary>
    /// Event published when player systems want the local holder to manually drop hopball.
    /// </summary>
    public class PlayerHopballManualDropRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;

        public PlayerHopballManualDropRequestedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
        }
    }

    /// <summary>
    /// Synchronous event used to evaluate whether a player's hopball pickup prompt should be shown this frame.
    /// </summary>
    public class PlayerHopballPickupPromptEvaluationRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;
        public bool CanPickupNearbyHopball;

        public PlayerHopballPickupPromptEvaluationRequestedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
        }
    }

    /// <summary>
    /// Event published when player disconnect transitions need first-person hopball visuals hidden.
    /// </summary>
    public class PlayerDisconnectFpVisualHideRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;

        public PlayerDisconnectFpVisualHideRequestedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
        }
    }

    /// <summary>
    /// Event published when local hopball hold state changes immediately on a client.
    /// </summary>
    public class HopballHoldStateChangedEvent : GameEvent {
        public readonly ulong PlayerOwnerClientId;
        public readonly bool IsHoldingHopball;

        public HopballHoldStateChangedEvent(ulong playerOwnerClientId, bool isHoldingHopball) {
            PlayerOwnerClientId = playerOwnerClientId;
            IsHoldingHopball = isHoldingHopball;
        }
    }

    /// <summary>
    /// Event published when weapon systems need player-side world-weapon visuals to refresh after a switch/presentation change.
    /// </summary>
    public class PlayerWorldWeaponPresentationRefreshRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;
        public readonly bool UsePodiumShadowState;

        public PlayerWorldWeaponPresentationRefreshRequestedEvent(ulong playerNetworkObjectId, bool usePodiumShadowState) {
            PlayerNetworkObjectId = playerNetworkObjectId;
            UsePodiumShadowState = usePodiumShadowState;
        }
    }

    /// <summary>
    /// Event published when weapon systems need player-side holster shadows refreshed.
    /// </summary>
    public class PlayerHolsterShadowRefreshRequestedEvent : GameEvent {
        public readonly ulong PlayerNetworkObjectId;

        public PlayerHolsterShadowRefreshRequestedEvent(ulong playerNetworkObjectId) {
            PlayerNetworkObjectId = playerNetworkObjectId;
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
    /// Event published on clients when hopball visuals should be prewarmed for player controllers.
    /// </summary>
    public class HopballVisualPrewarmRequestedEvent : GameEvent {
    }

    /// <summary>
    /// Event published on clients when hopball equip presentation should be applied.
    /// </summary>
    public class HopballEquippedPresentationEvent : GameEvent {
        public readonly Unity.Netcode.NetworkObjectReference HopballRef;
        public readonly ulong HolderClientId;

        public HopballEquippedPresentationEvent(Unity.Netcode.NetworkObjectReference hopballRef, ulong holderClientId) {
            HopballRef = hopballRef;
            HolderClientId = holderClientId;
        }
    }

    /// <summary>
    /// Event published on clients when hopball drop presentation should be applied.
    /// </summary>
    public class HopballDropPresentationEvent : GameEvent {
        public readonly ulong HolderClientId;

        public HopballDropPresentationEvent(ulong holderClientId) {
            HolderClientId = holderClientId;
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

