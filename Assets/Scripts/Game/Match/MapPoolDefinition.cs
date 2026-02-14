using System.Collections.Generic;
using UnityEngine;

namespace Game.Match {
    [CreateAssetMenu(
        fileName = "MapPoolDefinition",
        menuName = "HOP/Match/Map Pool Definition",
        order = 1
    )]
    public class MapPoolDefinition : ScriptableObject {
        [Header("Fallbacks")]
        [SerializeField] private string fallbackGameplaySceneName = "Game";
        [SerializeField] private string fallbackMapId = "default_game";

        [Header("Rotation")]
        [SerializeField] private List<MapDefinition> maps = new();

        public string FallbackGameplaySceneName => fallbackGameplaySceneName;
        public string FallbackMapId => fallbackMapId;
        public IReadOnlyList<MapDefinition> Maps => maps;
    }
}
