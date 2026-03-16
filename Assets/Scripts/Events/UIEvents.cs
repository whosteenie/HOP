namespace Events {
    using UnityEngine;

    /// <summary>
    /// Event published when gameplay wants the in-game menu root restored and any pause state cleared.
    /// </summary>
    public class RestoreGameplayMenuPresentationEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the pause menu should toggle open/closed.
    /// </summary>
    public class TogglePauseMenuRequestedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the in-game pause menu state changes.
    /// </summary>
    public class PauseMenuStateChangedEvent : GameEvent {
        public readonly bool IsPaused;

        public PauseMenuStateChangedEvent(bool isPaused) {
            IsPaused = isPaused;
        }
    }

    /// <summary>
    /// Event published when the chat UI open state changes.
    /// </summary>
    public class ChatOpenStateChangedEvent : GameEvent {
        public readonly bool IsOpen;

        public ChatOpenStateChangedEvent(bool isOpen) {
            IsOpen = isOpen;
        }
    }

    /// <summary>
    /// Event published when player health should be updated in the HUD.
    /// </summary>
    public class UpdateHealthEvent : GameEvent {
        public readonly float Current;
        public readonly float Max;

        public UpdateHealthEvent(float current, float max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when weapon ammo should be updated in the HUD.
    /// </summary>
    public class UpdateAmmoEvent : GameEvent {
        public readonly int Current;
        public readonly int Max;

        public UpdateAmmoEvent(int current, int max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when tag status should be updated in the HUD.
    /// </summary>
    public class UpdateTagStatusEvent : GameEvent {
        public readonly bool IsTagged;

        public UpdateTagStatusEvent(bool isTagged) {
            IsTagged = isTagged;
        }
    }

    /// <summary>
    /// Event published when kill multiplier should be updated in the HUD.
    /// </summary>
    public class UpdateMultiplierEvent : GameEvent {
        public readonly float Current;
        public readonly float Max;

        public UpdateMultiplierEvent(float current, float max) {
            Current = current;
            Max = max;
        }
    }

    /// <summary>
    /// Event published when the HUD should be shown.
    /// </summary>
    public class ShowHUDEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the HUD should be hidden.
    /// </summary>
    public class HideHUDEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the out-of-bounds countdown toast should update.
    /// </summary>
    public class UpdateOutOfBoundsCountdownEvent : GameEvent {
        public readonly bool IsVisible;
        public readonly float RemainingSeconds;

        public UpdateOutOfBoundsCountdownEvent(bool isVisible, float remainingSeconds = 0f) {
            IsVisible = isVisible;
            RemainingSeconds = remainingSeconds;
        }
    }

    /// <summary>
    /// Event published when the hopball interact prompt should update.
    /// </summary>
    public class UpdateHopballInteractPromptEvent : GameEvent {
        public readonly bool IsVisible;
        public readonly string Text;

        public UpdateHopballInteractPromptEvent(bool isVisible, string text) {
            IsVisible = isVisible;
            Text = text;
        }
    }

    /// <summary>
    /// Event published when the sniper overlay visibility should change.
    /// </summary>
    public class SetSniperOverlayVisibilityEvent : GameEvent {
        public readonly bool IsVisible;

        public SetSniperOverlayVisibilityEvent(bool isVisible) {
            IsVisible = isVisible;
        }
    }

    /// <summary>
    /// Event published when a kill feed entry should be added.
    /// </summary>
    public class AddKillFeedEntryEvent : GameEvent {
        public readonly string Killer;
        public readonly string Victim;
        public readonly bool IsLocalKiller;
        public readonly ulong KillerId;
        public readonly ulong VictimId;
        public readonly bool WasKill;

        public AddKillFeedEntryEvent(string killer, string victim, bool isLocalKiller, ulong killerId, ulong victimId, bool wasKill = true) {
            Killer = killer;
            Victim = victim;
            IsLocalKiller = isLocalKiller;
            KillerId = killerId;
            VictimId = victimId;
            WasKill = wasKill;
        }
    }

    /// <summary>
    /// Event published when the kill feed should be shown.
    /// </summary>
    public class ShowKillFeedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the kill feed should be hidden.
    /// </summary>
    public class HideKillFeedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when match time should be updated in the scoreboard.
    /// </summary>
    public class SetMatchTimeEvent : GameEvent {
        public readonly int Seconds;

        public SetMatchTimeEvent(int seconds) {
            Seconds = seconds;
        }
    }

    /// <summary>
    /// Event published when the scoreboard should be shown.
    /// </summary>
    public class ShowScoreboardEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the scoreboard should be hidden.
    /// </summary>
    public class HideScoreboardEvent : GameEvent {
    }

    /// <summary>
    /// Event published when scoreboard visibility changes.
    /// </summary>
    public class ScoreboardVisibilityChangedEvent : GameEvent {
        public readonly bool IsVisible;

        public ScoreboardVisibilityChangedEvent(bool isVisible) {
            IsVisible = isVisible;
        }
    }

    /// <summary>
    /// Event published when player code requests respawn fade presentation to begin.
    /// </summary>
    public class RequestRespawnFadeTransitionEvent : GameEvent {
    }

    /// <summary>
    /// Event published when player code requests the respawn fade-in phase to begin.
    /// </summary>
    public class RequestRespawnFadeInSignalEvent : GameEvent {
    }

    /// <summary>
    /// Event published when player code requests unexpected-disconnect FP visual capture.
    /// </summary>
    public class RequestDisconnectFpVisualCaptureEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the local damage vignette should flash from a world hit direction.
    /// </summary>
    public class ShowDamageVignetteFromWorldHitEvent : GameEvent {
        public readonly Vector3 WorldHitPos;
        public readonly Vector3 CameraPosition;
        public readonly Vector3 CameraForward;
        public readonly float Intensity;

        public ShowDamageVignetteFromWorldHitEvent(Vector3 worldHitPos, Vector3 cameraPosition, Vector3 cameraForward,
            float intensity) {
            WorldHitPos = worldHitPos;
            CameraPosition = cameraPosition;
            CameraForward = cameraForward;
            Intensity = intensity;
        }
    }

    /// <summary>
    /// Event published when the scoreboard should refresh its content (e.g. after score changes).
    /// </summary>
    public class ScoreboardRefreshRequestedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the gamemode has changed and the scoreboard title/headers should refresh.
    /// </summary>
    public class ScoreboardGamemodeChangedEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the small score display (next to timer) should be hidden.
    /// </summary>
    public class HideScoreDisplayEvent : GameEvent {
    }

    /// <summary>
    /// Event published when the small score display (next to timer) should be shown.
    /// </summary>
    public class ShowScoreDisplayEvent : GameEvent {
    }

    /// <summary>
    /// Event published when grapple UI should be hidden.
    /// </summary>
    public class HideGrappleUIEvent : GameEvent {
    }

    /// <summary>
    /// Event published when grapple UI should be shown.
    /// </summary>
    public class ShowGrappleUIEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a voice participant speech state changes.
    /// </summary>
    public class VoiceParticipantSpeechChangedEvent : GameEvent {
        public readonly string PlayerId;
        public readonly string DisplayName;
        public readonly bool IsSpeaking;

        public VoiceParticipantSpeechChangedEvent(string playerId, string displayName, bool isSpeaking) {
            PlayerId = playerId;
            DisplayName = displayName;
            IsSpeaking = isSpeaking;
        }
    }

    /// <summary>
    /// Event published when a voice participant leaves the active channel.
    /// </summary>
    public class VoiceParticipantRemovedEvent : GameEvent {
        public readonly string PlayerId;

        public VoiceParticipantRemovedEvent(string playerId) {
            PlayerId = playerId;
        }
    }

    /// <summary>
    /// Event published when local push-to-talk active state changes.
    /// </summary>
    public class VoiceLocalPttStateChangedEvent : GameEvent {
        public readonly bool IsActive;

        public VoiceLocalPttStateChangedEvent(bool isActive) {
            IsActive = isActive;
        }
    }

    /// <summary>
    /// Event published when the voice overlay should flush stale participant UI and rebuild from current state.
    /// </summary>
    public class VoiceOverlayResetEvent : GameEvent {
    }

    /// <summary>
    /// Event published when a chat message should be rendered by chat UI.
    /// The payload is a simple DTO so Events does not depend on Game.Social types.
    /// </summary>
    public class ChatMessageReceivedEvent : GameEvent {
        public readonly ulong SenderSteamId;
        public readonly string SenderName;
        public readonly string MessageContent;
        public readonly bool IsSystemMessage;

        public ChatMessageReceivedEvent(ulong senderSteamId, string senderName, string messageContent,
            bool isSystemMessage) {
            SenderSteamId = senderSteamId;
            SenderName = senderName;
            MessageContent = messageContent;
            IsSystemMessage = isSystemMessage;
        }
    }

    /// <summary>
    /// Event published when an off-screen indicator target is enabled/disabled.
    /// The payload is kept generic (UnityEngine.Component) so that Events
    /// does not depend on any specific indicator implementation.
    /// </summary>
    public class IndicatorTargetStateChangedEvent : GameEvent {
        public readonly Component Target;
        public readonly bool IsActive;

        public IndicatorTargetStateChangedEvent(Component target, bool isActive) {
            Target = target;
            IsActive = isActive;
        }
    }
}

