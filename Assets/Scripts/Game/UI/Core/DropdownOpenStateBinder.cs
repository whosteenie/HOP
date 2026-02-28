using System;
using UnityEngine.UIElements;

namespace Game.UI {
    /// <summary>
    /// Reusable UITK helper that mirrors dropdown open/close into a USS class.
    /// This avoids unsupported pseudo-classes such as :focus-within in Unity USS.
    /// </summary>
    public static class DropdownOpenStateBinder {
        private const string OpenClass = "dropdown-open";

        public static Action Bind(DropdownField dropdown, string popupOpenClass = null) {
            if(dropdown == null) {
                return null;
            }

            var panelRoot = dropdown.panel?.visualTree;
            var popupWatchGeneration = 0;
            VisualElement taggedPopupRoot = null;

            EventCallback<PointerDownEvent> onPointerDown = _ => {
                SetPanelRootScopedClass(true);
                dropdown.AddToClassList(OpenClass);
                StartPopupWatchdog();
            };

            EventCallback<ChangeEvent<string>> onValueChanged = _ => {
                CloseDropdown();
            };

            EventCallback<DetachFromPanelEvent> onDetach = _ => {
                CloseDropdown();
            };

            EventCallback<PointerDownEvent> onRootPointerDown = evt => {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                var target = evt.target as VisualElement;

                // Clicking the dropdown field while open should toggle-close immediately.
                if(IsInsideElement(target, dropdown)) {
                    CloseDropdown();
                    return;
                }

                // Clicking any popup option row should close immediately, even if the value
                // doesn't change (re-selecting the currently selected option).
                if(IsInsidePopupItem(target)) {
                    CloseDropdown();
                    return;
                }

                if(IsInsideDropdownOrPopup(evt.target as VisualElement, dropdown)) {
                    return;
                }

                // Clicking anywhere else should close immediately on pointer-down.
                CloseDropdown();
            };

            EventCallback<KeyDownEvent> onRootKeyDown = evt => {
                if(evt.keyCode == UnityEngine.KeyCode.Escape) {
                    CloseDropdown();
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
                dropdown.UnregisterCallback(onPointerDown);
                dropdown.UnregisterCallback(onValueChanged);
                dropdown.UnregisterCallback(onDetach);
                dropdown.UnregisterCallback(onAttach);
                panelRoot?.UnregisterCallback(onRootPointerDown);
                panelRoot?.UnregisterCallback(onRootKeyDown);
                panelRoot?.UnregisterCallback(onRootPointerUp);
                CloseDropdown();
            };

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

            void CloseDropdown() {
                popupWatchGeneration++;
                if(!dropdown.ClassListContains(OpenClass)) {
                    SetPanelRootScopedClass(false);
                    ClearTaggedPopupClass();
                    return;
                }

                dropdown.RemoveFromClassList(OpenClass);
                SetPanelRootScopedClass(false);
                ClearTaggedPopupClass();
            }

            void StartPopupWatchdog() {
                popupWatchGeneration++;
                var generation = popupWatchGeneration;
                var sawPopupOpen = false;
                var ticks = 0;

                dropdown.schedule.Execute(Tick).StartingIn(16);
                return;

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
                        CloseDropdown();
                        return;
                    }

                    // Safety timeout: if popup never appears, clear the state.
                    if(!sawPopupOpen && ticks >= 20) {
                        CloseDropdown();
                        return;
                    }

                    ticks++;
                    dropdown.schedule.Execute(Tick).StartingIn(16);
                }
            }

            void SetPanelRootScopedClass(bool enabled) {
                if(string.IsNullOrWhiteSpace(popupOpenClass)) return;

                var currentPanelRoot = dropdown.panel?.visualTree ?? panelRoot;
                if(currentPanelRoot != null) {
                    panelRoot = currentPanelRoot;
                }

                if(panelRoot == null) return;

                if(enabled) {
                    panelRoot.AddToClassList(popupOpenClass);
                } else {
                    panelRoot.RemoveFromClassList(popupOpenClass);
                }
            }
        }

        private static bool IsInsideDropdownOrPopup(VisualElement target, DropdownField dropdown) {
            if(target == null) {
                return false;
            }

            for(var current = target; current != null; current = current.parent) {
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
            return popupRoot ?? panelRoot.Query<VisualElement>(className: "unity-generic-dropdown-menu").First();
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

            // Handles cases where no ChangeEvent fires (re-selecting same value) and popup closes a frame later.
            dropdown.schedule.Execute(TryClose);
            dropdown.schedule.Execute(TryClose).StartingIn(16);
            dropdown.schedule.Execute(TryClose).StartingIn(48);
            return;

            void TryClose() {
                if(!dropdown.ClassListContains(OpenClass)) {
                    return;
                }

                if(!IsAnyDropdownPopupOpen(panelRoot)) {
                    dropdown.RemoveFromClassList(OpenClass);
                }
            }
        }
    }
}
