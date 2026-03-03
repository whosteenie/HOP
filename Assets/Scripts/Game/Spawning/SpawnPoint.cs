using UnityEngine;
using System.Collections.Generic;

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

    private static readonly HashSet<SpawnPoint> InstancesSet = new();
    public static IReadOnlyCollection<SpawnPoint> Instances => InstancesSet;

    private void OnEnable() {
        InstancesSet.Add(this);
    }

    private void OnDisable() {
        InstancesSet.Remove(this);
    }

    private void OnDrawGizmos() {
        Gizmos.color = Color.green;
        var position = transform.position;
        Gizmos.DrawWireSphere(position, radius);
        Gizmos.DrawLine(position, position + transform.forward * 2f * radius);
    }
    }
}
