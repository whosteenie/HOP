using UnityEngine;

public interface IDamageable
{
    void TakeDamage(float damageAmount, Vector3? hitPoint = null, Vector3? hitNormal = null);
    bool IsDead { get; }
}
