using System.Collections;
using System.Collections.Generic;
using Events;
using Game.Player.Core;
using Game.Social;
using Game.UI.Core;
using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Game.UI.Screens {
    public class ChatUIManager : UIElementBase {
        private VisualElement _chatContainer;
        private VisualElement _chatBackground;
        private ScrollView _chatScroll;
        private VisualElement _chatMessageList;
        private TextField _chatInput;

        private const int MaxMessages = 50; // Maximum messages to keep in history
        private const float MessageLifetime = 8f; // Seconds before message fades when chat is closed
        private const float FadeDuration = 1.5f; // Fade out duration

        private class ChatMessageElement {
            public VisualElement Element;
            public float Timestamp;
            public bool IsVisible = true;
        }

        private readonly List<ChatMessageElement> _messageElements = new();
        private Coroutine _lifetimeCheckCoroutine;
        private bool _isScoreboardVisible;

        public bool IsChatOpen { get; private set; }

        protected override void OnInitialize() {
            _chatContainer = QOptional<VisualElement>("chat-container");
            _chatBackground = QOptional<VisualElement>("chat-background");
            _chatScroll = QOptional<ScrollView>("chat-scroll");
            _chatMessageList = QOptional<VisualElement>("chat-message-list");
            _chatInput = QOptional<TextField>("chat-input");

            if(_chatInput != null) {
                // Register Submit event
                EventCallback<KeyDownEvent> keyDownHandler = OnChatInputKeyDown;
                EventCallback<ChangeEvent<string>> valueChangedHandler = OnChatInputValueChanged;
                _chatInput.RegisterCallback(keyDownHandler);
                _chatInput.RegisterCallback(valueChangedHandler);
                RegisterCleanup(() => _chatInput.UnregisterCallback(keyDownHandler));
                RegisterCleanup(() => _chatInput.UnregisterCallback(valueChangedHandler));
            }

            // Start with chat non-interactive (closed state)
            if(_chatScroll != null) {
                _chatScroll.pickingMode = PickingMode.Ignore;
            }

            EventBus.Unsubscribe<ChatMessageReceivedEvent>(OnChatMessageReceivedEvent);
            EventBus.Subscribe<ChatMessageReceivedEvent>(OnChatMessageReceivedEvent);
            EventBus.Unsubscribe<ScoreboardVisibilityChangedEvent>(OnScoreboardVisibilityChanged);
            EventBus.Subscribe<ScoreboardVisibilityChangedEvent>(OnScoreboardVisibilityChanged);
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "chat-container", typeof(VisualElement) },
                { "chat-background", typeof(VisualElement) },
                { "chat-message-list", typeof(VisualElement) },
                { "chat-input", typeof(TextField) }
            };
        }

        protected override void OnCleanup() {
            this.UnsubscribeFromEventBus();
            base.OnCleanup();
        }

        private void OnScoreboardVisibilityChanged(ScoreboardVisibilityChangedEvent evt) {
            _isScoreboardVisible = evt is { IsVisible: true };
        }

        public void ClearChatHistory() {
            // Clear message elements
            foreach(var msgElement in _messageElements) {
                msgElement.Element?.RemoveFromHierarchy();
            }

            _messageElements.Clear();

            // Clear visual message list
            if(_chatMessageList != null) {
                _chatMessageList.Clear();
            }

            // Stop lifetime check coroutine
            if(_lifetimeCheckCoroutine != null) {
                StopCoroutine(_lifetimeCheckCoroutine);
                _lifetimeCheckCoroutine = null;
            }

            // Ensure chat is closed
            if(IsChatOpen) {
                CloseChat();
            }
        }

        private void Update() {
            // Toggle Chat (New Input System)
            if(UnityEngine.InputSystem.Keyboard.current == null) return;
            if(UnityEngine.InputSystem.Keyboard.current.enterKey.wasPressedThisFrame) {
                if(IsChatOpen) {
                    SubmitChat(); // Enter to submit if open
                } else {
                    OpenChat();
                }
            }

            if(IsChatOpen && UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame) {
                CloseChat();
            }
        }

        private void OpenChat() {
            if(_chatInput == null) return;

            IsChatOpen = true;
            EventBus.Publish(new ChatOpenStateChangedEvent(true));

            // Show background and input
            if(_chatBackground != null) {
                _chatBackground.RemoveFromClassList("minimized");
            }

            _chatInput.RemoveFromClassList("minimized");
            _chatInput.RemoveFromClassList("hidden"); // Also remove hidden if it was set by UXML


            // Show scrollbar when open
            if(_chatScroll != null) {
                _chatScroll.verticalScrollerVisibility = ScrollerVisibility.Auto;
            }

            // Enable mouse interaction with scroll view
            if(_chatScroll != null) {
                _chatScroll.pickingMode = PickingMode.Position;
            }

            // Stop lifetime check
            if(_lifetimeCheckCoroutine != null) {
                StopCoroutine(_lifetimeCheckCoroutine);
                _lifetimeCheckCoroutine = null;
            }

            // Show all messages (remove fading class)
            foreach(var msgElement in _messageElements) {
                if(msgElement.Element == null) continue;
                msgElement.Element.RemoveFromClassList("fading");
                msgElement.Element.style.display = DisplayStyle.Flex;
                msgElement.IsVisible = true;
            }

            // Scroll to bottom
            StartCoroutine(ScrollToBottom());

            // Focus input field after a frame
            StartCoroutine(FocusInputField());

            // Unlock mouse
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Lock player camera
            if(PlayerController.LocalPlayer != null) {
                PlayerController.LocalPlayer.LockLook = true;
            }
        }

        private IEnumerator FocusInputField() {
            yield return null; // Wait one frame
            if(_chatInput != null) {
                _chatInput.Focus();
            }
        }

        private void CloseChat() {
            if(_chatInput == null) return;

            IsChatOpen = false;
            EventBus.Publish(new ChatOpenStateChangedEvent(false));
            _chatInput.value = ""; // Clear input
            _chatInput.AddToClassList("minimized");

            // Instantly hide any expired messages upon closing
            var currentTime = Time.time;
            foreach(var msgElement in _messageElements) {
                if(msgElement.Element == null) continue;
                if(!(currentTime - msgElement.Timestamp >= MessageLifetime)) continue;
                msgElement.Element.style.display = DisplayStyle.None;
                msgElement.IsVisible = false;
            }


            // Hide background (minimize)
            if(_chatBackground != null) {
                _chatBackground.AddToClassList("minimized");
            }

            // Hide scrollbar when closed
            if(_chatScroll != null) {
                _chatScroll.verticalScrollerVisibility = ScrollerVisibility.Hidden;
            }

            // Disable mouse interaction with scroll view
            if(_chatScroll != null) {
                _chatScroll.pickingMode = PickingMode.Ignore;
            }

            // Start lifetime-based visibility check
            StartLifetimeCheck();

            // Ensure we are scrolled to the bottom so the newest messages are visible
            StartCoroutine(ScrollToBottom());

            // Only re-lock if scoreboard isn't also visible
            if(_isScoreboardVisible) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;

            // Unlock player camera
            if(PlayerController.LocalPlayer != null) {
                PlayerController.LocalPlayer.LockLook = false;
            }
        }

        private void SubmitChat() {
            if(_chatInput == null) return;

            var message = ChatManager.ClampToUtf8ByteLimit(_chatInput.value, ChatManager.MaxChatInputBytes);
            if(!string.IsNullOrWhiteSpace(message)) {
                // Send message and keep chat open
                if(ChatManager.Instance != null) {
                    ChatManager.Instance.SendChatMessage(message);
                }

                // Clear input but keep chat open
                _chatInput.value = "";

                // Refocus input field
                StartCoroutine(FocusInputField());
            } else {
                // Empty input - close chat
                CloseChat();
            }
        }

        private static void OnChatInputKeyDown(KeyDownEvent evt) {
            if(evt.keyCode == KeyCode.Return) {
                // Handled in Update/SubmitChat, but prevent default newline
                evt.StopPropagation();
            }
        }

        private void OnChatInputValueChanged(ChangeEvent<string> evt) {
            if(_chatInput == null) return;
            var clamped = ChatManager.ClampToUtf8ByteLimit(evt.newValue, ChatManager.MaxChatInputBytes);
            if(string.Equals(clamped, evt.newValue, System.StringComparison.Ordinal)) return;
            _chatInput.SetValueWithoutNotify(clamped);
        }

        private void OnChatMessageReceivedEvent(ChatMessageReceivedEvent evt) {
            if(evt == null) return;

            var msg = new ChatMessage {
                SenderSteamId = evt.SenderSteamId,
                SenderName = evt.SenderName,
                MessageContent = evt.MessageContent,
                IsSystemMessage = evt.IsSystemMessage
            };

            AddMessage(msg);
        }

        private void AddMessage(ChatMessage msg) {
            if(_chatMessageList == null) return;

            var row = CreateMessageRow(msg);
            var msgElement = new ChatMessageElement {
                Element = row,
                Timestamp = Time.time,
                IsVisible = true
            };

            _messageElements.Add(msgElement);
            _chatMessageList.Add(row);

            // Limit message history
            if(_messageElements.Count > MaxMessages) {
                var oldest = _messageElements[0];
                _messageElements.RemoveAt(0);
                oldest.Element?.RemoveFromHierarchy();
            }

            // Always scroll to bottom to see the newest message
            StartCoroutine(ScrollToBottom());

            // If chat is closed, ensure lifetime check is running
            if(!IsChatOpen && _lifetimeCheckCoroutine == null) {
                StartLifetimeCheck();
            }
        }

        private static VisualElement CreateMessageRow(ChatMessage msg) {
            var row = new VisualElement();
            row.AddToClassList("chat-message-row");

            if(msg.IsSystemMessage) {
                var label = new Label(ChatManager.InsertSoftWrapBreaks(msg.MessageContent));
                label.AddToClassList("chat-message");
                label.AddToClassList("chat-message-system");
                row.Add(label);
            } else {
                // Click handler for context menu (Right-click)
                row.RegisterCallback<PointerDownEvent>(evt => {
                    if(evt.button != 1) return; // Right-click only
                    if(msg.SenderSteamId == 0) return;

                    // Don't show for self
                    if(Steamworks.SteamClient.SteamId == msg.SenderSteamId) return;

                    if(InGameContextMenuManager.Instance != null) {
                        InGameContextMenuManager.Instance.Show(msg.SenderSteamId, evt.position);
                    }
                });

                var safeName = EscapeRichText(msg.SenderName);
                var safeContent = EscapeRichText(ChatManager.InsertSoftWrapBreaks(msg.MessageContent));
                var textLabel = new Label {
                    enableRichText = true,
                    text = $"<b>{safeName}:</b> {safeContent}"
                };
                textLabel.AddToClassList("chat-message");
                textLabel.AddToClassList("chat-message-content");
                row.Add(textLabel);
            }

            return row;
        }

        private static string EscapeRichText(string value) {
            return string.IsNullOrEmpty(value) ? string.Empty : value.Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private void StartLifetimeCheck() {
            if(_lifetimeCheckCoroutine != null) {
                StopCoroutine(_lifetimeCheckCoroutine);
            }

            _lifetimeCheckCoroutine = StartCoroutine(LifetimeCheckRoutine());
        }

        private IEnumerator LifetimeCheckRoutine() {
            while(!IsChatOpen) {
                var currentTime = Time.time;
                var anyChanges = false;

                foreach(var msgElement in _messageElements) {
                    if(msgElement.Element == null) continue;

                    var age = currentTime - msgElement.Timestamp;

                    // Start fading when approaching lifetime
                    if(!(age >= MessageLifetime) || !msgElement.IsVisible) continue;
                    msgElement.Element.AddToClassList("fading");
                    msgElement.IsVisible = false;
                    anyChanges = true;
                }

                // Wait for fade duration, then hide faded messages
                if(anyChanges) {
                    yield return new WaitForSeconds(FadeDuration);

                    foreach(var msgElement in _messageElements) {
                        if(msgElement.Element != null && !msgElement.IsVisible) {
                            msgElement.Element.style.display = DisplayStyle.None;
                        }
                    }
                }

                yield return new WaitForSeconds(0.5f); // Check every 0.5 seconds
            }

            _lifetimeCheckCoroutine = null;
        }

        private IEnumerator ScrollToBottom() {
            yield return null; // Wait for layout
            yield return null; // Wait another frame for layout to stabilize
            if(_chatScroll == null || _chatMessageList == null) yield break;
            // Scroll to show the bottom (newest messages)
            var contentHeight = _chatScroll.contentContainer.layout.height;
            var viewportHeight = _chatScroll.contentViewport.layout.height;
            if(contentHeight > viewportHeight) {
                var maxScroll = contentHeight - viewportHeight;
                _chatScroll.scrollOffset = new Vector2(0, maxScroll);
            } else {
                _chatScroll.scrollOffset = Vector2.zero;
            }
        }
    }
}
