using System.Collections;
using System.Collections.Generic;
using Game.Social;
using Game.Menu;
using Game.Player;
using UnityEngine;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace Game.UI {
    public class ChatUIManager : UIElementBase {
        private VisualElement _chatContainer;
        private VisualElement _chatBackground;
        private ScrollView _chatScroll;
        private VisualElement _chatMessageList;
        private TextField _chatInput;

        private const int MAX_MESSAGES = 50; // Maximum messages to keep in history
        private const float MESSAGE_LIFETIME = 8f; // Seconds before message fades when chat is closed
        private const float FADE_DURATION = 1.5f; // Fade out duration

        private class ChatMessageElement {
            public VisualElement Element;
            public float Timestamp;
            public bool IsVisible = true;
        }

        private List<ChatMessageElement> _messageElements = new List<ChatMessageElement>();
        private Coroutine _lifetimeCheckCoroutine;

        public bool IsChatOpen { get; private set; }

        protected override void OnInitialize() {
            _chatContainer = QOptional<VisualElement>("chat-container");
            _chatBackground = QOptional<VisualElement>("chat-background");
            _chatScroll = QOptional<ScrollView>("chat-scroll");
            _chatMessageList = QOptional<VisualElement>("chat-message-list");
            _chatInput = QOptional<TextField>("chat-input");

            if(_chatInput != null) {
                // Register Submit event
                _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            }
            
            // Start with chat non-interactive (closed state)
            if(_chatScroll != null) {
                _chatScroll.pickingMode = PickingMode.Ignore;
            }

            if(ChatManager.Instance != null) {
                ChatManager.Instance.OnMessageReceived += HandleMessageReceived;
            }
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "chat-container", typeof(VisualElement) },
                { "chat-background", typeof(VisualElement) },
                { "chat-message-list", typeof(VisualElement) },
                { "chat-input", typeof(TextField) }
            };
        }

        protected override void OnDestroy() {
            if(ChatManager.Instance != null) {
                ChatManager.Instance.OnMessageReceived -= HandleMessageReceived;
            }
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
            if(UnityEngine.InputSystem.Keyboard.current != null) {
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
        }

        private void OpenChat() {
            if(_chatInput == null) return;
            
            IsChatOpen = true;
            
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
                if(msgElement.Element != null) {
                    msgElement.Element.RemoveFromClassList("fading");
                    msgElement.Element.style.display = DisplayStyle.Flex;
                    msgElement.IsVisible = true;
                }
            }
            
            // Scroll to bottom
            StartCoroutine(ScrollToBottom());
            
            // Focus input field after a frame
            StartCoroutine(FocusInputField());
            
            // Unlock mouse
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            
            // Lock player camera
            if (PlayerController.LocalPlayer != null) {
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
            _chatInput.value = ""; // Clear input
            _chatInput.AddToClassList("minimized");
            
            // Instantly hide any expired messages upon closing
            float currentTime = Time.time;
            foreach(var msgElement in _messageElements) {
                if(msgElement.Element == null) continue;
                if(currentTime - msgElement.Timestamp >= MESSAGE_LIFETIME) {
                    msgElement.Element.style.display = DisplayStyle.None;
                    msgElement.IsVisible = false;
                }
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
            
            // Ensure we are scrolled to the bottom so newest messages are visible
            StartCoroutine(ScrollToBottom());
            
            // Only re-lock if scoreboard isn't also visible
            var scoreboardVisible = ScoreboardManager.Instance != null && ScoreboardManager.Instance.IsScoreboardVisible;
            if (!scoreboardVisible) {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                
                // Unlock player camera
                if (PlayerController.LocalPlayer != null) {
                    PlayerController.LocalPlayer.LockLook = false;
                }
            }
        }

        private void SubmitChat() {
             if(_chatInput == null) return;
             
             string message = _chatInput.value;
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

        private void OnChatInputKeyDown(KeyDownEvent evt) {
            if(evt.keyCode == KeyCode.Return) {
                // Handled in Update/SubmitChat, but prevent default newline
                evt.StopPropagation();
            }
        }

        private void HandleMessageReceived(ChatMessage msg) {
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
            if(_messageElements.Count > MAX_MESSAGES) {
                var oldest = _messageElements[0];
                _messageElements.RemoveAt(0);
                oldest.Element?.RemoveFromHierarchy();
            }

            // Always scroll to bottom to see newest message
            StartCoroutine(ScrollToBottom());

            // If chat is closed, ensure lifetime check is running
            if(!IsChatOpen && _lifetimeCheckCoroutine == null) {
                StartLifetimeCheck();
            }
        }

        private VisualElement CreateMessageRow(ChatMessage msg) {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;

            if(msg.IsSystemMessage) {
                var label = new Label(msg.MessageContent);
                label.AddToClassList("chat-message");
                label.AddToClassList("chat-message-system");
                row.Add(label);
            } else {
                var nameLabel = new Label(msg.SenderName);
                nameLabel.AddToClassList("chat-message-name");
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                
                // Click handler for context menu (Right-click)
                nameLabel.RegisterCallback<PointerDownEvent>(evt => {
                    if (evt.button != 1) return; // Right-click only
                    if (msg.SenderSteamId == 0) return;
                    
                    // Don't show for self
                    if (Steamworks.SteamClient.SteamId == msg.SenderSteamId) return;

                    if (InGameContextMenuManager.Instance != null) {
                        InGameContextMenuManager.Instance.Show(msg.SenderSteamId, evt.position);
                    }
                });
                
                row.Add(nameLabel);
                
                var contentLabel = new Label($": {msg.MessageContent}");
                contentLabel.AddToClassList("chat-message-content");
                row.Add(contentLabel);
            }

            return row;
        }

        private void StartLifetimeCheck() {
            if(_lifetimeCheckCoroutine != null) {
                StopCoroutine(_lifetimeCheckCoroutine);
            }
            _lifetimeCheckCoroutine = StartCoroutine(LifetimeCheckRoutine());
        }

        private IEnumerator LifetimeCheckRoutine() {
            while(!IsChatOpen) {
                float currentTime = Time.time;
                bool anyChanges = false;

                foreach(var msgElement in _messageElements) {
                    if(msgElement.Element == null) continue;

                    float age = currentTime - msgElement.Timestamp;
                    
                    // Start fading when approaching lifetime
                    if(age >= MESSAGE_LIFETIME && msgElement.IsVisible) {
                        msgElement.Element.AddToClassList("fading");
                        msgElement.IsVisible = false;
                        anyChanges = true;
                    }
                }

                // Wait for fade duration, then hide faded messages
                if(anyChanges) {
                    yield return new WaitForSeconds(FADE_DURATION);
                    
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
            if(_chatScroll != null && _chatMessageList != null) {
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
}
