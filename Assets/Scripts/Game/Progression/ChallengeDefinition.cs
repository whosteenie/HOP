using UnityEngine;

namespace Game.Progression {
    public enum ChallengeType {
        Kill,
        Win,
        Damage,
        PlayTime,
        HypeRank,
        SpeedKill,
        AerialKill,
        WallRunChain,
        TagCount,
        HopballDissolve,
        MatchAvgSpeed,
        WeaponKill,
        KillStreak,
        MatchesPlayed,
        Placement
    }

    [CreateAssetMenu(fileName = "NewChallenge", menuName = "HOP/Progression/Challenge Definition")]
    public class ChallengeDefinition : ScriptableObject {
        public string ID; // Unique ID (e.g., "kill_50_enemies")
        public ChallengeType Type;
        public string DescriptionTemplate; // "Get {0} kills"
        public string Description => DescriptionTemplate;
        public int MinTarget = 10;
        public int MaxTarget = 50;
        public int BaseXPReward = 500;
        
        public bool IsWeekly; // If true, this challenge appears in weekly pool
        
        // Optional filters
        [Tooltip("If Type is WeaponKill, this must match the Weapon Name exactly.")]
        public string WeaponID; 
    }
}
