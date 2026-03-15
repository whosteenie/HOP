using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.Player.Core;
using Network.Steam;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;

namespace Game.UI.Screens.Scoreboard {
    /// <summary>Builds scoreboard rows from template and updates speaking indicators.</summary>
    internal sealed class ScoreboardRowFactory {
        private readonly VisualTreeAsset _template;
        private readonly ScoreboardPlayerData _playerData;
        private readonly Sprite[] _iconSprites;
        private readonly UnityEngine.Object _logContext;
        private readonly Dictionary<ulong, VisualElement> _speakingIndicators = new();
        private bool _missingTemplateLogged;
        private bool _invalidTemplateLogged;

        public ScoreboardRowFactory(VisualTreeAsset template, ScoreboardPlayerData playerData, Sprite[] iconSprites,
            UnityEngine.Object logContext = null) {
            _template = template;
            _playerData = playerData;
            _iconSprites = iconSprites;
            _logContext = logContext;
        }

        public void ClearSpeakingIndicators() {
            _speakingIndicators.Clear();
        }

        public void UpdateSpeakingIndicators(IReadOnlyCollection<PlayerController> controllers) {
            if(Social.VoiceManager.Instance == null) return;
            var voiceMgr = Social.VoiceManager.Instance;
            foreach(var player in controllers) {
                if(player == null || !_speakingIndicators.TryGetValue(player.OwnerClientId, out var indicator))
                    continue;
                var steamId = player.SteamId.Value;
                if(steamId == 0) continue;
                if(voiceMgr.IsSpeaking(steamId.ToString()))
                    indicator.AddToClassList("active");
                else
                    indicator.RemoveFromClassList("active");
            }
        }

        public bool EnsureTemplateAssigned() {
            if(_template != null) return true;
            if(_missingTemplateLogged) return false;
            _missingTemplateLogged = true;
            Debug.LogError(
                "[ScoreboardManager] Missing `scoreboardRowTemplate` assignment. Assign a scoreboard row VisualTreeAsset in the inspector.",
                _logContext);
            return false;
        }

        public VisualElement CreatePlayerRow(PlayerController player, VisualElement parentContainer, bool isTagMode) {
            var row = CreatePlayerRowBase(player, parentContainer);
            if(row == null) return null;
            var statLabels = GetRowStatLabels(row);
            if(statLabels == null) return row;
            if(isTagMode) {
                var tagCtrl = _playerData.GetTagController(player);
                var timeTaggedVal = tagCtrl != null ? tagCtrl.TimeTagged.Value : 0;
                var tagsVal = tagCtrl != null ? tagCtrl.Tags.Value : 0;
                var taggedVal = tagCtrl != null ? tagCtrl.Tagged.Value : 0;
                statLabels[0].style.display = statLabels[1].style.display = statLabels[2].style.display =
                    statLabels[3].style.display = statLabels[6].style.display = DisplayStyle.Flex;
                statLabels[4].style.display = statLabels[5].style.display = DisplayStyle.None;
                statLabels[0].text = timeTaggedVal.ToString();
                statLabels[1].text = tagsVal.ToString();
                statLabels[2].text = taggedVal.ToString();
                var ttr = ScoreboardPlayerData.CalculateTtr(tagsVal, taggedVal);
                statLabels[3].text = ttr.ToString("F2");
                if(ttr >= 2.0f) statLabels[3].AddToClassList("player-stat-highlight");
                else statLabels[3].RemoveFromClassList("player-stat-highlight");
                statLabels[4].text = statLabels[5].text = string.Empty;
                statLabels[6].text = _playerData.GetAverageVelocityText(player);
            } else {
                AddNormalModeStats(row, player);
            }

            return row;
        }

        public VisualElement CreatePlayerRow(PlayerController player, VisualElement parentContainer,
            bool simplifiedStats, bool isYourTeam) {
            if(!simplifiedStats) return CreatePlayerRow(player, parentContainer, false);
            var row = CreatePlayerRowBase(player, parentContainer, isYourTeam);
            if(row == null) return null;
            AddNormalModeStats(row, player);
            return row;
        }

        public VisualElement CreateEmptyRow(VisualElement parentContainer, bool isTagMode) {
            if(!EnsureTemplateAssigned()) return null;
            var row = _template.CloneTree();
            var rowRoot = row.Q<VisualElement>("scoreboard-row-root") ?? row;
            if(!TryGetRequiredRowElements(row, out var pingLabel, out _, out _, out var nameLabel, out var statLabels))
                return null;
            row.userData = statLabels;
            rowRoot.AddToClassList("player-row-empty");
            parentContainer.Add(row);
            if(pingLabel != null) pingLabel.text = "-";
            if(nameLabel != null) nameLabel.text = "-";
            foreach(var t in statLabels) t.text = "-";
            if(isTagMode) {
                statLabels[4].style.display = DisplayStyle.None;
                statLabels[5].style.display = DisplayStyle.None;
            } else {
                statLabels[4].style.display = DisplayStyle.Flex;
                statLabels[5].style.display = DisplayStyle.Flex;
            }

            return row;
        }

        private VisualElement CreatePlayerRowBase(PlayerController player, VisualElement parentContainer,
            bool isYourTeam = false) {
            if(!EnsureTemplateAssigned()) return null;
            var row = _template.CloneTree();
            var rowRoot = row.Q<VisualElement>("scoreboard-row-root") ?? row;
            if(!TryGetRequiredRowElements(row, out var pingLabel, out var avatar, out var speakingIndicator,
                   out var nameLabel, out var statLabels)) return null;
            row.userData = statLabels;
            if(player.IsOwner) {
                rowRoot.AddToClassList("player-row-local");
                if(isYourTeam) rowRoot.AddToClassList("player-row-local-your-team");
            }

            parentContainer.Add(row);
            if(pingLabel != null) {
                pingLabel.text = GetPingText(player);
                var pingClass = GetPingColorClass(player);
                if(!string.IsNullOrEmpty(pingClass)) pingLabel.AddToClassList(pingClass);
            }

            if(avatar != null) {
                ApplyFallbackAvatar(player, avatar);
                if(player != null && player.SteamId.Value != 0) LoadSteamAvatar(player.SteamId.Value, avatar).Forget();
            }

            if(speakingIndicator != null && player != null)
                _speakingIndicators[player.OwnerClientId] = speakingIndicator;
            if(nameLabel != null) nameLabel.text = player.PlayerName.Value.ToString();
            rowRoot.RegisterCallback<PointerDownEvent>(evt => {
                if(evt.button != 1 || player == null || player.IsOwner ||
                   InGameContextMenuManager.Instance == null) return;
                InGameContextMenuManager.Instance.Show(player.SteamId.Value, evt.position);
            });
            return row;
        }

        private void AddNormalModeStats(VisualElement row, PlayerController player) {
            var statLabels = GetRowStatLabels(row);
            if(statLabels == null) return;
            foreach(var t in statLabels) t.style.display = DisplayStyle.Flex;
            statLabels[0].text = player.Kills.Value.ToString();
            statLabels[1].text = player.Deaths.Value.ToString();
            statLabels[2].text = player.Assists.Value.ToString();
            var kda = ScoreboardPlayerData.CalculateKdr(player.Kills.Value, player.Deaths.Value, player.Assists.Value);
            statLabels[3].text = kda.ToString("F2");
            if(kda >= 2.0f) statLabels[3].AddToClassList("player-stat-highlight");
            else statLabels[3].RemoveFromClassList("player-stat-highlight");
            statLabels[4].text = $"{Mathf.RoundToInt(player.DamageDealt.Value):N0}";
            statLabels[5].text = "0%";
            statLabels[6].text = _playerData.GetAverageVelocityText(player);
        }

        private Label[] GetRowStatLabels(VisualElement row) {
            if(row?.userData is Label[] { Length: 7 } cached) return cached;
            if(!TryGetRequiredRowElements(row, out _, out _, out _, out _, out var statLabels)) return null;
            if(row != null) row.userData = statLabels;
            return statLabels;
        }

        private bool TryGetRequiredRowElements(VisualElement row, out Label pingLabel, out VisualElement avatar,
            out VisualElement speakingIndicator, out Label nameLabel, out Label[] statLabels) {
            pingLabel = row?.Q<Label>("ping-label");
            avatar = row?.Q<VisualElement>("avatar");
            speakingIndicator = avatar?.Q<VisualElement>("speaking-indicator");
            nameLabel = row?.Q<Label>("name-label");
            statLabels = new Label[7];
            var missing = row == null || pingLabel == null || avatar == null || speakingIndicator == null ||
                          nameLabel == null;
            for(var i = 0; i < 7; i++) {
                statLabels[i] = row?.Q<Label>($"stat-{i}");
                if(statLabels[i] == null) missing = true;
            }

            if(!missing) return true;
            if(_invalidTemplateLogged) return false;
            _invalidTemplateLogged = true;
            Debug.LogError(
                "[ScoreboardManager] `scoreboardRowTemplate` is missing required elements. Expected: `ping-label`, `avatar`, `speaking-indicator`, `name-label`, and `stat-0` through `stat-6`.",
                _logContext);
            return false;
        }

        private static string GetPingText(PlayerController player) {
            var ping = player != null ? player.PingMs : 0;
            return $"{ping}ms";
        }

        private static string GetPingColorClass(PlayerController player) {
            var ping = player != null ? player.PingMs : 0;
            return ping switch { > 100 => "player-ping-critical", > 50 => "player-ping-high", _ => "" };
        }

        private Sprite GetPlayerIconSprite(Color baseColor) {
            if(_iconSprites == null || _iconSprites.Length == 0) return null;
            var idx = GetClosestIconIndex(baseColor);
            return _iconSprites[Mathf.Clamp(idx, 0, _iconSprites.Length - 1)];
        }

        private int GetClosestIconIndex(Color baseColor) {
            if(_iconSprites == null || _iconSprites.Length == 0) return 0;
            var palette = new[] {
                new Color(1f, 1f, 1f), new Color(1f, 0f, 0f), new Color(1f, 0.5f, 0f), new Color(1f, 1f, 0f),
                new Color(0f, 1f, 0f), new Color(0f, 0f, 1f), new Color(0.5f, 0f, 1f)
            };
            var bestIndex = 0;
            var bestDist = float.MaxValue;
            for(var i = 0; i < palette.Length; i++) {
                var d = palette[i] - baseColor;
                var distSq = d.r * d.r + d.g * d.g + d.b * d.b;
                if(distSq >= bestDist) continue;
                bestDist = distSq;
                bestIndex = i;
            }

            return bestIndex;
        }

        private void ApplyFallbackAvatar(PlayerController player, VisualElement avatarElement) {
            if(avatarElement == null) return;
            var baseColor = player != null ? player.CurrentBaseColor : Color.white;
            var icon = GetPlayerIconSprite(baseColor);
            if(icon != null) avatarElement.style.backgroundImage = new StyleBackground(icon);
        }

        private static async UniTaskVoid LoadSteamAvatar(ulong steamId, VisualElement avatarElement) {
            if(!Steamworks.SteamClient.IsValid || !Steamworks.SteamClient.IsLoggedOn) return;
            if(SteamManager.Instance == null) return;
            try {
                var texture = await SteamManager.Instance.GetAvatarAsync(steamId);
                if(texture != null && avatarElement != null)
                    avatarElement.style.backgroundImage = new StyleBackground(texture);
            } catch(Exception ex) {
                Debug.LogWarning($"[ScoreboardManager] Steam avatar fetch failed for {steamId}: {ex.Message}");
            }
        }
    }
}