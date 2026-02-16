using UnityEngine;

namespace Game.Spawning {
    public class SpawnPoint : MonoBehaviour {
    public enum Team {
        TeamA,
        TeamB,
        None
    }

    [Range(0.5f, 5f)]
    [SerializeField] private float radius = 0.5f;

    [Header("Team")]
    [SerializeField] private Team team = Team.TeamA;

    public Team AssignedTeam => team;

    private void OnDrawGizmos() {
        Gizmos.color = Color.green;
        var position = transform.position;
        Gizmos.DrawWireSphere(position, radius);
        Gizmos.DrawLine(position, position + transform.forward * 2f * radius);
    }
    }
}