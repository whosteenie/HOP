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
            return Bind(dropdown, null);
        }

        public static Action Bind(DropdownField dropdown, string popupOpenClass) {
            if(dropdown == null) {
                return null;
            }

            VisualElement panelRoot = dropdown.panel?.visualTree;
            var popupWatchGeneration = 0;
            VisualElement taggedPopupRoot = null;

            void ClearTaggedPopupClass() {
                if(string.IsNullOrWhiteSpace(popupOpenClass)) return;
                if(taggedPopupRoot == null) return;
                taggedPopupRoot.RemoveFromClassList(popupOpenClass);
                taggedPopupRoot = null;
            }

            void TryTagCurrentPopupRoot(VisualElement currentPanelRoot) {
                if(string.IsNullOrWhiteSpace(popupOpenClass)) return;
                var popupRoot = FindAnyDropdownPopupRoot(currentPanelRoot);
                if(popupRoot == null) return;

                if(taggedPopupRoot != null && taggedPopupRoot != popupRoot) {
                    taggedPopupRoot.RemoveFromClassList(popupOpenClass);
                }

                taggedPopupRoot = popupRoot;
                if(!taggedPopupRoot.ClassListContains(popupOpenClass)) {
                    taggedPopupRoot.AddToClassList(popupOpenClass);
                }
            }

            void CloseDropdown(string reason) {
                popupWatchGeneration++;
                if(!dropdown.ClassListContains(OpenClass)) {
                    ClearTaggedPopupClass();
                    return;
                }

                dropdown.RemoveFromClassList(OpenClass);
                ClearTaggedPopupClass();
            }

            void StartPopupWatchdog() {
                popupWatchGeneration++;
                var generation = popupWatchGeneration;
                var sawPopupOpen = false;
                var ticks = 0;

                void Tick() {
                    if(dropdown == null || generation != popupWatchGeneration) {
                        return;
                    }

                    if(!dropdown.ClassListContains(OpenClass)) {
                        return;
                    }

                    var currentPanelRoot = dropdown.panel?.visualTree ?? panelRoot;
                    var popupOpen = IsAnyDropdownPopupOpen(currentPanelRoot);
                    if(popupOpen) {
                        sawPopupOpen = true;
                        TryTagCurrentPopupRoot(currentPanelRoot);
                    } else if(sawPopupOpen) {
                        CloseDropdown("Popup watchdog detected closed popup");
                        return;
                    }

                    // Safety timeout: if popup never appears, clear the state.
                    if(!sawPopupOpen && ticks >= 20) {
                        CloseDropdown("Popup watchdog timeout");
                        return;
                    }

                    ticks++;
                    dropdown.schedule.Execute(Tick).StartingIn(50);
                }

                dropdown.schedule.Execute(Tick).StartingIn(50);
            }

            EventCallback<PointerDownEvent> onPointerDown = _ => {
                dropdown.AddToClassList(OpenClass);
                StartPopupWatchdog();
            };

            EventCallback<ChangeEvent<string>> onValueChanged = _ => {
                CloseDropdown("ChangeEvent<string>");
            };

            EventCallback<DetachFromPanelEvent> onDetach = _ => {
                CloseDropdown("DetachFromPanelEvent");
            };

            EventCallback<PointerDownEvent> onRootPointerDown = evt => {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                var target = evt.target as VisualElement;

                // Clicking the dropdown field while open should toggle-close immediately.
                if(IsInsideElement(target, dropdown)) {
                    CloseDropdown("Root PointerDown inside field");
                    return;
                }

                // Clicking any popup option row should close immediately, even if the value
                // doesn't change (re-selecting the currently selected option).
                if(IsInsidePopupItem(target)) {
                    CloseDropdown("Root PointerDown inside popup item");
                    return;
                }

                if(IsInsideDropdownOrPopup(evt.target as VisualElement, dropdown)) {
                    return;
                }

                // Clicking anywhere else should close immediately on pointer-down.
                CloseDropdown("Root PointerDown outside dropdown/popup");
            };

            EventCallback<KeyDownEvent> onRootKeyDown = evt => {
                if(evt.keyCode == UnityEngine.KeyCode.Escape) {
                    CloseDropdown("Root KeyDown Escape");
                }
            };

            EventCallback<PointerUpEvent> onRootPointerUp = _ => {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                ScheduleCloseIfPopupGone(dropdown, panelRoot);
            };

            EventCallback<AttachToPanelEvent> onAttach = _ => {
                if(panelRoot != null) {
                    return;
                }

                panelRoot = dropdown.panel?.visualTree;
                panelRoot?.RegisterCallback(onRootPointerDown);
                panelRoot?.RegisterCallback(onRootKeyDown);
                panelRoot?.RegisterCallback(onRootPointerUp);
            };

            dropdown.RegisterCallback(onPointerDown);
            dropdown.RegisterCallback(onValueChanged);
            dropdown.RegisterCallback(onDetach);
            dropdown.RegisterCallback(onAttach);
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
                dropdown.UnregisterCallback(onAttach);
                panelRoot?.UnregisterCallback(onRootPointerDown);
                panelRoot?.UnregisterCallback(onRootKeyDown);
                panelRoot?.UnregisterCallback(onRootPointerUp);
                CloseDropdown("Cleanup");
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

                // Treat only concrete popup content/shell as "inside".
                // Do not treat the broad full-screen popup host root as inside.
                if(current.ClassListContains("unity-base-dropdown__container-outer") ||
                   current.ClassListContains("unity-base-dropdown__container-inner") ||
                   current.ClassListContains("unity-base-dropdown__item") ||
                   current.ClassListContains("unity-base-dropdown__item-content") ||
                   current.ClassListContains("unity-base-dropdown__label") ||
                   current.ClassListContains("unity-base-dropdown__checkmark") ||
                   current.ClassListContains("unity-base-popup-field__menu-item")) {
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

        private static VisualElement FindAnyDropdownPopupRoot(VisualElement panelRoot) {
            if(panelRoot == null) return null;

            var popupRoot = panelRoot.Query<VisualElement>(className: "unity-base-dropdown").First();
            if(popupRoot != null) return popupRoot;

            popupRoot = panelRoot.Query<VisualElement>(className: "unity-base-popup-field__menu").First();
            if(popupRoot != null) return popupRoot;

            popupRoot = panelRoot.Query<VisualElement>(className: "unity-popup-field__menu").First();
            if(popupRoot != null) return popupRoot;

            return panelRoot.Query<VisualElement>(className: "unity-generic-dropdown-menu").First();
        }

        private static bool IsInsideElement(VisualElement target, VisualElement root) {
            if(target == null || root == null) {
                return false;
            }

            for(var current = target; current != null; current = current.parent) {
                if(current == root) {
                    return true;
                }
            }

            return false;
        }

        private static bool IsInsidePopupItem(VisualElement target) {
            if(target == null) {
                return false;
            }

            for(var current = target; current != null; current = current.parent) {
                if(current.ClassListContains("unity-base-dropdown__item") ||
                   current.ClassListContains("unity-base-popup-field__menu-item") ||
                   current.ClassListContains("unity-collection-view__item") ||
                   current.ClassListContains("unity-list-view__item")) {
                    return true;
                }
            }

            return false;
        }

        private static void ScheduleCloseIfPopupGone(DropdownField dropdown, VisualElement panelRoot) {
            if(dropdown == null) {
                return;
            }

            void TryClose() {
                if(dropdown == null || !dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                if(!IsAnyDropdownPopupOpen(panelRoot)) {
                    dropdown.RemoveFromClassList(OpenClass);
                }
            }

            // Handles cases where no ChangeEvent fires (re-selecting same value) and popup closes a frame later.
            dropdown.schedule.Execute(TryClose);
            dropdown.schedule.Execute(TryClose).StartingIn(16);
            dropdown.schedule.Execute(TryClose).StartingIn(48);
        }
    }
}
