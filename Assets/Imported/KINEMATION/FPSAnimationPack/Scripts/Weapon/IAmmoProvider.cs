namespace KINEMATION.FPSAnimationPack.Scripts.Weapon
{
    public interface IAmmoProvider
    {
        public int GetActiveAmmo();
        public int GetMaxAmmo();
    }
}