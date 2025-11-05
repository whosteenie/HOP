using Unity.Netcode;
using UnityEngine;

public class CustomNetworkManager : MonoBehaviour
{
    private void Start()
    {
        NetworkManager.Singleton.OnServerStarted += OnServerStarted;
    }
    
    private void OnServerStarted()
    {
        NetworkManager.Singleton.ConnectionApprovalCallback += ApprovalCheck;
    }
    
    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, 
        NetworkManager.ConnectionApprovalResponse response)
    {
        response.Approved = true;
        response.CreatePlayerObject = true;
        
        // Set spawn position from SpawnManager
        if(SpawnManager.Instance != null)
        {
            response.Position = SpawnManager.Instance.GetNextSpawnPosition();
            response.Rotation = SpawnManager.Instance.GetNextSpawnRotation();
        }
        else
        {
            // Fallback
            response.Position = new Vector3(0, 5, 0);
            response.Rotation = Quaternion.identity;
        }
    }
}