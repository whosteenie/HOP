using System.Collections.Generic;
using UnityEngine;

namespace Game.Match {
    [CreateAssetMenu(
        fileName = "MapDefinition",
        menuName = "HOP/Match/Map Definition",
        order = 0
    )]
    public class MapDefinition : ScriptableObject {
        [Header("Identity")]
        [SerializeField] private string mapId = "default_map";
        [SerializeField] private string sceneName = "Game";
        [SerializeField] private bool enabledInRotation = true;
        [SerializeField, Min(1)] private int selectionWeight = 1;
        [SerializeField] private Sprite previewImage;

        [Header("Supported Gamemodes")]
        [SerializeField] private List<string> supportedGamemodes = new();

        public string MapId => mapId;
        public string SceneName => sceneName;
        public bool EnabledInRotation => enabledInRotation;
        public int SelectionWeight => Mathf.Max(1, selectionWeight);
        public Sprite PreviewImage => previewImage;
        public IReadOnlyList<string> SupportedGamemodes => supportedGamemodes;

        public bool SupportsGamemode(string gamemodeId) {
            if(string.IsNullOrWhiteSpace(gamemodeId)) {
                return false;
            }

            if(supportedGamemodes == null || supportedGamemodes.Count == 0) {
                return true;
            }

            for(var i = 0; i < supportedGamemodes.Count; i++) {
                if(string.Equals(supportedGamemodes[i], gamemodeId, System.StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }

            return false;
        }
    }
}
