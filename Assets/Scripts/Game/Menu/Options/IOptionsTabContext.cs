using System;
using UnityEngine.UIElements;

namespace Game.Menu.Options {
    /// <summary>
    /// Interface for tab handlers to access UI (element queries, cleanup registration, root).
    /// </summary>
    public interface IOptionsTabContext {
        T QOptional<T>(string name) where T : VisualElement;
        void RegisterCleanup(Action a);
        VisualElement Root { get; }
    }
}
