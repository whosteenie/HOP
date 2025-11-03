using UnityEngine;

[CreateAssetMenu(fileName = "New Weapon", menuName = "Weapon Data")]
public class WeaponData : ScriptableObject {
    [Header("Identity")]
    public new string name;
    
    [Header("Ammo")]
    public int magSize;
    public int currentAmmo;
    
    [Header("Damage")]
    public int baseDamage;
    public float maxDamageMultiplier;
    public float damageCap;

    [Header("Fire Properties")]
    public float fireRate;
    public string fireMode;
    public float bulletSpread;
    
    [Header("Reload")]
    public float reloadTime;

    [Header("Audio")]
    public AudioClip[] fireSounds;
    public AudioClip[] dryFireSounds;
    public AudioClip[] reloadSounds;
    
    [Header("Visuals")]
    public TrailRenderer bulletTrail;
}
