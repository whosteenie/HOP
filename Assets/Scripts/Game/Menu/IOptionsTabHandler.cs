using Game.Settings;

namespace Game.Menu {
    /// <summary>
    /// Interface for options tab handlers.
    /// </summary>
    public interface IOptionsTabHandler {
        void FindElements(IOptionsTabContext ctx);
        void SetupCallbacks(IOptionsTabContext ctx);
        void Load(SettingsData data);
        void Save(SettingsData data);
        void StoreOriginal();
        bool HasUnsavedChanges();
        void RefreshDisplay();
        /// <summary>
        /// Apply settings to actual runtime (audio mixer, URP, etc.). Only audio and video tabs need this.
        /// </summary>
        void ApplyToRuntime();
    }
}
