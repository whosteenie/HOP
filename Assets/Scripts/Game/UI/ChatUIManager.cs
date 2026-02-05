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
        private VisualElement _chatHistoryContainer;
        private ScrollView _chatScroll;
        private VisualElement _chatMessageList;
        private VisualElement _chatRecentMessages;
        private TextField _chatInput;

        private const int MAX_RECENT_MESSAGES = 5;
        private const float RECENT_MESSAGE_FADE_TIME = 5f; // Seconds before fade starts
        private List<VisualElement> _recentMessageElements = new List<VisualElement>();
        private Queue<ChatMessage> _messageHistory = new Queue<ChatMessage>();
        private Coroutine _fadeOutCoroutine;

        public bool IsChatOpen { get; private set; }

        protected override void OnInitialize() {
            _chatContainer = QOptional<VisualElement>("chat-container");
            _chatHistoryContainer = QOptional<VisualElement>("chat-history-container");
            _chatScroll = QOptional<ScrollView>("chat-scroll");
            _chatMessageList = QOptional<VisualElement>("chat-message-list");
            _chatRecentMessages = QOptional<VisualElement>("chat-recent-messages");
            _chatInput = QOptional<TextField>("chat-input");

            if(_chatInput != null) {
                // Register Submit event
                _chatInput.RegisterCallback<KeyDownEvent>(OnChatInputKeyDown);
            }

            if(ChatManager.Instance != null) {
                ChatManager.Instance.OnMessageReceived += HandleMessageReceived;
            }
        }

        protected override Dictionary<string, System.Type> GetRequiredElements() {
            return new Dictionary<string, System.Type> {
                { "chat-container", typeof(VisualElement) },
                { "chat-history-container", typeof(VisualElement) },
                { "chat-message-list", typeof(VisualElement) },
                { "chat-recent-messages", typeof(VisualElement) },
                { "chat-input", typeof(TextField) }
            };
        }

        protected override void OnDestroy() {
            if(ChatManager.Instance != null) {
                ChatManager.Instance.OnMessageReceived -= HandleMessageReceived;
            }
        }


        public void ClearChatHistory() {
            // Clear message history queue
            _messageHistory.Clear();
            
            // Clear visual message list
            if(_chatMessageList != null) {
                _chatMessageList.Clear();
            }
            
            // Clear recent messages
            ClearRecentMessages();
            
            // Stop any fade-out coroutine
            if(_fadeOutCoroutine != null) {
                StopCoroutine(_fadeOutCoroutine);
                _fadeOutCoroutine = null;
            }
            
            // Ensure chat is closed
            if(IsChatOpen) {
                CloseChat();
            }
        }

        private void Update() {
            // Toggle Chat
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
            
            // Show history container and input
            if(_chatHistoryContainer != null) {
                _chatHistoryContainer.RemoveFromClassList("hidden");
            }
            _chatInput.RemoveFromClassList("hidden");
            
            // Stop any fade-out coroutine
            if(_fadeOutCoroutine != null) {
                StopCoroutine(_fadeOutCoroutine);
                _fadeOutCoroutine = null;
            }
            
            // Clear recent messages when opening chat (they're now in history)
            ClearRecentMessages();
            
            // Populate history container with stored messages if empty
            if(_chatMessageList != null && _chatMessageList.childCount == 0 && _messageHistory.Count > 0) {
                foreach(var msg in _messageHistory) {
                    var row = CreateMessageRow(msg);
                    _chatMessageList.Add(row);
                }
                // Scroll to bottom after populating
                StartCoroutine(ScrollToBottom());
            }
            
            // Focus input field after a frame to ensure it's ready
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
            _chatInput.AddToClassList("hidden");
            
            // Hide history container
            if(_chatHistoryContainer != null) {
                _chatHistoryContainer.AddToClassList("hidden");
            }
            
            // Move recent messages from history to recent messages area
            MoveRecentMessagesToDisplay();
            
            // Start fade-out timer for recent messages
            if(_recentMessageElements.Count > 0) {
                if(_fadeOutCoroutine != null) {
                    StopCoroutine(_fadeOutCoroutine);
                }
                _fadeOutCoroutine = StartCoroutine(FadeOutRecentMessages());
            }
            
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
                 
                 // Refocus input field after a frame to ensure it's ready and can receive input
                 StartCoroutine(FocusInputField());
             } else {
                 // Empty input - close chat (common game pattern for quick chat checking)
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
            if(IsChatOpen) {
                // Add to history container
                AddMessageToHistory(msg);
            } else {
                // Add to recent messages area (shown on screen)
                AddMessageToRecent(msg);
            }
        }

        private void AddMessageToHistory(ChatMessage msg) {
            if(_chatMessageList == null) return;

            // Store message in history
            _messageHistory.Enqueue(msg);
            if(_messageHistory.Count > 50) { // Limit history size
                _messageHistory.Dequeue();
            }

            var row = CreateMessageRow(msg);
            // Insert at the end (bottom) - Add() adds to the end
            _chatMessageList.Add(row);

            // Auto-scroll to bottom (newest at bottom)
            StartCoroutine(ScrollToBottom());
        }

        private void AddMessageToRecent(ChatMessage msg) {
            if(_chatRecentMessages == null) return;

            // Also store in history for when chat is opened
            _messageHistory.Enqueue(msg);
            if(_messageHistory.Count > 50) { // Limit history size
                _messageHistory.Dequeue();
            }

            // Stop fade-out if in progress
            if(_fadeOutCoroutine != null) {
                StopCoroutine(_fadeOutCoroutine);
                _fadeOutCoroutine = null;
            }

            // Reset opacity on existing messages
            foreach(var elem in _recentMessageElements) {
                elem.RemoveFromClassList("fading");
                elem.style.opacity = 1f;
            }

            var row = CreateMessageRow(msg, isRecent: true);
            _recentMessageElements.Add(row);
            _chatRecentMessages.Add(row);

            // Limit recent messages
            if(_recentMessageElements.Count > MAX_RECENT_MESSAGES) {
                var oldest = _recentMessageElements[0];
                _recentMessageElements.RemoveAt(0);
                oldest.RemoveFromHierarchy();
            }

            // Start fade-out timer
            _fadeOutCoroutine = StartCoroutine(FadeOutRecentMessages());
        }

        private VisualElement CreateMessageRow(ChatMessage msg, bool isRecent = false) {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;

            if(msg.IsSystemMessage) {
                var label = new Label(msg.MessageContent);
                label.AddToClassList(isRecent ? "chat-recent-message" : "chat-message");
                if(isRecent) {
                    label.AddToClassList("chat-recent-message-system");
                } else {
                    label.AddToClassList("chat-message-system");
                }
                row.Add(label);
            } else {
                var nameLabel = new Label(msg.SenderName);
                if(isRecent) {
                    nameLabel.AddToClassList("chat-recent-message");
                } else {
                    nameLabel.AddToClassList("chat-message-name");
                }
                nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                
                // Click handler for context menu (Right-click) - only in history
                if(!isRecent) {
                    nameLabel.RegisterCallback<PointerDownEvent>(evt => {
                        if (evt.button != 1) return; // Right-click only
                        if (msg.SenderSteamId == 0) return;
                        
                        // Don't show for self
                        if (Steamworks.SteamClient.SteamId == msg.SenderSteamId) return;

                        if (InGameContextMenuManager.Instance != null) {
                            InGameContextMenuManager.Instance.Show(msg.SenderSteamId, evt.position);
                        }
                    });
                }
                
                row.Add(nameLabel);
                
                var contentLabel = new Label($": {msg.MessageContent}");
                if(isRecent) {
                    contentLabel.AddToClassList("chat-recent-message");
                } else {
                    contentLabel.AddToClassList("chat-message-content");
                }
                row.Add(contentLabel);
            }

            return row;
        }

        private void MoveRecentMessagesToDisplay() {
            if(_chatRecentMessages == null) return;

            // Clear existing recent messages
            ClearRecentMessages();

            // Get the last few messages from history
            var messagesArray = _messageHistory.ToArray();
            var startIndex = Mathf.Max(0, messagesArray.Length - MAX_RECENT_MESSAGES);
            
            // Add recent messages from history (oldest to newest)
            for(int i = startIndex; i < messagesArray.Length; i++) {
                var msg = messagesArray[i];
                var row = CreateMessageRow(msg, isRecent: true);
                _recentMessageElements.Add(row);
                _chatRecentMessages.Add(row);
            }
        }

        private void ClearRecentMessages() {
            if(_chatRecentMessages == null) return;
            _chatRecentMessages.Clear();
            _recentMessageElements.Clear();
        }

        private IEnumerator FadeOutRecentMessages() {
            yield return new WaitForSeconds(RECENT_MESSAGE_FADE_TIME);
            
            // Fade out all recent messages
            foreach(var elem in _recentMessageElements) {
                elem.AddToClassList("fading");
            }
            
            yield return new WaitForSeconds(0.5f); // Wait for fade animation
            
            // Remove faded messages
            ClearRecentMessages();
            _fadeOutCoroutine = null;
        }

        private IEnumerator ScrollToBottom() {
            yield return null; // Wait for layout
            yield return null; // Wait another frame for layout to stabilize
            if(_chatScroll != null && _chatMessageList != null) {
                // Scroll to show the bottom (newest messages)
                // With justify-content: flex-end, newest messages are at the bottom
                // ScrollView scrolls from top (0) to bottom (max), so we want max scroll
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
