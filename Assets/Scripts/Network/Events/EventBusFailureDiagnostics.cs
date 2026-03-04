using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Network.Events {
    /// <summary>
    /// Centralized failure diagnostics for EventBus handler exceptions.
    /// Writes one local NDJSON file per play session and can optionally echo to Unity logs.
    /// </summary>
    internal static class EventBusFailureDiagnostics {
        private static readonly object Gate = new();
        private static readonly int NewLineByteCount = Encoding.UTF8.GetByteCount(Environment.NewLine);
        private static FailureRuntimeSettings _settings = FailureRuntimeSettings.Default;
        private static StreamWriter _writer;
        private static string _activeLogPath;
        private static string _sessionId;
        private static bool _initialized;
        private static bool _internalErrorLogged;
        private static bool _sessionStartWritten;
        private static bool _sessionEndWritten;
        private static bool _maxFileSizeReached;
        private static int _recordCount;
        private static long _bytesWritten;
        private static float _nextFlushAt;
        private static EventBusFailureDiagnosticsDriver _driver;
        private static bool _hasFailureCaptureRuntimeOverride;
        private static bool _failureCaptureRuntimeOverride;
        private static bool _hasFileLoggingRuntimeOverride;
        private static bool _fileLoggingRuntimeOverride;

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
                    recordType = "handler_exception",
                    sessionId = _sessionId,
                    sequence = _recordCount + 1,
                    timestampUtc = DateTime.UtcNow.ToString("o"),
                    severity = "Error",
                    scene = ResolveSceneName(),
                    frame = Time.frameCount,
                    networkRole = ResolveNetworkRole(),
                    eventType = eventType != null ? eventType.FullName : "Unknown",
                    publisherMember = callerMember,
                    publisherFile = publisherFile,
                    publisherLine = callerLine,
                    publisherContext = publisherContext,
                    correlationId = correlationId,
                    parentCorrelationId = parentCorrelationId,
                    correlationDepth = correlationDepth,
                    publisherEventContext = MaybeRedactString(publisherEventContext, settingsSnapshot.RedactIdentifiers),
                    subscriberMethod = subscriberMethod,
                    subscriberDeclaringType = subscriberDeclaringType,
                    subscriberTargetType = subscriberTargetType,
                    subscriberInstanceId = MaybeRedactInt(subscriberInstanceId, settingsSnapshot.RedactIdentifiers),
                    subscriberObjectName = MaybeRedactString(subscriberObjectName, settingsSnapshot.RedactIdentifiers),
                    subscriberObjectPath = MaybeRedactString(subscriberObjectPath, settingsSnapshot.RedactIdentifiers),
                    subscriberNetworkObjectId = MaybeRedactString(subscriberNetworkObjectId, settingsSnapshot.RedactIdentifiers),
                    subscriberOwnerClientId = MaybeRedactString(subscriberOwnerClientId, settingsSnapshot.RedactIdentifiers),
                    subscriberContext = subscriberContext,
                    exceptionType = exception != null ? exception.GetType().FullName : "UnknownException",
                    exceptionMessage = exception != null ? exception.Message : "Unknown exception",
                    exceptionStackTrace = exception != null ? exception.StackTrace : string.Empty,
                    eventPayload = settingsSnapshot.IncludeEventPayload && gameEvent != null ? gameEvent.ToString() : string.Empty,
                    publishStackTrace = settingsSnapshot.IncludePublisherStackTrace ? new System.Diagnostics.StackTrace(2, true).ToString() : string.Empty
                };

                WriteRecord(record, flushImmediately: settingsSnapshot.ImmediateFlushOnError);

                if(settingsSnapshot.EchoToUnityConsole) {
                    Debug.LogError(
                        $"[EventBusFailure] event={record.eventType} publisher={record.publisherContext} " +
                        $"subscriber={record.subscriberDeclaringType}.{record.subscriberMethod} " +
                        $"exception={record.exceptionType}: {record.exceptionMessage} session={record.sessionId} " +
                        $"corr={record.correlationId}");
                }
            } catch(Exception internalException) {
                if(_internalErrorLogged) {
                    return settingsSnapshot.FailFastOnHandlerException;
                }

                _internalErrorLogged = true;
                Debug.LogError($"[EventBusFailure] Diagnostics pipeline failure: {internalException}");
            }

            return settingsSnapshot.FailFastOnHandlerException;
        }

        internal static void ApplyLogSettings(EventBusLogSettings settings) {
            if(settings == null) return;
            var shouldInitialize = false;

            lock(Gate) {
                _settings = new FailureRuntimeSettings {
                    FailureCaptureEnabled = settings.failureCaptureEnabled,
                    FileLoggingEnabled = settings.failureFileLoggingEnabled,
                    EchoToUnityConsole = settings.failureEchoToUnityConsole,
                    IncludePublisherStackTrace = settings.failureIncludePublisherStackTrace,
                    IncludeEventPayload = settings.failureIncludeEventPayload,
                    FailFastOnHandlerException = settings.failureFailFastOnHandlerException,
                    FlushIntervalSeconds = Mathf.Max(0.1f, settings.failureFlushIntervalSeconds),
                    MaxFileSizeBytes = Mathf.Max(1, settings.failureMaxFileSizeMb) * 1024L * 1024L,
                    MaxRecordsPerSession = Mathf.Max(1, settings.failureMaxRecordsPerSession),
                    ImmediateFlushOnError = settings.failureImmediateFlushOnError,
                    RedactIdentifiers = settings.failureRedactIdentifiers
                };
                if(_hasFailureCaptureRuntimeOverride) {
                    _settings.FailureCaptureEnabled = _failureCaptureRuntimeOverride;
                }
                if(_hasFileLoggingRuntimeOverride) {
                    _settings.FileLoggingEnabled = _fileLoggingRuntimeOverride;
                }

                if(_initialized) {
                    ApplyWriterStateLocked();
                } else {
                    shouldInitialize = _settings.FailureCaptureEnabled && _settings.FileLoggingEnabled;
                }
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFileLoggingEnabled(bool enabled) {
            bool shouldInitialize;
            lock(Gate) {
                _settings.FileLoggingEnabled = enabled;
                _hasFileLoggingRuntimeOverride = true;
                _fileLoggingRuntimeOverride = enabled;
                if(_initialized) {
                    ApplyWriterStateLocked();
                    return;
                }

                shouldInitialize = enabled && _settings.FailureCaptureEnabled;
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFailureCaptureEnabled(bool enabled) {
            bool shouldInitialize;
            lock(Gate) {
                _settings.FailureCaptureEnabled = enabled;
                _hasFailureCaptureRuntimeOverride = true;
                _failureCaptureRuntimeOverride = enabled;
                shouldInitialize = enabled && _settings.FileLoggingEnabled && _initialized == false;
            }

            if(shouldInitialize) {
                EnsureInitialized();
            }
        }

        internal static void SetFailFastEnabled(bool enabled) {
            lock(Gate) {
                _settings.FailFastOnHandlerException = enabled;
            }
        }

        internal static string GetActiveLogPath() {
            lock(Gate) {
                return _activeLogPath;
            }
        }

        internal static void Tick() {
            lock(Gate) {
                if(_initialized == false || _writer == null) return;
                if(Time.unscaledTime < _nextFlushAt) return;
                _nextFlushAt = Time.unscaledTime + _settings.FlushIntervalSeconds;

                try {
                    _writer.Flush();
                } catch(Exception flushException) {
                    if(_internalErrorLogged) return;
                    _internalErrorLogged = true;
                    Debug.LogError($"[EventBusFailure] Failed flushing diagnostics log: {flushException}");
                }
            }
        }

        internal static void Shutdown() {
            lock(Gate) {
                if(_initialized == false) return;
                if(_sessionEndWritten == false) {
                    WriteSessionBoundaryLocked("session_end");
                    _sessionEndWritten = true;
                }

                try {
                    _writer?.Flush();
                    _writer?.Dispose();
                } catch(Exception closeException) {
                    if(_internalErrorLogged == false) {
                        _internalErrorLogged = true;
                        Debug.LogError($"[EventBusFailure] Failed closing diagnostics log: {closeException}");
                    }
                } finally {
                    _writer = null;
                    _activeLogPath = null;
                    ResetSessionStateLocked();
                }
            }
        }

        private static void EnsureInitialized() {
            if(_initialized) return;

            lock(Gate) {
                if(_initialized) return;

                _initialized = true;
                _sessionId = Guid.NewGuid().ToString("N")[..8];

                var isWindows = Application.platform == RuntimePlatform.WindowsEditor ||
                                Application.platform == RuntimePlatform.WindowsPlayer;
                if(isWindows == false) {
                    // Current product scope is Windows-first for local file diagnostics.
                    // Keep failure capture active, but disable file sink on other platforms.
                    if(_settings.FileLoggingEnabled) {
                        Debug.Log("[EventBusFailure] File logging is supported on Windows only; disabling file sink for this session.");
                    }
                    _settings.FileLoggingEnabled = false;
                }

                ApplyWriterStateLocked();

                if(_sessionStartWritten == false && _writer != null) {
                    WriteSessionBoundaryLocked("session_start");
                    _sessionStartWritten = true;
                }
            }
        }

        private static void CreateDriverLocked() {
            if(_driver != null) return;
            var go = new GameObject("EventBusFailureDiagnosticsDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _driver = go.AddComponent<EventBusFailureDiagnosticsDriver>();
        }

        private static void ApplyWriterStateLocked() {
            if(_settings.FileLoggingEnabled == false) {
                if(_writer != null) {
                    try {
                        _writer.Flush();
                        _writer.Dispose();
                    } catch {
                        // No-op: best effort close.
                    } finally {
                        _writer = null;
                    }
                }
                _activeLogPath = null;
                _sessionStartWritten = false;
                DestroyDriverLocked();

                return;
            }

            if(_writer != null) return;

            try {
                var directory = Path.Combine(Application.persistentDataPath, "Logs", "EventBus");
                Directory.CreateDirectory(directory);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var candidatePath = Path.Combine(directory, $"eventbus_{timestamp}_{_sessionId}.ndjson");

                var stream = new FileStream(candidatePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
                    AutoFlush = false
                };
                _activeLogPath = candidatePath;
                _recordCount = 0;
                _bytesWritten = 0;
                _maxFileSizeReached = false;
                _nextFlushAt = Time.unscaledTime + _settings.FlushIntervalSeconds;
                CreateDriverLocked();
                if(_sessionStartWritten == false) {
                    WriteSessionBoundaryLocked("session_start");
                    _sessionStartWritten = true;
                }
            } catch(Exception writerException) {
                if(_internalErrorLogged == false) {
                    _internalErrorLogged = true;
                    Debug.LogError($"[EventBusFailure] Failed opening diagnostics log file: {writerException}");
                }

                _writer = null;
                _activeLogPath = null;
            }
        }

        private static void DestroyDriverLocked() {
            if(_driver == null) return;
            var driver = _driver;
            _driver = null;
            if(driver != null) {
                UnityEngine.Object.Destroy(driver.gameObject);
            }
        }

        private static void ResetSessionStateLocked() {
            _initialized = false;
            _sessionId = null;
            _sessionStartWritten = false;
            _sessionEndWritten = false;
            _maxFileSizeReached = false;
            _recordCount = 0;
            _bytesWritten = 0;
            _nextFlushAt = 0f;
            _internalErrorLogged = false;
        }

        private static void WriteSessionBoundaryLocked(string recordType) {
            var boundary = new EventBusFailureRecord {
                recordType = recordType,
                sessionId = _sessionId,
                sequence = _recordCount + 1,
                timestampUtc = DateTime.UtcNow.ToString("o"),
                severity = "Info",
                scene = ResolveSceneName(),
                frame = Time.frameCount,
                networkRole = ResolveNetworkRole(),
                eventType = string.Empty,
                publisherContext = string.Empty,
                correlationId = string.Empty,
                parentCorrelationId = string.Empty,
                correlationDepth = 0,
                publisherEventContext = string.Empty,
                subscriberMethod = string.Empty,
                subscriberContext = string.Empty,
                exceptionType = string.Empty,
                exceptionMessage = string.Empty,
                exceptionStackTrace = string.Empty,
                eventPayload = string.Empty,
                publishStackTrace = string.Empty
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
            if(_writer == null) return;
            if(_maxFileSizeReached) return;
            if(_recordCount >= _settings.MaxRecordsPerSession) return;

            try {
                record.sequence = _recordCount + 1;
                var json = JsonUtility.ToJson(record);
                var byteCount = Encoding.UTF8.GetByteCount(json) + NewLineByteCount;
                if(_bytesWritten + byteCount > _settings.MaxFileSizeBytes) {
                    _maxFileSizeReached = true;
                    Debug.LogWarning(
                        $"[EventBusFailure] Log file size cap reached for session {_sessionId}. " +
                        "Further EventBus failure records will be dropped.");
                    return;
                }

                _writer.WriteLine(json);
                _bytesWritten += byteCount;
                _recordCount++;

                if(flushImmediately) {
                    _writer.Flush();
                }
            } catch(Exception writeException) {
                if(_internalErrorLogged) return;
                _internalErrorLogged = true;
                Debug.LogError($"[EventBusFailure] Failed writing diagnostics record: {writeException}");
            }
        }

        private static string ResolveSceneName() {
            var scene = SceneManager.GetActiveScene();
            return scene.IsValid() ? scene.name : "Unknown";
        }

        private static string ResolveNetworkRole() {
            var networkManager = NetworkManager.Singleton;
            if(networkManager == null) return "Offline";
            if(networkManager.IsServer && networkManager.IsClient) return "Host";
            if(networkManager.IsServer) return "Server";
            return networkManager.IsClient ? "Client" : "Offline";
        }

        private static string MaybeRedactString(string input, bool redactIdentifiers) {
            if(redactIdentifiers == false) return input;
            if(string.IsNullOrEmpty(input)) return input;
            return "<redacted>";
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
                return _settings;
            }
        }

        private static bool HasAnyConfiguredSink(FailureRuntimeSettings settings) {
            return settings.FileLoggingEnabled || settings.EchoToUnityConsole;
        }

        private static bool IsFileSinkActive() {
            lock(Gate) {
                return _writer != null;
            }
        }

        [Serializable]
        private sealed class EventBusFailureRecord {
            public string recordType;
            public string sessionId;
            public long sequence;
            public string timestampUtc;
            public string severity;
            public string scene;
            public int frame;
            public string networkRole;
            public string eventType;
            public string publisherMember;
            public string publisherFile;
            public int publisherLine;
            public string publisherContext;
            public string correlationId;
            public string parentCorrelationId;
            public int correlationDepth;
            public string publisherEventContext;
            public string subscriberMethod;
            public string subscriberDeclaringType;
            public string subscriberTargetType;
            public int subscriberInstanceId;
            public string subscriberObjectName;
            public string subscriberObjectPath;
            public string subscriberNetworkObjectId;
            public string subscriberOwnerClientId;
            public string subscriberContext;
            public string exceptionType;
            public string exceptionMessage;
            public string exceptionStackTrace;
            public string eventPayload;
            public string publishStackTrace;
        }

        private struct FailureRuntimeSettings {
            public bool FailureCaptureEnabled;
            public bool FileLoggingEnabled;
            public bool EchoToUnityConsole;
            public bool IncludePublisherStackTrace;
            public bool IncludeEventPayload;
            public bool FailFastOnHandlerException;
            public float FlushIntervalSeconds;
            public long MaxFileSizeBytes;
            public int MaxRecordsPerSession;
            public bool ImmediateFlushOnError;
            public bool RedactIdentifiers;

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
