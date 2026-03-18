using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

// ReSharper disable NotAccessedField.Local

namespace Events {
    /// <summary>
    /// Centralized failure diagnostics for EventBus handler exceptions.
    /// Writes one local NDJSON file per play session and can optionally echo to Unity logs.
    /// </summary>
    internal static class EventBusFailureDiagnostics {
        private static readonly object Gate = new();
        private static readonly int NewLineByteCount = Encoding.UTF8.GetByteCount(Environment.NewLine);
        private static FailureRuntimeSettings settings = FailureRuntimeSettings.Default;
        private static StreamWriter writer;
        private static string activeLogPath;
        private static string activeSessionId;
        private static bool initialized;
        private static bool internalErrorLogged;
        private static bool sessionStartWritten;
        private static bool sessionEndWritten;
        private static bool maxFileSizeReached;
        private static int recordCount;
        private static long bytesWritten;
        private static float nextFlushAt;
        private static bool hasFailureCaptureRuntimeOverride;
        private static bool failureCaptureRuntimeOverride;
        private static bool hasFileLoggingRuntimeOverride;
        private static bool fileLoggingRuntimeOverride;

        internal static bool RecordHandlerException(
            Type eventType,
            GameEvent gameEvent,
            Delegate handler,
            Exception exception,
            string callerMember,
            string callerFile,
            int callerLine) {
            var settingsSnapshot = GetSettingsSnapshot();
            if(settingsSnapshot.FailureCaptureEnabled == false) {
                return settingsSnapshot.FailFastOnHandlerException;
            }
            if(HasAnyConfiguredSink(settingsSnapshot) == false) {
                return settingsSnapshot.FailFastOnHandlerException;
            }

            EnsureInitialized();

            settingsSnapshot = GetSettingsSnapshot();
            if(settingsSnapshot.FailureCaptureEnabled == false) {
                return settingsSnapshot.FailFastOnHandlerException;
            }
            if(HasAnyConfiguredSink(settingsSnapshot) == false) {
                return settingsSnapshot.FailFastOnHandlerException;
            }
            if(settingsSnapshot.EchoToUnityConsole == false && IsFileSinkActive() == false) {
                return settingsSnapshot.FailFastOnHandlerException;
            }

            try {
                var method = handler?.Method;
                var target = handler?.Target;
                var subscriberTargetType = target != null ? target.GetType().FullName : string.Empty;
                var subscriberDeclaringType = method?.DeclaringType != null ? method.DeclaringType.FullName : "Unknown";
                var subscriberMethod = method != null ? method.Name : "Unknown";

                var subscriberInstanceId = 0;
                var subscriberObjectName = string.Empty;
                var subscriberObjectPath = string.Empty;
                var subscriberNetworkObjectId = string.Empty;
                var subscriberOwnerClientId = string.Empty;

                if(target is UnityEngine.Object unityTarget) {
                    subscriberInstanceId = unityTarget.GetInstanceID();
                    subscriberObjectName = unityTarget.name;
                }

                if(target is Component componentTarget) {
                    subscriberObjectName = componentTarget.gameObject != null ? componentTarget.gameObject.name : subscriberObjectName;
                    subscriberObjectPath = EventBusContextValues.BuildHierarchyPath(componentTarget.transform);
                }

                if(target is NetworkBehaviour networkTarget) {
                    subscriberNetworkObjectId = networkTarget.NetworkObject != null
                        ? networkTarget.NetworkObjectId.ToString(CultureInfo.InvariantCulture)
                        : string.Empty;
                    subscriberOwnerClientId = networkTarget.OwnerClientId.ToString(CultureInfo.InvariantCulture);
                }

                var publisherFile = string.IsNullOrEmpty(callerFile) ? string.Empty : Path.GetFileName(callerFile);
                var publisherContext = string.IsNullOrEmpty(publisherFile)
                    ? callerMember
                    : $"{publisherFile}:{callerLine} ({callerMember})";
                var correlationId = gameEvent != null ? gameEvent.CorrelationId : string.Empty;
                var parentCorrelationId = gameEvent != null ? gameEvent.ParentCorrelationId : string.Empty;
                var correlationDepth = gameEvent != null ? gameEvent.CorrelationDepth : 0;
                var publisherEventContext = gameEvent != null ? gameEvent.BuildContextSummary() : string.Empty;
                var subscriberContext = ResolveSubscriberContext(target, settingsSnapshot.RedactIdentifiers);

                var record = new EventBusFailureRecord {
                    RecordType = "handler_exception",
                    SessionId = activeSessionId,
                    Sequence = recordCount + 1,
                    TimestampUtc = DateTime.UtcNow.ToString("o"),
                    Severity = "Error",
                    Scene = ResolveSceneName(),
                    Frame = Time.frameCount,
                    NetworkRole = ResolveNetworkRole(),
                    EventType = eventType != null ? eventType.FullName : "Unknown",
                    PublisherMember = callerMember,
                    PublisherFile = publisherFile,
                    PublisherLine = callerLine,
                    PublisherContext = publisherContext,
                    CorrelationId = correlationId,
                    ParentCorrelationId = parentCorrelationId,
                    CorrelationDepth = correlationDepth,
                    PublisherEventContext = MaybeRedactString(publisherEventContext, settingsSnapshot.RedactIdentifiers),
                    SubscriberMethod = subscriberMethod,
                    SubscriberDeclaringType = subscriberDeclaringType,
                    SubscriberTargetType = subscriberTargetType,
                    SubscriberInstanceId = MaybeRedactInt(subscriberInstanceId, settingsSnapshot.RedactIdentifiers),
                    SubscriberObjectName = MaybeRedactString(subscriberObjectName, settingsSnapshot.RedactIdentifiers),
                    SubscriberObjectPath = MaybeRedactString(subscriberObjectPath, settingsSnapshot.RedactIdentifiers),
                    SubscriberNetworkObjectId = MaybeRedactString(subscriberNetworkObjectId, settingsSnapshot.RedactIdentifiers),
                    SubscriberOwnerClientId = MaybeRedactString(subscriberOwnerClientId, settingsSnapshot.RedactIdentifiers),
                    SubscriberContext = subscriberContext,
                    ExceptionType = exception != null ? exception.GetType().FullName : "UnknownException",
                    ExceptionMessage = exception != null ? exception.Message : "Unknown exception",
                    ExceptionStackTrace = exception != null ? exception.StackTrace : string.Empty,
                    EventPayload = settingsSnapshot.IncludeEventPayload && gameEvent != null ? gameEvent.ToString() : string.Empty,
                    PublishStackTrace = settingsSnapshot.IncludePublisherStackTrace ? new System.Diagnostics.StackTrace(2, true).ToString() : string.Empty
                };

                WriteRecord(record, flushImmediately: settingsSnapshot.ImmediateFlushOnError);

                if(settingsSnapshot.EchoToUnityConsole) {
                    EventDevLog.LogError(
                        $"[EventBusFailure] event={record.EventType} publisher={record.PublisherContext} " +
                        $"subscriber={record.SubscriberDeclaringType}.{record.SubscriberMethod} " +
                        $"exception={record.ExceptionType}: {record.ExceptionMessage} session={record.SessionId} " +
                        $"corr={record.CorrelationId}");
                }
            } catch(Exception internalException) {
                if(internalErrorLogged) {
                    return settingsSnapshot.FailFastOnHandlerException;
                }

                internalErrorLogged = true;
                EventDevLog.LogError($"[EventBusFailure] Diagnostics pipeline failure: {internalException}");
            }

            return settingsSnapshot.FailFastOnHandlerException;
        }

        internal static void ApplyLogSettings(EventBusLogSettings eventBusLogSettings) {
            if(eventBusLogSettings == null) return;
            var shouldInitialize = false;

            lock(Gate) {
                settings = new FailureRuntimeSettings {
                    FailureCaptureEnabled = eventBusLogSettings.failureCaptureEnabled,
                    FileLoggingEnabled = eventBusLogSettings.failureFileLoggingEnabled,
                    EchoToUnityConsole = eventBusLogSettings.failureEchoToUnityConsole,
                    IncludePublisherStackTrace = eventBusLogSettings.failureIncludePublisherStackTrace,
                    IncludeEventPayload = eventBusLogSettings.failureIncludeEventPayload,
                    FailFastOnHandlerException = eventBusLogSettings.failureFailFastOnHandlerException,
                    FlushIntervalSeconds = Mathf.Max(0.1f, eventBusLogSettings.failureFlushIntervalSeconds),
                    MaxFileSizeBytes = Mathf.Max(1, eventBusLogSettings.failureMaxFileSizeMb) * 1024L * 1024L,
                    MaxRecordsPerSession = Mathf.Max(1, eventBusLogSettings.failureMaxRecordsPerSession),
                    ImmediateFlushOnError = eventBusLogSettings.failureImmediateFlushOnError,
                    RedactIdentifiers = eventBusLogSettings.failureRedactIdentifiers
                };
                if(hasFailureCaptureRuntimeOverride) {
                    settings.FailureCaptureEnabled = failureCaptureRuntimeOverride;
                }
                if(hasFileLoggingRuntimeOverride) {
                    settings.FileLoggingEnabled = fileLoggingRuntimeOverride;
                }

                if(initialized) {
                    ApplyWriterStateLocked();
                } else {
                    shouldInitialize = settings is { FailureCaptureEnabled: true, FileLoggingEnabled: true };
                }
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFileLoggingEnabled(bool enabled) {
            bool shouldInitialize;
            lock(Gate) {
                settings.FileLoggingEnabled = enabled;
                hasFileLoggingRuntimeOverride = true;
                fileLoggingRuntimeOverride = enabled;
                if(initialized) {
                    ApplyWriterStateLocked();
                    return;
                }

                shouldInitialize = enabled && settings.FailureCaptureEnabled;
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFailureCaptureEnabled(bool enabled) {
            bool shouldInitialize;
            lock(Gate) {
                settings.FailureCaptureEnabled = enabled;
                hasFailureCaptureRuntimeOverride = true;
                failureCaptureRuntimeOverride = enabled;
                shouldInitialize = enabled && settings.FileLoggingEnabled && initialized == false;
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFailFastEnabled(bool enabled) {
            lock(Gate) {
                settings.FailFastOnHandlerException = enabled;
            }
        }

        internal static string GetActiveLogPath() {
            lock(Gate) {
                return activeLogPath;
            }
        }

        internal static void Tick() {
            lock(Gate) {
                if(initialized == false || writer == null) return;
                if(Time.unscaledTime < nextFlushAt) return;
                nextFlushAt = Time.unscaledTime + settings.FlushIntervalSeconds;

                try {
                    writer.Flush();
                } catch(Exception flushException) {
                    if(internalErrorLogged) return;
                    internalErrorLogged = true;
                    EventDevLog.LogError($"[EventBusFailure] Failed flushing diagnostics log: {flushException}");
                }
            }
        }

        internal static void Shutdown() {
            lock(Gate) {
                if(initialized == false) return;
                if(sessionEndWritten == false) {
                    WriteSessionBoundaryLocked("session_end");
                    sessionEndWritten = true;
                }

                try {
                    writer?.Flush();
                    writer?.Dispose();
                } catch(Exception closeException) {
                    if(internalErrorLogged == false) {
                        internalErrorLogged = true;
                        EventDevLog.LogError($"[EventBusFailure] Failed closing diagnostics log: {closeException}");
                    }
                } finally {
                    writer = null;
                    activeLogPath = null;
                    ResetSessionStateLocked();
                }
            }
        }

        private static void EnsureInitialized() {
            if(initialized) return;

            lock(Gate) {
                if(initialized) return;

                initialized = true;
                activeSessionId = Guid.NewGuid().ToString("N")[..8];

                var isWindows = Application.platform == RuntimePlatform.WindowsEditor ||
                                Application.platform == RuntimePlatform.WindowsPlayer;
                if(isWindows == false) {
                    // Current product scope is Windows-first for local file diagnostics.
                    // Keep failure capture active, but disable file sink on other platforms.
                    if(settings.FileLoggingEnabled) {
                        EventDevLog.Log("[EventBusFailure] File logging is supported on Windows only; disabling file sink for this session.");
                    }
                    settings.FileLoggingEnabled = false;
                }

                ApplyWriterStateLocked();

                if(sessionStartWritten || writer == null) return;
                WriteSessionBoundaryLocked("session_start");
                sessionStartWritten = true;
            }
        }

        private static void ApplyWriterStateLocked() {
            if(settings.FileLoggingEnabled == false) {
                if(writer != null) {
                    try {
                        writer.Flush();
                        writer.Dispose();
                    } catch {
                        // No-op: best effort close.
                    } finally {
                        writer = null;
                    }
                }
                activeLogPath = null;
                sessionStartWritten = false;

                return;
            }

            if(writer != null) return;

            try {
                var directory = Path.Combine(Application.persistentDataPath, "Logs", "EventBus");
                Directory.CreateDirectory(directory);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var candidatePath = Path.Combine(directory, $"eventbus_{timestamp}_{activeSessionId}.ndjson");

                var stream = new FileStream(candidatePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
                    AutoFlush = false
                };
                activeLogPath = candidatePath;
                recordCount = 0;
                bytesWritten = 0;
                maxFileSizeReached = false;
                nextFlushAt = Time.unscaledTime + settings.FlushIntervalSeconds;
                if(sessionStartWritten) return;
                WriteSessionBoundaryLocked("session_start");
                sessionStartWritten = true;
            } catch(Exception writerException) {
                if(internalErrorLogged == false) {
                    internalErrorLogged = true;
                    EventDevLog.LogError($"[EventBusFailure] Failed opening diagnostics log file: {writerException}");
                }

                writer = null;
                activeLogPath = null;
            }
        }

        private static void ResetSessionStateLocked() {
            initialized = false;
            activeSessionId = null;
            sessionStartWritten = false;
            sessionEndWritten = false;
            maxFileSizeReached = false;
            recordCount = 0;
            bytesWritten = 0;
            nextFlushAt = 0f;
            internalErrorLogged = false;
        }

        private static void WriteSessionBoundaryLocked(string recordType) {
            var boundary = new EventBusFailureRecord {
                RecordType = recordType,
                SessionId = activeSessionId,
                Sequence = recordCount + 1,
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                Severity = "Info",
                Scene = ResolveSceneName(),
                Frame = Time.frameCount,
                NetworkRole = ResolveNetworkRole(),
                EventType = string.Empty,
                PublisherContext = string.Empty,
                CorrelationId = string.Empty,
                ParentCorrelationId = string.Empty,
                CorrelationDepth = 0,
                PublisherEventContext = string.Empty,
                SubscriberMethod = string.Empty,
                SubscriberContext = string.Empty,
                ExceptionType = string.Empty,
                ExceptionMessage = string.Empty,
                ExceptionStackTrace = string.Empty,
                EventPayload = string.Empty,
                PublishStackTrace = string.Empty
            };

            WriteRecordLocked(boundary, flushImmediately: true);
        }

        private static void WriteRecord(EventBusFailureRecord record, bool flushImmediately) {
            lock(Gate) {
                WriteRecordLocked(record, flushImmediately);
            }
        }

        private static void WriteRecordLocked(EventBusFailureRecord record, bool flushImmediately) {
            if(record == null) return;
            if(writer == null) return;
            if(maxFileSizeReached) return;
            if(recordCount >= settings.MaxRecordsPerSession) return;

            try {
                record.Sequence = recordCount + 1;
                var json = JsonUtility.ToJson(record);
                var byteCount = Encoding.UTF8.GetByteCount(json) + NewLineByteCount;
                if(bytesWritten + byteCount > settings.MaxFileSizeBytes) {
                    maxFileSizeReached = true;
                    EventDevLog.LogWarning(
                        $"[EventBusFailure] Log file size cap reached for session {activeSessionId}. " +
                        "Further EventBus failure records will be dropped.");
                    return;
                }

                writer.WriteLine(json);
                bytesWritten += byteCount;
                recordCount++;

                if(flushImmediately) {
                    writer.Flush();
                }
            } catch(Exception writeException) {
                if(internalErrorLogged) return;
                internalErrorLogged = true;
                EventDevLog.LogError($"[EventBusFailure] Failed writing diagnostics record: {writeException}");
            }
        }

        private static string ResolveSceneName() {
            var scene = SceneManager.GetActiveScene();
            return scene.IsValid() ? scene.name : "Unknown";
        }

        private static string ResolveNetworkRole() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return "Offline";
            return networkManager.IsServer switch {
                true when networkManager.IsClient => "Host",
                true => "Server",
                _ => networkManager.IsClient ? "Client" : "Offline"
            };
        }

        private static string MaybeRedactString(string input, bool redactIdentifiers) {
            if(redactIdentifiers == false) return input;
            return string.IsNullOrEmpty(input) ? input : "<redacted>";
        }

        private static int MaybeRedactInt(int input, bool redactIdentifiers) {
            if(redactIdentifiers == false) return input;
            return input == 0 ? 0 : -1;
        }

        private static string ResolveSubscriberContext(object target, bool redactIdentifiers) {
            if(target is not IEventBusContextProvider provider) return string.Empty;
            if(redactIdentifiers) return "<redacted>";

            try {
                var values = new EventBusContextValues();
                provider.PopulateEventBusContext(values);
                return values.ToCompactString();
            } catch(Exception ex) {
                return $"context_error={ex.GetType().Name}";
            }
        }

        private static FailureRuntimeSettings GetSettingsSnapshot() {
            lock(Gate) {
                return settings;
            }
        }

        private static bool HasAnyConfiguredSink(FailureRuntimeSettings failureRuntimeSettings) {
            return failureRuntimeSettings.FileLoggingEnabled || failureRuntimeSettings.EchoToUnityConsole;
        }

        private static bool IsFileSinkActive() {
            lock(Gate) {
                return writer != null;
            }
        }

        [Serializable]
        private sealed class EventBusFailureRecord {
            internal string RecordType;
            internal string SessionId;
            internal long Sequence;
            internal string TimestampUtc;
            internal string Severity;
            internal string Scene;
            internal int Frame;
            internal string NetworkRole;
            internal string EventType;
            internal string PublisherMember;
            internal string PublisherFile;
            internal int PublisherLine;
            internal string PublisherContext;
            internal string CorrelationId;
            internal string ParentCorrelationId;
            internal int CorrelationDepth;
            internal string PublisherEventContext;
            internal string SubscriberMethod;
            internal string SubscriberDeclaringType;
            internal string SubscriberTargetType;
            internal int SubscriberInstanceId;
            internal string SubscriberObjectName;
            internal string SubscriberObjectPath;
            internal string SubscriberNetworkObjectId;
            internal string SubscriberOwnerClientId;
            internal string SubscriberContext;
            internal string ExceptionType;
            internal string ExceptionMessage;
            internal string ExceptionStackTrace;
            internal string EventPayload;
            internal string PublishStackTrace;
        }

        private struct FailureRuntimeSettings {
            internal bool FailureCaptureEnabled;
            internal bool FileLoggingEnabled;
            internal bool EchoToUnityConsole;
            internal bool IncludePublisherStackTrace;
            internal bool IncludeEventPayload;
            internal bool FailFastOnHandlerException;
            internal float FlushIntervalSeconds;
            internal long MaxFileSizeBytes;
            internal int MaxRecordsPerSession;
            internal bool ImmediateFlushOnError;
            internal bool RedactIdentifiers;

            public static FailureRuntimeSettings Default => new() {
                FailureCaptureEnabled = true,
                FileLoggingEnabled = true,
                EchoToUnityConsole = true,
                IncludePublisherStackTrace = false,
                IncludeEventPayload = false,
                FailFastOnHandlerException = false,
                FlushIntervalSeconds = 2f,
                MaxFileSizeBytes = 8L * 1024L * 1024L,
                MaxRecordsPerSession = 20000,
                ImmediateFlushOnError = true,
                RedactIdentifiers = false
            };
        }
    }

    internal sealed class EventBusFailureDiagnosticsDriver : MonoBehaviour {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateInstance() {
            // Avoid creating duplicates if something else already added one.
            if(FindFirstObjectByType<EventBusFailureDiagnosticsDriver>() != null) return;

            var go = new GameObject("EventBusFailureDiagnosticsDriver") {
                hideFlags = HideFlags.HideAndDontSave
            };
            DontDestroyOnLoad(go);
            go.AddComponent<EventBusFailureDiagnosticsDriver>();
        }

        private void Update() {
            EventBusFailureDiagnostics.Tick();
        }

        private void OnApplicationQuit() {
            EventBusFailureDiagnostics.Shutdown();
        }

        private void OnDestroy() {
            EventBusFailureDiagnostics.Shutdown();
        }
    }
}
