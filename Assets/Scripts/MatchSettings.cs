using UnityEngine;

public class MatchSettings : MonoBehaviour {
    public static MatchSettings Instance { get; private set; }

    [Header("Defaults")]
    [Tooltip("Fallback duration if nothing else is set (seconds).")]
    public int defaultMatchDurationSeconds = 600; // 10 minutes

    [Header("Runtime")]
    [Tooltip("Set by main menu / gamemode selection before loading the Game scene.")]
    public int matchDurationSeconds;

    [Tooltip("Optional identifier for current gamemode, e.g. 'Deathmatch'.")]
    public string selectedGameModeId;

    private void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (matchDurationSeconds <= 0) {
            matchDurationSeconds = defaultMatchDurationSeconds;
        }
        
        // Initialize gamemode if not set
        if (string.IsNullOrEmpty(selectedGameModeId)) {
            selectedGameModeId = "Deathmatch";
        }
    }

    public int GetMatchDurationSeconds() {
        return matchDurationSeconds > 0 ? matchDurationSeconds : defaultMatchDurationSeconds;
    }
}