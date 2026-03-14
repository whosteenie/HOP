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
            RebindProfileStateSubscriptions(player);
            _onRefreshRequested();
        }

        public void Unregister(PlayerController player) {
            if(player == null || !_players.Contains(player)) return;
            player.playerBaseColor.OnValueChanged -= OnPlayerProfileChanged;
            UnbindProfileStateSubscriptions(player.OwnerClientId);
            _players.Remove(player);
            _onRefreshRequested();
        }

        public void OnStateRegistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            var player = Find(playerClientId);
            if(player == null) return;
            RebindProfileStateSubscriptions(player);
            ForceRefresh();
        }

        public void OnStateUnregistered(ulong playerClientId, MatchPlayerStateProxy proxy) {
            UnbindProfileStateSubscriptions(playerClientId, proxy);
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
            ClearProfileStateSubscriptions();
            _players.Clear();
        }

        public void ClearProfileStateSubscriptions() {
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

        private void RebindProfileStateSubscriptions(PlayerController player) {
            if(player == null) return;
            UnbindProfileStateSubscriptions(player.OwnerClientId);
            var playerState = player.PlayerState;
            if(playerState == null) return;
            playerState.playerName.OnValueChanged -= OnPlayerProfileChanged;
            playerState.playerName.OnValueChanged += OnPlayerProfileChanged;
            playerState.steamId.OnValueChanged -= OnPlayerProfileChanged;
            playerState.steamId.OnValueChanged += OnPlayerProfileChanged;
            _boundProfileStates[player.OwnerClientId] = playerState;
        }

        private void UnbindProfileStateSubscriptions(ulong clientId, MatchPlayerStateProxy expectedState = null) {
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