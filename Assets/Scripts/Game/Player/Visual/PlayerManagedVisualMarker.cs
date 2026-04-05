using System.Collections.Generic;
using UnityEngine;

namespace Game.Player.Visual {
    /// <summary>
    /// Marks visuals that are managed by a specialized player-adjacent subsystem and should be skipped by generic player renderer/shadow passes.
    /// </summary>
    public static class PlayerManagedVisualMarker {
        private static readonly HashSet<EntityId> ManagedVisualInstanceIds = new();

        public static void Register(GameObject gameObject) {
            if(gameObject == null) return;
            ManagedVisualInstanceIds.Add(gameObject.GetEntityId());
        }

        public static void Unregister(GameObject gameObject) {
            if(gameObject == null) return;
            ManagedVisualInstanceIds.Remove(gameObject.GetEntityId());
        }

        public static bool IsManagedVisual(GameObject gameObject) {
            return gameObject != null && ManagedVisualInstanceIds.Contains(gameObject.GetEntityId());
        }
    }
}
