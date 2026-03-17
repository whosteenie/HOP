using UnityEngine;

namespace Game.Player.Contracts {
    public static class PlayerContractResolver {
        public static bool TryResolve<T>(Component owner, ref MonoBehaviour source, out T contract)
            where T : class {
            // ReSharper disable once UsePatternMatching
            var cached = source as T;
            if(source != null && cached != null) {
                contract = cached;
                return true;
            }

            var behaviours = owner.GetComponents<MonoBehaviour>();
            foreach(var b in behaviours) {
                if(b == null) continue;
                // ReSharper disable once UseNegatedPatternMatching
                var found = b as T;
                if(found == null) continue;
                source = b;
                contract = found;
                return true;
            }

            contract = null;
            return false;
        }
    }
}
