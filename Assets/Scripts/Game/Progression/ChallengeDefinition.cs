using UnityEngine;
using UnityEngine.Serialization;

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
        [FormerlySerializedAs("ID")]
        public string id; // Unique ID (e.g., "kill_50_enemies")
        [FormerlySerializedAs("Type")]
        public ChallengeType type;
        [FormerlySerializedAs("DescriptionTemplate")]
        public string descriptionTemplate; // "Get {0} kills"
        public string Description => descriptionTemplate;
        [FormerlySerializedAs("MinTarget")]
        public int minTarget = 10;
        [FormerlySerializedAs("MaxTarget")]
        public int maxTarget = 50;
        [FormerlySerializedAs("BaseXPReward")]
        public int baseXpReward = 500;
        
        [FormerlySerializedAs("IsWeekly")]
        public bool isWeekly; // If true, this challenge appears in weekly pool
        
        // Optional filters
        [Tooltip("If Type is WeaponKill, this must match the Weapon Name exactly.")]
        [FormerlySerializedAs("WeaponID")]
        public string weaponID; 
    }
}
