using Cysharp.Threading.Tasks;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;

namespace Network.Relay {
    /// <summary>
    /// Unity Relay connector implementation. Converts Task-based APIs to UniTask for consistency.
    /// </summary>
    public sealed class RelayConnector : IRelayConnector {
        /// <inheritdoc />
        public async UniTask<(Allocation alloc, string joinCode)> CreateAllocationAsync(int maxPlayers) {
            var alloc = await RelayService.Instance.CreateAllocationAsync(maxPlayers);
            var code = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            return (alloc, code);
        }

        /// <inheritdoc />
        public async UniTask<JoinAllocation> JoinAllocationAsync(string joinCode) {
            return await RelayService.Instance.JoinAllocationAsync(joinCode);
        }
    }
}