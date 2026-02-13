using System;
using UnityEngine.UIElements;

namespace Game.UI {
    /// <summary>
    /// Reusable UITK helper that mirrors dropdown open/close into a USS class.
    /// This avoids unsupported pseudo-classes such as :focus-within in Unity USS.
    /// </summary>
    public static class DropdownOpenStateBinder {
        private const string OpenClass = "dropdown-open";

        public static Action Bind(DropdownField dropdown) {
            if(dropdown == null) {
                return null;
            }

            VisualElement panelRoot = dropdown.panel?.visualTree;

            EventCallback<PointerDownEvent> onPointerDown = _ => {
                dropdown.AddToClassList(OpenClass);
            };

            EventCallback<ChangeEvent<string>> onValueChanged = _ => {
                dropdown.RemoveFromClassList(OpenClass);
            };

            EventCallback<DetachFromPanelEvent> onDetach = _ => {
                dropdown.RemoveFromClassList(OpenClass);
            };

            EventCallback<PointerDownEvent> onRootPointerDown = evt => {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                if(IsInsideDropdownOrPopup(evt.target as VisualElement, dropdown)) {
                    return;
                }

                dropdown.RemoveFromClassList(OpenClass);
            };

            EventCallback<KeyDownEvent> onRootKeyDown = evt => {
                if(evt.keyCode == UnityEngine.KeyCode.Escape) {
                    dropdown.RemoveFromClassList(OpenClass);
                }
            };

            EventCallback<PointerUpEvent> onRootPointerUp = _ => {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                // Wait until UITK finishes processing popup open/close for this click.
                dropdown.schedule.Execute(() => {
                    if(!IsAnyDropdownPopupOpen(panelRoot)) {
                        dropdown.RemoveFromClassList(OpenClass);
                    }
                });
            };

            dropdown.RegisterCallback(onPointerDown);
            dropdown.RegisterCallback(onValueChanged);
            dropdown.RegisterCallback(onDetach);
            panelRoot?.RegisterCallback(onRootPointerDown);
            panelRoot?.RegisterCallback(onRootKeyDown);
            panelRoot?.RegisterCallback(onRootPointerUp);

            return () => {
                if(dropdown == null) {
                    return;
                }

                dropdown.UnregisterCallback(onPointerDown);
                dropdown.UnregisterCallback(onValueChanged);
                dropdown.UnregisterCallback(onDetach);
                panelRoot?.UnregisterCallback(onRootPointerDown);
                panelRoot?.UnregisterCallback(onRootKeyDown);
                panelRoot?.UnregisterCallback(onRootPointerUp);
                dropdown.RemoveFromClassList(OpenClass);
            };
        }

        private static bool IsInsideDropdownOrPopup(VisualElement target, DropdownField dropdown) {
            if(target == null) {
                return false;
            }

            for(VisualElement current = target; current != null; current = current.parent) {
                if(current == dropdown) {
                    return true;
                }

                if(current.ClassListContains("unity-base-dropdown") ||
                   current.ClassListContains("unity-base-popup-field__menu") ||
                   current.ClassListContains("unity-popup-field__menu") ||
                   current.ClassListContains("unity-generic-dropdown-menu")) {
                    return true;
                }
            }

            return false;
        }

        private static bool IsAnyDropdownPopupOpen(VisualElement panelRoot) {
            if(panelRoot == null) {
                return false;
            }

            return panelRoot.Query<VisualElement>(className: "unity-base-dropdown").First() != null ||
                   panelRoot.Query<VisualElement>(className: "unity-base-popup-field__menu").First() != null ||
                   panelRoot.Query<VisualElement>(className: "unity-popup-field__menu").First() != null ||
                   panelRoot.Query<VisualElement>(className: "unity-generic-dropdown-menu").First() != null;
        }
    }
}
