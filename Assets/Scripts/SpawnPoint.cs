using UnityEngine;

public class SpawnPoint : MonoBehaviour {
    public enum Team {
        TeamA,
        TeamB
    }
    
    [Header("Team")] [SerializeField] private Team team = Team.TeamA;
    
    public Team AssignedTeam => team;

    private void OnDrawGizmos() {
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(transform.position, 0.5f);
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 2f);
    }
}