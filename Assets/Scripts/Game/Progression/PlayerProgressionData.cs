using System;
using System.Collections.Generic;

namespace Game.Progression {
    [Serializable]
    public class PlayerProgressionData {
        public int level = 1;
        public int currentXp;
        public int totalXp;
        // Stats
        public PlayerStats stats = new();

        public float playTimeSeconds;
        
        // Challenges
        public List<ActiveChallengeData> dailyChallenges = new();
        public string lastDailyReset; // Stored as DateTime string
        
        public List<ActiveChallengeData> weeklyChallenges = new();
        public string lastWeeklyReset; // Stored as DateTime string
    }

    [Serializable]
    public class PlayerStats {
        // Combat
        public int kills;
        public int wins;
        public int losses;
        public int deaths;
        public int shotsFired;
        public int shotsHit;
        public int highestKillStreak;
        public int oobDeaths; // Out of Bounds deaths
        public List<float> recentMatchAverageSpeeds = new(); // Rolling average buffer
        
        // Time / Objective
        public float totalPlayTimeSeconds; // Lifetime playtime
        public float timeHoldingHopball; // Seconds
        public float timeAsKing; // Seconds (KOTH)
        public float timeTagged; // Seconds (Tag)
        
        // Movement
        public double totalDistanceTraveled; // use double for large accretion? float might lose precision over years. Double is safer.
        public float totalAirTime; // Seconds in air
        public int grapplesUsed;
        public int jumpPadsUsed;
        // Average Speed can be calculated: TotalDistanceTraveled / TotalPlayTimeSeconds
        
        public int highestHypeRank; // 0=D, 1=C, ... 6=SSS
    }

    [Serializable]
    public class ActiveChallengeData {
        public string challengeID;
        public string filterID; // Dynamic filter (gamemode, weapon, etc.) set at generation time
        public int currentProgress;
        public int targetProgress;
        public int xpReward;
        public bool isCompleted;
    }
}
