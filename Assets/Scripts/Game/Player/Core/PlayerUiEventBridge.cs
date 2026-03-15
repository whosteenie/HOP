using Events;

namespace Game.Player.Core {
    internal static class PlayerUiEventBridge {
        public static void PublishShowHud() {
            EventBus.Publish(new ShowHUDEvent());
        }

        public static void PublishLocalPlayerReady(PlayerController player) {
            EventBus.Publish(new LocalPlayerReadyEvent(player));
        }

        public static void PublishTagStatus(bool isTagged) {
            EventBus.Publish(new UpdateTagStatusEvent(isTagged));
        }

        public static void PublishHealthUpdated(float health, float maxHealth) {
            EventBus.Publish(new UpdateHealthEvent(health, maxHealth));
        }

        public static void PublishWeaponHudRefresh(int ammo, int magSize, float health, float multiplier, float maxMultiplier) {
            EventBus.Publish(new UpdateAmmoEvent(ammo, magSize));
            EventBus.Publish(new UpdateHealthEvent(health, 100f));
            EventBus.Publish(new UpdateMultiplierEvent(multiplier, maxMultiplier));
        }
    }
}
