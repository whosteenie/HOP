namespace Game.Audio {
    public enum SfxKey : byte {
    Walk = 0,
    Run = 1,
    Jump = 2,
    Land = 3,
    Reload = 4,
    Dry = 5,
    Shoot = 6,
    JumpPad = 7,
    Grapple = 8,
    BulletTrail = 9,
    HopballSpawn = 10,

    // UI Sounds
    ButtonClick = 11,
    ButtonHover = 12,
    BackButton = 13,
    TimerTick = 14,
    Hit = 15,
    Kill = 16,
    Hurt = 17,
    Tagged = 18,
    Tagging = 19,

    // Weapon-specific shoots
    ShootPistol = 20,
    ShootDeagle = 21,
    ShootSmg = 22,
    ShootRifle = 23,
    ShootShotgun = 24,
    ShootSniper = 25,

    // Weapon-specific reloads
    ReloadPistol = 26,
    ReloadDeagle = 27,
    ReloadSmg = 28,
    ReloadRifle = 29,
    ReloadShotgun = 30,
    ReloadSniper = 31,
    SniperZoom = 32,

    // New additions appended to avoid shifting legacy values
    BulletImpact = 33,
    WeaponSwitch = 34
    }
}