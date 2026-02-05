using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Game.Match;

namespace Game.Progression {
    public class ProgressionManager : MonoBehaviour {
        public static ProgressionManager Instance { get; private set; }

        [Header("Settings")]
        [SerializeField] private List<ChallengeDefinition> challengePool;
        [SerializeField] private int baseXp = 1000;
        [SerializeField] private float xpMultiplier = 1.2f;

        public PlayerProgressionData Data { get; private set; }

        public event Action<int> OnLevelUp;
        public event Action<int> OnXpAdded;

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
                if (loaded != null && loaded.Length > 0) {
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
            
            OnXpAdded?.Invoke(amount);

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
                var def = GetChallengeDefinition(challenge.challengeID);
                if (def == null || def.Type != ChallengeType.KillStreak) continue;

                // Update progress to highest streak achieved
                if (currentStreak > challenge.currentProgress) {
                    challenge.currentProgress = currentStreak;
                }

                if (challenge.currentProgress >= challenge.targetProgress) {
                    challenge.currentProgress = challenge.targetProgress;
                    challenge.isCompleted = true;
                    challenge.isCompleted = true;
                    AddXp(challenge.xpReward);
                    // Notify user?
                }
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
             
             float total = 0f;
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
            CheckChallengeList(Data.dailyChallenges, type, amount, contextId);
            CheckChallengeList(Data.weeklyChallenges, type, amount, contextId);
        }

        private void CheckChallengeList(List<ActiveChallengeData> list, ChallengeType type, int amount, string contextId) {
             foreach(var challenge in list) {
                if (challenge.isCompleted) continue;
                var def = GetChallengeDefinition(challenge.challengeID);
                if (def == null || def.Type != type) continue;

                // Get the filter from the active challenge (dynamic) or definition (static)
                var filterToUse = !string.IsNullOrEmpty(challenge.filterID) ? challenge.filterID : def.WeaponID;

                // Validate Weapon Filter / Context
                if (type == ChallengeType.WeaponKill && !string.IsNullOrEmpty(filterToUse)) {
                    if (contextId != filterToUse) continue;
                }
                
                // Validate Gamemode for MatchesPlayed (uses filterID from active challenge)
                if (type == ChallengeType.MatchesPlayed && !string.IsNullOrEmpty(filterToUse)) {
                     if (contextId != filterToUse) continue;
                }
                
                // Validate Placement Context (top5, top3 etc)
                if (type == ChallengeType.Placement && !string.IsNullOrEmpty(filterToUse)) {
                     if (contextId != filterToUse) continue;
                }

                challenge.currentProgress += amount;
                if (challenge.currentProgress >= challenge.targetProgress) {
                    challenge.currentProgress = challenge.targetProgress;
                    challenge.isCompleted = true;
                    AddXp(challenge.xpReward);
                    // Notify user?
                }
             }
        }
        private void Update() {
            if (Data != null) {
                // Only track playtime if in active match
                if (IsMatchActive()) {
                    Data.stats.totalPlayTimeSeconds += Time.deltaTime;
                }
            }
        }

        private bool IsMatchActive() {
            // 1. Must have MatchTimerManager (implies we are in a game scene)
            if (MatchTimerManager.Instance == null) return false;

            // 2. Must not be in pre-match
            if (MatchTimerManager.Instance.IsPreMatch) return false;

            // 3. Timer must be running
            if (MatchTimerManager.Instance.TimeRemainingSeconds <= 0) return false;

            // 4. Must not be in post-match flow
            if (PostMatchManager.Instance != null && PostMatchManager.Instance.PostMatchFlowStarted) return false;

            return true;
        }

        private void CheckLevelUp() {
            var xpRequired = GetXpRequiredForLevel(Data.level);
            var leveledUp = false;

            while (Data.currentXp >= xpRequired) {
                Data.currentXp -= xpRequired;
                Data.level++;
                OnLevelUp?.Invoke(Data.level);
                leveledUp = true;
                
                // Recalculate for next level
                xpRequired = GetXpRequiredForLevel(Data.level);
            }
            
            if (leveledUp) {
                Debug.Log($"[Progression] Leveled Up! New Level: {Data.level}");
            }
        }

        public int GetXpRequiredForLevel(int level) {
            // Formula: Base * (Multiplier ^ (Level - 1))
            return Mathf.FloorToInt(baseXp * Mathf.Pow(xpMultiplier, level - 1));
        }

        // --- Challenges ---

        private void CheckDailyReset() {
            var needsReset = false;
            if (string.IsNullOrEmpty(Data.lastDailyReset)) {
                needsReset = true;
            } else {
                if (DateTime.TryParse(Data.lastDailyReset, out DateTime lastReset)) {
                    if ((DateTime.Now - lastReset).TotalHours >= 24) {
                        needsReset = true;
                    }
                } else {
                    needsReset = true;
                }
            }

            if (needsReset) {
                GenerateDailyChallenges();
            }
            
            CheckWeeklyReset();
        }
        
        private void CheckWeeklyReset() {
             var needsReset = false;
             if (string.IsNullOrEmpty(Data.lastWeeklyReset)) {
                 needsReset = true;
             } else {
                 if (DateTime.TryParse(Data.lastWeeklyReset, out DateTime lastReset)) {
                     if ((DateTime.Now - lastReset).TotalDays >= 7) {
                         needsReset = true;
                     }
                 } else {
                     needsReset = true;
                 }
             }
 
             if (needsReset) {
                 GenerateWeeklyChallenges();
             }
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
            
            // Filter pool for Daily (IsWeekly == false)
            var dailyPool = challengePool.FindAll(c => !c.IsWeekly);
            if (dailyPool.Count == 0) dailyPool = challengePool; // Fallback

            // Pick 3 random challenges
            var activeIds = GetActiveChallengeIDs();
            var usedGamemodes = new HashSet<string>(); // Track gamemodes for play_matches_of
            var usedWeapons = new HashSet<string>();   // Track weapons for weapon_kills
            int playMatchesChallengeCount = 0;
            int weaponKillsChallengeCount = 0;
            int addedCount = 0;
            int maxAttempts = 50; // Safety break

            while(addedCount < 3 && maxAttempts > 0) {
                maxAttempts--;
                var def = dailyPool[UnityEngine.Random.Range(0, dailyPool.Count)];
                
                // Special handling for play_matches_of (allow up to 2, but no duplicate gamemodes)
                if (def.ID == "play_matches_of") {
                    if (playMatchesChallengeCount >= 2) continue; // Max 2 of this type
                    
                    // Pick a random unused gamemode
                    var availableForPicking = new List<string>();
                    foreach(var gm in AvailableGamemodes) {
                        if (!usedGamemodes.Contains(gm)) availableForPicking.Add(gm);
                    }
                    if (availableForPicking.Count == 0) continue; // All gamemodes used
                    
                    var chosenGamemode = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                    usedGamemodes.Add(chosenGamemode);
                    playMatchesChallengeCount++;
                    
                    var target = UnityEngine.Random.Range(def.MinTarget, def.MaxTarget + 1);
                    var reward = CalculateXpReward(def, target);
                    
                    var challenge = new ActiveChallengeData {
                        challengeID = def.ID,
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
                if (def.ID == "weapon_kills") {
                    if (weaponKillsChallengeCount >= 2) continue; // Max 2 of this type
                    
                    // Pick a random unused weapon
                    var availableForPicking = new List<string>();
                    foreach(var w in AvailableWeapons) {
                        if (!usedWeapons.Contains(w)) availableForPicking.Add(w);
                    }
                    if (availableForPicking.Count == 0) continue; // All weapons used
                    
                    var chosenWeapon = availableForPicking[UnityEngine.Random.Range(0, availableForPicking.Count)];
                    usedWeapons.Add(chosenWeapon);
                    weaponKillsChallengeCount++;
                    
                    var target = UnityEngine.Random.Range(def.MinTarget, def.MaxTarget + 1);
                    var reward = CalculateXpReward(def, target);
                    
                    var challenge = new ActiveChallengeData {
                        challengeID = def.ID,
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
                
                // Standard duplicate check for other challenges
                if (activeIds.Contains(def.ID)) continue;

                var standardTarget = UnityEngine.Random.Range(def.MinTarget, def.MaxTarget + 1);
                var standardReward = CalculateXpReward(def, standardTarget);
                
                var standardChallenge = new ActiveChallengeData {
                    challengeID = def.ID,
                    filterID = def.WeaponID, // Use static filter if defined
                    targetProgress = standardTarget,
                    currentProgress = 0,
                    xpReward = standardReward,
                    isCompleted = false
                };
                Data.dailyChallenges.Add(standardChallenge);
                activeIds.Add(def.ID); // Mark as used for this session
                addedCount++;
            }
            
            SaveData();
            Debug.Log($"[Progression] Generated {addedCount} new Daily Challenges.");
        }
        
        private void GenerateWeeklyChallenges() {
            if (challengePool == null || challengePool.Count == 0) return;

            Data.weeklyChallenges.Clear();
            Data.lastWeeklyReset = DateTime.Now.ToString(CultureInfo.InvariantCulture);
            
            // Filter pool for Weekly (IsWeekly == true)
            var weeklyPool = challengePool.FindAll(c => c.IsWeekly);
            // Fallback: If no weekly challenges defined, use daily ones but scale target? 
            // For now, just use general pool if no strict weekly challenges found, or warn.
            if (weeklyPool.Count == 0) {
                 // Option: fallback to general pool but mark as weekly in context? 
                 // Effectively, if we haven't made any weekly-specific ones, we might just pick 5 random ones.
                 weeklyPool = challengePool;
            }

            // Pick 5 random challenges
            var activeIds = GetActiveChallengeIDs();
            int addedCount = 0;
            int maxAttempts = 100;

            while(addedCount < 5 && maxAttempts > 0) {
                maxAttempts--;
                var def = weeklyPool[UnityEngine.Random.Range(0, weeklyPool.Count)];
                
                // Prevent duplicate
                if (activeIds.Contains(def.ID)) continue;

                // Scale target for Weekly
                var target = UnityEngine.Random.Range(def.MinTarget * 3, def.MaxTarget * 5);
                var reward = CalculateXpReward(def, target);
                
                var challenge = new ActiveChallengeData {
                    challengeID = def.ID,
                    targetProgress = target,
                    currentProgress = 0,
                    xpReward = reward,
                    isCompleted = false
                };
                Data.weeklyChallenges.Add(challenge);
                activeIds.Add(def.ID);
                addedCount++;
            }
            
            SaveData();
            Debug.Log($"[Progression] Generated {addedCount} new Weekly Challenges.");
        }
        
        public ChallengeDefinition GetChallengeDefinition(string id) {
            return challengePool.Find(c => c.ID == id);
        }
        
        public string GetGamemodeDisplayName(string gamemodeId) {
            if (string.IsNullOrEmpty(gamemodeId)) return "";
            return GamemodeDisplayNames.TryGetValue(gamemodeId, out var name) ? name : gamemodeId;
        }
        
        public string GetWeaponDisplayName(string weaponId) {
            if (string.IsNullOrEmpty(weaponId)) return "";
            return WeaponDisplayNames.TryGetValue(weaponId, out var name) ? name : weaponId;
        }
        
        // Unified filter display name lookup (tries gamemode first, then weapon)
        public string GetFilterDisplayName(string filterId) {
            if (string.IsNullOrEmpty(filterId)) return "";
            if (GamemodeDisplayNames.TryGetValue(filterId, out var gmName)) return gmName;
            if (WeaponDisplayNames.TryGetValue(filterId, out var wpName)) return wpName;
            return filterId;
        }
        
        private HashSet<string> GetActiveChallengeIDs() {
            var ids = new HashSet<string>();
            if (Data.dailyChallenges != null) {
                foreach(var c in Data.dailyChallenges) ids.Add(c.challengeID);
            }
            if (Data.weeklyChallenges != null) {
                foreach(var c in Data.weeklyChallenges) ids.Add(c.challengeID);
            }
            return ids;
        }

        private int CalculateXpReward(ChallengeDefinition def, int target) {
            if (def.MinTarget <= 0) return def.BaseXPReward;
            // BaseXPReward is amount for MinTarget effort.
            // Scale linearly: if target is 2x MinTarget, reward is 2x Base.
            float scale = (float)target / def.MinTarget;
            return Mathf.RoundToInt(def.BaseXPReward * scale);
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
