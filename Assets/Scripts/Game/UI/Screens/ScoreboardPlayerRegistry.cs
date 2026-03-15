using System;
using System.Collections.Generic;
using Game.Match;
using Game.Player.Core;

namespace Game.UI.Screens {
    /// <summary>Registry of players on the scoreboard and profile/state subscriptions; invokes refresh callback on changes.</summary>
    internal sealed class ScoreboardPlayerRegistry {
        private readonly HashSet<PlayerController> _players = new();
        private readonly Dictionary<ulong, MatchPlayerStateProxy> _boundProfileStates = new();
        private readonly Action _onRefreshRequested;

        public ScoreboardPlayerRegistry(Action onRefreshRequested) {
            _onRefreshRequested = onRefreshRequested ?? (() => { });
        }

        public void Register(PlayerController player) {
            if(player == null || !_players.Add(player)) return;
            player.playerBaseColor.OnValueChanged += OnPlayerProfileChanged;
            RebindProfileSubscriptions(player);
            _onRefreshRequested();
        }

        public void Unregister(PlayerController player) {
            if(player == null || !_players.Contains(player)) return;
            player.playerBaseColor.OnValueChanged -= OnPlayerProfileChanged;
            UnbindProfileSubscriptions(player.OwnerClientId);
            _players.Remove(player);
            _onRefreshRequested();
        }

        public void OnStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            var player = Find(playerClientId);
            if(player == null) return;
            RebindProfileSubscriptions(player);
            ForceRefresh();
        }

        public void OnStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            UnbindProfileSubscriptions(playerClientId, proxy);
            ForceRefresh();
        }

        private PlayerController Find(ulong clientId) {
            foreach(var p in _players) {
                if(p != null && p.OwnerClientId == clientId) return p;
            }

            return null;
        }

        public IReadOnlyCollection<PlayerController> GetAllPlayers() {
            _players.RemoveWhere(p => p == null);
            return _players;
        }

        public void Clear() {
            foreach(var player in _players) {
                if(player == null) continue;
                player.playerBaseColor.OnValueChanged -= OnPlayerProfileChanged;
            }
            ClearProfileSubscriptions();
            _players.Clear();
        }

        /// <summary>Clears all profile state subscriptions.</summary>
        private void ClearProfileSubscriptions() {
            foreach(var entry in _boundProfileStates) {
                if(entry.Value == null) continue;
                entry.Value.playerName.OnValueChanged -= OnPlayerProfileChanged;
                entry.Value.steamId.OnValueChanged -= OnPlayerProfileChanged;
            }

            _boundProfileStates.Clear();
        }

        private void ForceRefresh() {
            _onRefreshRequested();
        }

        /// <summary>Rebinds profile state subscriptions for the given player.</summary>
        private void RebindProfileSubscriptions(PlayerController player) {
            if(player == null) return;
            UnbindProfileSubscriptions(player.OwnerClientId);
            var playerState = player.PlayerState;
            if(playerState == null) return;
            playerState.playerName.OnValueChanged -= OnPlayerProfileChanged;
            playerState.playerName.OnValueChanged += OnPlayerProfileChanged;
            playerState.steamId.OnValueChanged -= OnPlayerProfileChanged;
            playerState.steamId.OnValueChanged += OnPlayerProfileChanged;
            _boundProfileStates[player.OwnerClientId] = playerState;
        }

        /// <summary>Unbinds profile state subscriptions for the given client.</summary>
        private void UnbindProfileSubscriptions(ulong clientId, MatchPlayerStateProxy expectedState = null) {
            if(!_boundProfileStates.TryGetValue(clientId, out var bound) || bound == null) return;
            if(expectedState != null && bound != expectedState) return;
            bound.playerName.OnValueChanged -= OnPlayerProfileChanged;
            bound.steamId.OnValueChanged -= OnPlayerProfileChanged;
            _boundProfileStates.Remove(clientId);
        }

        private void OnPlayerProfileChanged(Unity.Collections.FixedString64Bytes oldV,
            Unity.Collections.FixedString64Bytes newV) {
            ForceRefresh();
        }

        private void OnPlayerProfileChanged(ulong oldV, ulong newV) {
            ForceRefresh();
        }

        private void OnPlayerProfileChanged<T>(T oldV, T newV) {
            ForceRefresh();
        }
    }
}