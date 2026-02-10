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
        [Tooltip("Minimum target when this challenge is generated as a weekly challenge.")]
        public int weeklyMinTarget = 10;
        [Tooltip("Maximum target when this challenge is generated as a weekly challenge.")]
        public int weeklyMaxTarget = 50;
        [FormerlySerializedAs("BaseXPReward")]
        public int baseXpReward = 500;

        [Tooltip("Allow this challenge type to appear in the daily challenge pool.")]
        public bool includeInDaily = true;
        [Tooltip("Allow this challenge type to appear in the weekly challenge pool.")]
        [FormerlySerializedAs("isWeekly")]
        [FormerlySerializedAs("IsWeekly")]
        public bool includeInWeekly;
        
        // Optional filters
        [Tooltip("If Type is WeaponKill, this must match the Weapon Name exactly.")]
        [FormerlySerializedAs("WeaponID")]
        public string weaponID; 

        public int GetMinTarget(bool weeklyVariant) => weeklyVariant ? weeklyMinTarget : minTarget;
        public int GetMaxTarget(bool weeklyVariant) => weeklyVariant ? weeklyMaxTarget : maxTarget;
    }
}
