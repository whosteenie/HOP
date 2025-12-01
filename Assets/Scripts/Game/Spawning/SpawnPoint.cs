using UnityEngine;

namespace Game.Spawning {
    public class SpawnPoint : MonoBehaviour {
    public enum Team {
        TeamA,
        TeamB
    }

    [Header("Team")]
    [SerializeField] private Team team = Team.TeamA;

    public Team AssignedTeam => team;

    private void OnDrawGizmos() {
        Gizmos.color = Color.green;
        var position = transform.position;
        Gizmos.DrawWireSphere(position, 0.5f);
        Gizmos.DrawLine(position, position + transform.forward * 2f);
    }
    }
}