using Cysharp.Threading.Tasks;
using Unity.Services.Relay.Models;

namespace Network.Relay {
    /// <summary>
    /// Minimal abstraction over Unity Relay allocation and join APIs.
    /// Returns both allocation and the generated join code for hosts.
    /// </summary>
    public interface IRelayConnector {
        /// <summary>Create a host allocation and its join code.</summary>
        UniTask<(Allocation alloc, string joinCode)> CreateAllocationAsync(int maxPlayers);
        /// <summary>Join an existing allocation by code (client path).</summary>
        UniTask<JoinAllocation> JoinAllocationAsync(string joinCode);
    }
}