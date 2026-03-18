using System.IO;
using Diagnostics;
using Game.Progression;
using UnityEditor;
using UnityEngine;

namespace Editor.Tools {
    public static class ChallengeAssetGenerator {
    [MenuItem("HOP/Progression/Generate Challenge Assets")]
    public static void GenerateAssets() {
        const string path = "Assets/Resources/Challenges";
        if (!Directory.Exists(path)) {
            Directory.CreateDirectory(path);
        }

        // --- Daily Pool ---
        CreateChallenge("kill_generic", ChallengeType.Kill, "Get {0} Kills", 10, 50, 500);
        CreateChallenge("kill_speed", ChallengeType.SpeedKill, "Get {0} Kills while moving > 15m/s", 3, 5, 750);
        CreateChallenge("kill_aerial", ChallengeType.AerialKill, "Get {0} Kills while Airborne", 5, 10, 750);
        CreateChallenge("wallrun_chain", ChallengeType.WallRunChain, "Chain {0} Wall Runs without touching ground", 10, 20, 1000);
        CreateChallenge("hop_dissolve", ChallengeType.HopballDissolve, "Hold Hopball until Dissolve {0} times", 1, 3, 1000);
        CreateChallenge("tag_count", ChallengeType.TagCount, "Tag {0} Players", 5, 10, 500);
        CreateChallenge("win_match", ChallengeType.Win, "Win {0} Matches", 1, 3, 1000);
        CreateChallenge("kill_rampage", ChallengeType.KillStreak, "Get a Killstreak of {0}", 3, 5, 1000);

        // Gamemode Participation (dynamically assigned at generation time)
        // Uses special handling in ProgressionManager to pick a random gamemode
        CreateChallenge("play_matches_of", ChallengeType.MatchesPlayed, "Play {0} Matches of {1}", 2, 5, 500);

        // Placement Challenges
        CreateFilteredChallenge("place_top3", ChallengeType.Placement, "top3", "Place in the Top 3 {0} times", 1, 3, 800);
        CreateFilteredChallenge("place_top5", ChallengeType.Placement, "top5", "Place in the Top 5 {0} times", 2, 5, 600);

        // Weapon Challenges (dynamically assigned at generation time)
        // Uses special handling in ProgressionManager to pick a random weapon
        CreateChallenge("weapon_kills", ChallengeType.WeaponKill, "Get {0} Kills with {1}", 5, 15, 600);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        DevLog.Log("Challenge Assets Generated in Resources/Challenges!");
    }

    private static void CreateChallenge(string id, ChallengeType type, string desc, int min, int max, int xp) {
        CreateFilteredChallenge(id, type, "", desc, min, max, xp);
    }

    private static void CreateFilteredChallenge(string id, ChallengeType type, string filter, string desc, int min, int max, int xp) {
        var asset = ScriptableObject.CreateInstance<ChallengeDefinition>();
        asset.id = id;
        asset.type = type;
        asset.weaponID = filter; // Using WeaponID as generic Filter ID
        asset.descriptionTemplate = desc;
        asset.minTarget = min;
        asset.maxTarget = max;
        asset.weeklyMinTarget = min;
        asset.weeklyMaxTarget = max;
        asset.baseXpReward = xp;
        asset.includeInDaily = true;
        asset.includeInWeekly = true;

        var assetPath = $"Assets/Resources/Challenges/{id}.asset";
        AssetDatabase.CreateAsset(asset, assetPath);
    }

    private static void CreateWeaponChallenge(string id, string weaponName, string desc, int min, int max, int xp) {
        CreateFilteredChallenge(id, ChallengeType.WeaponKill, weaponName, desc, min, max, xp);
    }
    }
}
