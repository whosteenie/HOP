using UnityEngine;
using UnityEditor;
using Game.Progression;
using System.IO;

public class ChallengeAssetGenerator {
    [MenuItem("HOP/Progression/Generate Challenge Assets")]
    public static void GenerateAssets() {
        string path = "Assets/Resources/Challenges";
        if (!Directory.Exists(path)) {
            Directory.CreateDirectory(path);
        }

        CreateChallenge("kill_generic", ChallengeType.Kill, "Get {0} Kills", 10, 50, 500);
        CreateChallenge("kill_speed", ChallengeType.SpeedKill, "Get {0} Kills while moving > 15m/s", 3, 5, 750);
        CreateChallenge("kill_aerial", ChallengeType.AerialKill, "Get {0} Kills while Airborne", 5, 10, 750);
        CreateChallenge("wallrun_chain", ChallengeType.WallRunChain, "Chain {0} Wall Runs without touching ground", 10, 20, 1000);
        CreateChallenge("hop_dissolve", ChallengeType.HopballDissolve, "Hold Hopball until Dissolve {0} times", 1, 3, 1000);
        CreateChallenge("tag_count", ChallengeType.TagCount, "Tag {0} Players", 5, 10, 500);
        CreateChallenge("win_match", ChallengeType.Win, "Win {0} Matches", 1, 3, 1000);

        CreateChallenge("kill_rampage", ChallengeType.KillStreak, "Get a Killstreak of {0}", 3, 5, 1000);

        // Weapon Challenges
        CreateWeaponChallenge("kill_railgun", "Railgun", "Get {0} Kills with Railgun", 5, 10, 600);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Challenge Assets Generated in Resources/Challenges!");
    }

    private static void CreateChallenge(string id, ChallengeType type, string desc, int min, int max, int xp) {
        ChallengeDefinition asset = ScriptableObject.CreateInstance<ChallengeDefinition>();
        asset.ID = id;
        asset.Type = type;
        asset.DescriptionTemplate = desc; // Public field is DescriptionTemplate based on my view_file earlier
        asset.MinTarget = min;
        asset.MaxTarget = max;
        asset.BaseXPReward = xp;

        string assetPath = $"Assets/Resources/Challenges/{id}.asset";
        AssetDatabase.CreateAsset(asset, assetPath);
    }

    private static void CreateWeaponChallenge(string id, string weaponName, string desc, int min, int max, int xp) {
        ChallengeDefinition asset = ScriptableObject.CreateInstance<ChallengeDefinition>();
        asset.ID = id;
        asset.Type = ChallengeType.WeaponKill;
        asset.WeaponID = weaponName;
        asset.DescriptionTemplate = desc;
        asset.MinTarget = min;
        asset.MaxTarget = max;
        asset.BaseXPReward = xp;

        string assetPath = $"Assets/Resources/Challenges/{id}.asset";
        AssetDatabase.CreateAsset(asset, assetPath);
    }
}
