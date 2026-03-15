using System;
using System.Collections.Generic;
using System.Globalization;
using Events;
using UnityEngine;
using Game.Match;

namespace Game.Progression {
    public class ProgressionManager : MonoBehaviour {
        public static ProgressionManager Instance { get; private set; }
        private const DayOfWeek WeeklyResetDay = DayOfWeek.Monday;

        [Header("Settings")]
        [SerializeField] private List<ChallengeDefinition> challengePool;
        [SerializeField] private int baseXp = 1000;
        [SerializeField] private float xpMultiplier = 1.2f;

        public PlayerProgressionData Data { get; private set; }

        private void Awake() {
            if (Instance != null && Instance != this) {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject); // Persist across scenes

            // Autoload challenges if not assigned
            if (challengePool == null || challengePool.Count == 0) {
                var loaded = Resources.LoadAll<ChallengeDefinition>("Challenges");
                if (loaded is { Length: > 0 }) {
                    challengePool = new List<ChallengeDefinition>(loaded);
                } else {
                    Debug.LogWarning("[ProgressionManager] No challenges found in Resources/Challenges!");
                }
            }

            LoadData();
        }

        private void Start() {
            CheckDailyReset();
        }

        private void OnApplicationQuit() {
            SaveData();
        }
        
        // --- Core Data ---

        private void LoadData() {
            Data = ProgressionStore.Load();
            // validate
            if (Data.level < 1) Data.level = 1;

            var normalizedDaily = NormalizeChallengeTargets(Data.dailyChallenges, false);
            var normalizedWeekly = NormalizeChallengeTargets(Data.weeklyChallenges, true);
            if (normalizedDaily || normalizedWeekly) {
                SaveData();
            }
        }

        private bool NormalizeChallengeTargets(List<ActiveChallengeData> challenges, bool weeklyVariant) {
            if (challenges == null || challenges.Count == 0) return false;

            var changed = false;
            foreach (var challenge in challenges) {
                var def = GetChallenge(challenge.challengeID);
                if (def == null) continue;

                var minTarget = Mathf.Max(1, def.GetMinTarget(weeklyVariant));
                var maxTarget = Mathf.Max(minTarget, def.GetMaxTarget(weeklyVariant));
                var clampedTarget = Mathf.Clamp(challenge.targetProgress, minTarget, maxTarget);
                if (challenge.targetProgress != clampedTarget) {
                    challenge.targetProgress = clampedTarget;
                    changed = true;
                }

                if (challenge.currentProgress < 0) {
                    challenge.currentProgress = 0;
                    changed = true;
                }

                if (challenge.currentProgress > challenge.targetProgress) {
                    challenge.currentProgress = challenge.targetProgress;
                    changed = true;
                }

                var shouldBeCompleted = challenge.currentProgress >= challenge.targetProgress;
                if(challenge.isCompleted == shouldBeCompleted) continue;
                challenge.isCompleted = shouldBeCompleted;
                changed = true;
            }

            return changed;
        }

        public void SaveData() {
            if (Data != null) {
                ProgressionStore.Save(Data);
            }
        }

        // --- Match XP Delta ---

        public int CurrentMatchXp { get; private set; }


        // Snapshots for UI animation
        public int StartMatchLevel { get; private set; }
        public int StartMatchCurrentXp { get; private set; } // Current XP for that level

        public void StartMatch() {
            CurrentMatchXp = 0;
            if(Data == null) return;
            StartMatchLevel = Data.level;
            StartMatchCurrentXp = Data.currentXp;
        }

        public void EndMatch() {
            // Commit any final match logic here
            // _currentMatchXP is preserved for UI to read until StartMatch is called again
            SaveData();
        }

        // --- XP & Leveling ---

        public void AddXp(int amount) {
            Data.currentXp += amount;
            Data.totalXp += amount;
            CurrentMatchXp += amount;

            CheckLevelUp();
            // SaveData(); // Optimization: Only save on important events or EndMatch to avoid disk I/O spam
        }

        // --- Stat Tracking Helpers ---

        public void RecordKill(float killSpeed = 0f, bool isGrounded = true, string weaponId = null) {
            Data.stats.kills++; // Changed from _data.Stats.Kills++ to Data.stats.kills++ to match existing code style
            // Check challenges
            // Speed Kill (> 15m/s)
            if (killSpeed > 15f) {
                UpdateChallengeProgress(ChallengeType.SpeedKill, 1);
            }
            // Aerial Kill (not grounded)
            if (!isGrounded) {
                UpdateChallengeProgress(ChallengeType.AerialKill, 1);
            }
            // Weapon Kill
            if (!string.IsNullOrEmpty(weaponId)) {
                UpdateChallengeProgress(ChallengeType.WeaponKill, 1, weaponId); 
            }
            // Generic Kill
            UpdateChallengeProgress(ChallengeType.Kill, 1);
        }

        public void RecordDeath(bool isOob) {
            Data.stats.deaths++;
            if (isOob) Data.stats.oobDeaths++;
        }
        
        public void RecordWin() {
            Data.stats.wins++;
            UpdateChallengeProgress(ChallengeType.Win, 1);
        }

        public void RecordLoss() {
            Data.stats.losses++;
        }

        public void RecordShotFired() {
            Data.stats.shotsFired++;
        }

        public void RecordShotHit() {
            Data.stats.shotsHit++;
        }
        
        public void RecordAirtime(float seconds) {
            Data.stats.totalAirTime += seconds;
        }

        public void RecordGrappleUsed() {
            Data.stats.grapplesUsed++;
        }

        public void RecordJumpPadUsed() {
            Data.stats.jumpPadsUsed++;
        }

        public void UpdateKillStreak(int currentStreak) {
            if (currentStreak > Data.stats.highestKillStreak) {
                Data.stats.highestKillStreak = currentStreak;
            }

            // Check KillStreak challenges
            foreach(var challenge in Data.dailyChallenges) {
                if (challenge.isCompleted) continue;
                var def = GetChallenge(challenge.challengeID);
                if (def == null || def.type != ChallengeType.KillStreak) continue;

                // Update progress to the highest streak achieved
                if (currentStreak > challenge.currentProgress) {
                    challenge.currentProgress = currentStreak;
                }

                if(challenge.currentProgress < challenge.targetProgress) continue;
                challenge.currentProgress = challenge.targetProgress;
                challenge.isCompleted = true;
                challenge.isCompleted = true;
                AddXp(challenge.xpReward);
                // Notify user?
            }
        }

        public void RecordMatchComplete(string gamemode, int placement) {
            // Check MatchesPlayed
            UpdateChallengeProgress(ChallengeType.MatchesPlayed, 1, gamemode);
            
            if (placement <= 5) {
                 UpdateChallengeProgress(ChallengeType.Placement, 1, "top5");
            }
            if (placement <= 3) {
                 UpdateChallengeProgress(ChallengeType.Placement, 1, "top3");
            }
            if (placement == 1) {
                 UpdateChallengeProgress(ChallengeType.Placement, 1, "top1");
            }
        }

        public void RecordWallRunChain(int chainCount) {
             UpdateChallengeProgress(ChallengeType.WallRunChain, chainCount);
        }

        public void RecordTag() {
            UpdateChallengeProgress(ChallengeType.TagCount, 1);
        }

        public void RecordHopballDissolve() {
            UpdateChallengeProgress(ChallengeType.HopballDissolve, 1);
        }

        public void AddDistanceTraveled(float distance) {
            Data.stats.totalDistanceTraveled += distance;
        }

        public void RecordMatchAverageSpeed(float speed) {
            // Keep last 50 matches (user requested efficient storage)
            if (Data.stats.recentMatchAverageSpeeds.Count >= 50) {
                Data.stats.recentMatchAverageSpeeds.RemoveAt(0);
            }
            Data.stats.recentMatchAverageSpeeds.Add(speed);
        }

        // Get the rolling average of recent matches
        public float GetAverageMatchSpeed() {
             if (Data.stats.recentMatchAverageSpeeds.Count == 0) return 0f;
             
             var total = 0f;
             foreach(var s in Data.stats.recentMatchAverageSpeeds) {
                 total += s;
             }
             return total / Data.stats.recentMatchAverageSpeeds.Count;
        }

        public void AddTimeHoldingHopball(float seconds) {
            Data.stats.timeHoldingHopball += seconds;
        }

        public void AddTimeAsKing(float seconds) {
            Data.stats.timeAsKing += seconds;
        }

        public void AddTimeTagged(float seconds) {
            Data.stats.timeTagged += seconds;
        }

        private void UpdateChallengeProgress(ChallengeType type, int amount, string contextId = null) {
            var anyChange = false;
            anyChange |= CheckChallengeList(Data.dailyChallenges, type, amount, contextId);
            anyChange |= CheckChallengeList(Data.weeklyChallenges, type, amount, contextId);
            if(anyChange) {
                NotifyChallengesUpdated();
            }
        }

        private bool CheckChallengeList(List<ActiveChallengeData> list, ChallengeType type, int amount, string contextId) {
             if(list == null || list.Count == 0 || amount == 0) return false;
             var changed = false;
             foreach(var challenge in list) {
                if (challenge.isCompleted) continue;
                var def = GetChallenge(challenge.challengeID);
                if (def == null || def.type != type) continue;

                // Get the filter from the active challenge (dynamic) or definition (static)
                var filterToUse = !string.IsNullOrEmpty(challenge.filterID) ? challenge.filterID : def.weaponID;

                switch(type) {
                    // Validate Weapon Filter / Context
                    case ChallengeType.WeaponKill when !string.IsNullOrEmpty(filterToUse) && contextId != filterToUse && contextId != filterToUse:
                    // Validate Gamemode for MatchesPlayed (uses filterID from active challenge)
                    case ChallengeType.MatchesPlayed when !string.IsNullOrEmpty(filterToUse) && contextId != filterToUse && contextId != filterToUse:
                    // Validate Placement Context (top5, top3 etc)
                    case ChallengeType.Placement when !string.IsNullOrEmpty(filterToUse) && contextId != filterToUse && contextId != filterToUse:
                        continue;
                }

                var previousProgress = challenge.currentProgress;
                challenge.currentProgress += amount;
                if(challenge.currentProgress > challenge.targetProgress) {
                    challenge.currentProgress = challenge.targetProgress;
                }

                if(challenge.currentProgress != previousProgress) {
                    changed = true;
                }

                if(challenge.currentProgress < challenge.targetProgress) continue;
                if(challenge.isCompleted) continue;
                challenge.isCompleted = true;
                changed = true;
                AddXp(challenge.xpReward);
                // Notify user?
             }
             return changed;
        }

        private float _nextChallengeCheckTime;
        private const float ChallengeCheckInterval = 1f; // Check frequently (1s) to be responsive when timer hits 0

        private void Update() {
            if(Data == null) return;
            // Only track playtime if in active match
            if (IsMatchActive()) {
                Data.stats.totalPlayTimeSeconds += Time.deltaTime;
            }

            // Check for challenge reset periodically
            if(!(Time.time >= _nextChallengeCheckTime)) return;
            CheckDailyReset();
            CheckWeeklyReset(); // Also check weekly
            _nextChallengeCheckTime = Time.time + ChallengeCheckInterval;
        }

        private static bool IsMatchActive() {
            // 1. Must have MatchTimerManager (implies we are in a game scene)
            if (MatchTimerManager.Instance == null) return false;

            // 2. Must not be in pre-match
            if (MatchTimerManager.Instance.IsPreMatch) return false;

            // 3. Timer must be running, unless this match is configured as infinite time.
            var settings = MatchSettingsManager.Instance;
            var isInfiniteTimer = settings != null && settings.IsInfiniteMatchTimer();
            if (!isInfiniteTimer && MatchTimerManager.Instance.TimeRemainingSeconds <= 0) return false;

            // 4. Must not be in post-match flow
            return PostMatchManager.Instance == null || !PostMatchManager.Instance.PostMatchFlowStarted;
        }

        private void CheckLevelUp() {
            var xpRequired = GetXpForLevel(Data.level);
            var leveledUp = false;

            while (Data.currentXp >= xpRequired) {
                Data.currentXp -= xpRequired;
                Data.level++;
                leveledUp = true;
                
                // Recalculate for next level
                xpRequired = GetXpForLevel(Data.level);
            }
            
            if (leveledUp) {
                Debug.Log($"[Progression] Leveled Up! New Level: {Data.level}");
            }
        }

        public int GetXpForLevel(int level) {
            // Formula: Base * (Multiplier ^ (Level - 1))
            return Mathf.FloorToInt(baseXp * Mathf.Pow(xpMultiplier, level - 1));
        }

        // --- Challenges ---

        private void CheckDailyReset() {
            var needsReset = false;
            var now = DateTime.Now;
            var todayMidnight = now.Date;

            if (string.IsNullOrEmpty(Data.lastDailyReset)) {
                needsReset = true;
            } else {
                if (DateTime.TryParse(Data.lastDailyReset, out var lastReset)) {
                    // Calendar-based daily rollover: reset once local date crosses midnight.
                    if (lastReset < todayMidnight) {
                        needsReset = true;
                    }
                } else {
                    needsReset = true;
                }
            }

            if (!needsReset && HasInvalidChallenges(Data.dailyChallenges, false)) {
                needsReset = true;
            }

            if (needsReset) {
                GenerateDailyChallenges();
            }
            
            CheckWeeklyReset();
        }
        
        private void CheckWeeklyReset() {
             var needsReset = false;
             var now = DateTime.Now;
             var currentWeeklyBoundary = GetWeeklyBoundary(now);

             if (string.IsNullOrEmpty(Data.lastWeeklyReset)) {
                 needsReset = true;
             } else {
                 if (DateTime.TryParse(Data.lastWeeklyReset, out var lastReset)) {
                     // Calendar-based weekly rollover: reset once we cross the configured weekly boundary at midnight.
                     if (lastReset < currentWeeklyBoundary) {
                         needsReset = true;
                     }
                 } else {
                     needsReset = true;
                 }
             }

             if (!needsReset && HasInvalidChallenges(Data.weeklyChallenges, true)) {
                 needsReset = true;
             }
 
             if (needsReset) {
                 GenerateWeeklyChallenges();
             }
        }

        private bool HasInvalidChallenges(List<ActiveChallengeData> challenges, bool weeklyVariant) {
            if (challenges == null || challenges.Count == 0) return true;

            foreach (var challenge in challenges) {
                if (string.IsNullOrEmpty(challenge.challengeID)) return true;
                var def = GetChallenge(challenge.challengeID);
                if (def == null) return true;
                var minTarget = Mathf.Max(1, def.GetMinTarget(weeklyVariant));
                var maxTarget = Mathf.Max(minTarget, def.GetMaxTarget(weeklyVariant));
                if (challenge.targetProgress < minTarget || challenge.targetProgress > maxTarget) return true;
            }

            return false;
        }

        public TimeSpan GetTimeUntilDailyReset() {
             if (Data == null) return TimeSpan.Zero;

             var now = DateTime.Now;
             var nextReset = now.Date.AddDays(1); // Next local midnight
             var remaining = nextReset - now;
             return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public TimeSpan GetTimeUntilWeeklyReset() {
             if (Data == null) return TimeSpan.Zero;

             var now = DateTime.Now;
             var nextReset = GetWeeklyBoundary(now).AddDays(7);
             var remaining = nextReset - now;
             return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        private static DateTime GetWeeklyBoundary(DateTime now) {
            var midnightToday = now.Date;
            var daysSinceResetDay = ((int)midnightToday.DayOfWeek - (int)WeeklyResetDay + 7) % 7;
            return midnightToday.AddDays(-daysSinceResetDay);
        }

        // Available gamemodes for dynamic "play_matches_of" challenges
        private static readonly string[] AvailableGamemodes = {
            "Deathmatch", "TeamDeathmatch", "Hopball", "KingOfTheHill", "GunTag"
        };
        
        private static readonly Dictionary<string, string> GamemodeDisplayNames = new() {
            { "Deathmatch", "Deathmatch" },
            { "TeamDeathmatch", "Team Deathmatch" },
            { "Hopball", "Hopball" },
            { "KingOfTheHill", "KOTH" },
            { "GunTag", "Gun Tag" }
        };
        
        // Available weapons for dynamic "weapon_kills" challenges
        private static readonly string[] AvailableWeapons = {
            "Deagle", "M1911", "AK74", "Bennelli_M4", "Uzi", "M107"
        };
        
        private static readonly Dictionary<string, string> WeaponDisplayNames = new() {
            { "Deagle", "Deagle" },
            { "M1911", "Pistol" },
            { "AK74", "Assault Rifle" },
            { "Bennelli_M4", "Shotgun" },
            { "Uzi", "SMG" },
            { "M107", "Sniper Rifle" }
        };

        private void GenerateDailyChallenges() {
            if (challengePool == null || challengePool.Count == 0) return;

            Data.dailyChallenges.Clear();
            Data.lastDailyReset = DateTime.Now.ToString(CultureInfo.InvariantCulture);
            
            // Filter pool for Daily using explicit per-challenge inclusion flags.
            var dailyPool = challengePool.FindAll(c => c.includeInDaily);
            if (dailyPool.Count == 0) {
                Debug.LogError("[Progression] Daily challenge pool is empty. Enable 'Include In Daily' on at least one challenge definition.");
                return;
            }

            // Pick 3 random challenges
            var activeIds = GetActiveChallengeIDs();
            var usedGamemodes = new HashSet<string>(); // Track gamemodes for play_matches_of
            var usedWeapons = new HashSet<string>();   // Track weapons for weapon_kills
            var playMatchesChallengeCount = 0;
            var weaponKillsChallengeCount = 0;
            var addedCount = 0;
            var maxAttempts = 50; // Safety break

            while(addedCount < 3 && maxAttempts > 0) {
                maxAttempts--;
                var def = dailyPool[UnityEngine.Random.Range(0, dailyPool.Count)];

                switch(def.id) {
                    // Special handling for play_matches_of (allow up to 2, but no duplicate gamemodes)
                    case "play_matches_of" when playMatchesChallengeCount >= 2:
                        continue; // Max 2 of this type
                    // Pick a random unused gamemode
                    case "play_matches_of": {
                        var availableForPicking = new List<string>();
                        foreach(var gm in AvailableGamemodes) {
                            if (!usedGamemodes.Contains(gm)) availableForPicking.Add(gm);
                        }
                        if (availableForPicking.Count == 0) continue; // All gamemodes used
                    
                        var chosenGamemode = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                        usedGamemodes.Add(chosenGamemode);
                        playMatchesChallengeCount++;
                    
                        var minTarget = Mathf.Max(1, def.GetMinTarget(false));
                        var maxTarget = Mathf.Max(minTarget, def.GetMaxTarget(false));
                        var target = UnityEngine.Random.Range(minTarget, maxTarget + 1);
                        var reward = CalculateXpReward(def, target, false);
                    
                        var challenge = new ActiveChallengeData {
                            challengeID = def.id,
                            filterID = chosenGamemode,
                            targetProgress = target,
                            currentProgress = 0,
                            xpReward = reward,
                            isCompleted = false
                        };
                        Data.dailyChallenges.Add(challenge);
                        addedCount++;
                        continue;
                    }
                    // Special handling for weapon_kills (allow up to 2, but no duplicate weapons)
                    case "weapon_kills" when weaponKillsChallengeCount >= 2:
                        continue; // Max 2 of this type
                    // Pick a random unused weapon
                    case "weapon_kills": {
                        var availableForPicking = new List<string>();
                        foreach(var w in AvailableWeapons) {
                            if (!usedWeapons.Contains(w)) availableForPicking.Add(w);
                        }
                        if (availableForPicking.Count == 0) continue; // All weapons used
                    
                        var chosenWeapon = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                        usedWeapons.Add(chosenWeapon);
                        weaponKillsChallengeCount++;
                    
                        var minTarget = Mathf.Max(1, def.GetMinTarget(false));
                        var maxTarget = Mathf.Max(minTarget, def.GetMaxTarget(false));
                        var target = UnityEngine.Random.Range(minTarget, maxTarget + 1);
                        var reward = CalculateXpReward(def, target, false);
                    
                        var challenge = new ActiveChallengeData {
                            challengeID = def.id,
                            filterID = chosenWeapon,
                            targetProgress = target,
                            currentProgress = 0,
                            xpReward = reward,
                            isCompleted = false
                        };
                        Data.dailyChallenges.Add(challenge);
                        addedCount++;
                        continue;
                    }
                }

                // Standard duplicate check for other challenges
                if (activeIds.Contains(def.id)) continue;

                var standardTarget = UnityEngine.Random.Range(def.minTarget, def.maxTarget + 1);
                var standardReward = CalculateXpReward(def, standardTarget, false);
                
                var standardChallenge = new ActiveChallengeData {
                    challengeID = def.id,
                    filterID = def.weaponID, // Use static filter if defined
                    targetProgress = standardTarget,
                    currentProgress = 0,
                    xpReward = standardReward,
                    isCompleted = false
                };
                Data.dailyChallenges.Add(standardChallenge);
                activeIds.Add(def.id); // Mark as used for this session
                addedCount++;
            }
            
            SaveData();
            Debug.Log($"[Progression] Generated {addedCount} new Daily Challenges.");
            NotifyChallengesUpdated();
        }
        
        private void GenerateWeeklyChallenges() {
            if (challengePool == null || challengePool.Count == 0) return;

            Data.weeklyChallenges.Clear();
            Data.lastWeeklyReset = DateTime.Now.ToString(CultureInfo.InvariantCulture);
            
            // Filter pool for Weekly using explicit per-challenge inclusion flags.
            var weeklyPool = challengePool.FindAll(c => c.includeInWeekly);
            if (weeklyPool.Count == 0) {
                Debug.LogError("[Progression] Weekly challenge pool is empty. Enable 'Include In Weekly' on at least one challenge definition.");
                return;
            }

            // Pick 5 random challenges
            var activeIds = GetActiveChallengeIDs();
            var usedGamemodes = new HashSet<string>(); // Track gamemodes for play_matches_of
            var usedWeapons = new HashSet<string>();   // Track weapons for weapon_kills
            var playMatchesChallengeCount = 0;
            var weaponKillsChallengeCount = 0;
            var addedCount = 0;
            var maxAttempts = 100;

            while(addedCount < 5 && maxAttempts > 0) {
                maxAttempts--;
                var def = weeklyPool[UnityEngine.Random.Range(0, weeklyPool.Count)];

                switch(def.id) {
                    // Special handling for play_matches_of (allow up to 2, but no duplicate gamemodes)
                    case "play_matches_of" when playMatchesChallengeCount >= 2:
                        continue;
                    case "play_matches_of": {
                        var availableForPicking = new List<string>();
                        foreach (var gm in AvailableGamemodes) {
                            if (!usedGamemodes.Contains(gm)) {
                                availableForPicking.Add(gm);
                            }
                        }

                        if (availableForPicking.Count == 0) continue;

                        var chosenGamemode = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                        usedGamemodes.Add(chosenGamemode);
                        playMatchesChallengeCount++;

                        var gamemodeMinTarget = Mathf.Max(1, def.GetMinTarget(true));
                        var gamemodeMaxTarget = Mathf.Max(gamemodeMinTarget, def.GetMaxTarget(true));
                        var gamemodeTarget = UnityEngine.Random.Range(gamemodeMinTarget, gamemodeMaxTarget + 1);
                        var gamemodeReward = CalculateXpReward(def, gamemodeTarget, true);

                        var challenge = new ActiveChallengeData {
                            challengeID = def.id,
                            filterID = chosenGamemode,
                            targetProgress = gamemodeTarget,
                            currentProgress = 0,
                            xpReward = gamemodeReward,
                            isCompleted = false
                        };
                        Data.weeklyChallenges.Add(challenge);
                        activeIds.Add(def.id);
                        addedCount++;
                        continue;
                    }
                    // Special handling for weapon_kills (allow up to 2, but no duplicate weapons)
                    case "weapon_kills" when weaponKillsChallengeCount >= 2:
                        continue;
                    case "weapon_kills": {
                        var availableForPicking = new List<string>();
                        foreach (var w in AvailableWeapons) {
                            if (!usedWeapons.Contains(w)) {
                                availableForPicking.Add(w);
                            }
                        }

                        if (availableForPicking.Count == 0) continue;

                        var chosenWeapon = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                        usedWeapons.Add(chosenWeapon);
                        weaponKillsChallengeCount++;

                        var weaponMinTarget = Mathf.Max(1, def.GetMinTarget(true));
                        var weaponMaxTarget = Mathf.Max(weaponMinTarget, def.GetMaxTarget(true));
                        var weaponTarget = UnityEngine.Random.Range(weaponMinTarget, weaponMaxTarget + 1);
                        var weaponReward = CalculateXpReward(def, weaponTarget, true);

                        var challenge = new ActiveChallengeData {
                            challengeID = def.id,
                            filterID = chosenWeapon,
                            targetProgress = weaponTarget,
                            currentProgress = 0,
                            xpReward = weaponReward,
                            isCompleted = false
                        };
                        Data.weeklyChallenges.Add(challenge);
                        activeIds.Add(def.id);
                        addedCount++;
                        continue;
                    }
                }

                // Prevent duplicate
                if (activeIds.Contains(def.id)) continue;

                // Weekly targets use explicit weekly challenge bounds.
                var standardMinTarget = Mathf.Max(1, def.GetMinTarget(true));
                var standardMaxTarget = Mathf.Max(standardMinTarget, def.GetMaxTarget(true));
                var standardTarget = UnityEngine.Random.Range(standardMinTarget, standardMaxTarget + 1);
                var standardReward = CalculateXpReward(def, standardTarget, true);

                var standardChallenge = new ActiveChallengeData {
                    challengeID = def.id,
                    filterID = def.weaponID,
                    targetProgress = standardTarget,
                    currentProgress = 0,
                    xpReward = standardReward,
                    isCompleted = false
                };
                Data.weeklyChallenges.Add(standardChallenge);
                activeIds.Add(def.id);
                addedCount++;
            }
            
            SaveData();
            Debug.Log($"[Progression] Generated {addedCount} new Weekly Challenges.");
            NotifyChallengesUpdated();
        }

        private static void NotifyChallengesUpdated() {
            EventBus.Publish(new ChallengesUpdatedEvent());
        }
        
        public ChallengeDefinition GetChallenge(string id) {
            return challengePool.Find(c => c.id == id);
        }
        
        public string GetGamemodeName(string gamemodeId) {
            return string.IsNullOrEmpty(gamemodeId) ? "" : GamemodeDisplayNames.GetValueOrDefault(gamemodeId, gamemodeId);
        }
        
        public string GetWeaponName(string weaponId) {
            return string.IsNullOrEmpty(weaponId) ? "" : WeaponDisplayNames.GetValueOrDefault(weaponId, weaponId);
        }
        
        // Unified filter display name lookup (tries gamemode first, then weapon)
        public static string GetFilterName(string filterId) {
            if (string.IsNullOrEmpty(filterId)) return "";
            return GamemodeDisplayNames.TryGetValue(filterId, out var gmName) ? gmName : WeaponDisplayNames.GetValueOrDefault(filterId, filterId);
        }
        
        private HashSet<string> GetActiveChallengeIDs() {
            var ids = new HashSet<string>();
            if (Data.dailyChallenges != null) {
                foreach(var c in Data.dailyChallenges) ids.Add(c.challengeID);
            }

            if(Data.weeklyChallenges == null) return ids;
            {
                foreach(var c in Data.weeklyChallenges) ids.Add(c.challengeID);
            }
            return ids;
        }

        private static int CalculateXpReward(ChallengeDefinition def, int target, bool weeklyVariant) {
            var minTargetForVariant = Mathf.Max(1, def.GetMinTarget(weeklyVariant));
            if (minTargetForVariant <= 0) return def.baseXpReward;
            // BaseXPReward is amount for MinTarget effort.
            // Scale linearly: if target is 2x MinTarget, reward is 2x Base.
            var scale = (float)target / minTargetForVariant;
            return Mathf.RoundToInt(def.baseXpReward * scale);
        }
        
        // --- Debug / Testing ---
        
        [ContextMenu("Debug: Add 1000 XP")]
        public void DebugAddXp() {
            AddXp(1000);
        }

        [ContextMenu("Debug: Force Reset Challenges")]
        public void DebugResetChallenges() {
            GenerateDailyChallenges();
            GenerateWeeklyChallenges();
        }
    }
}
