using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Services.Multiplayer;

namespace Network.Core {
    /// <summary>
    /// Provides player display identity for UGS session properties (e.g., "playerName").
    /// Abstracted to keep UGS/Authentication calls out of SessionManager.
    /// </summary>
    public interface IPlayerIdentity {
        /// <summary>Builds the UGS PlayerProperty dictionary with a best-effort name (user-set name or PlayerId suffix fallback).</summary>
        UniTask<Dictionary<string, PlayerProperty>> GetPlayerPropertiesAsync(string playerNameKey);
    }
}