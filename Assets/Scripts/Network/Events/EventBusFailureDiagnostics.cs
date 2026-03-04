using System;
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InitializeOnRuntimeLoad() {
            EnsureInitialized();
        }

        internal static bool RecordHandlerException(
            Type eventType,
            GameEvent gameEvent,
            Delegate handler,
            Exception exception,
            string callerMember,
            string callerFile,
            int callerLine) {
            EnsureInitialized();

            if(_settings.FailureCaptureEnabled == false) {
                return _settings.FailFastOnHandlerException;
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
                    subscriberObjectPath = BuildHierarchyPath(componentTarget.transform);
                }

                if(target is NetworkBehaviour networkTarget) {
                    subscriberNetworkObjectId = networkTarget.NetworkObject != null
                        ? networkTarget.NetworkObjectId.ToString()
                        : string.Empty;
                    subscriberOwnerClientId = networkTarget.OwnerClientId.ToString();
                }

                var publisherFile = string.IsNullOrEmpty(callerFile) ? string.Empty : Path.GetFileName(callerFile);
                var publisherContext = string.IsNullOrEmpty(publisherFile)
                    ? callerMember
                    : $"{publisherFile}:{callerLine} ({callerMember})";

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
                    subscriberMethod = subscriberMethod,
                    subscriberDeclaringType = subscriberDeclaringType,
                    subscriberTargetType = subscriberTargetType,
                    subscriberInstanceId = subscriberInstanceId,
                    subscriberObjectName = subscriberObjectName,
                    subscriberObjectPath = subscriberObjectPath,
                    subscriberNetworkObjectId = subscriberNetworkObjectId,
                    subscriberOwnerClientId = MaybeRedactIdentifier(subscriberOwnerClientId),
                    exceptionType = exception != null ? exception.GetType().FullName : "UnknownException",
                    exceptionMessage = exception != null ? exception.Message : "Unknown exception",
                    exceptionStackTrace = exception != null ? exception.StackTrace : string.Empty,
                    eventPayload = _settings.IncludeEventPayload && gameEvent != null ? gameEvent.ToString() : string.Empty,
                    publishStackTrace = _settings.IncludePublisherStackTrace ? new System.Diagnostics.StackTrace(2, true).ToString() : string.Empty
                };

                WriteRecord(record, flushImmediately: _settings.ImmediateFlushOnError);

                if(_settings.EchoToUnityConsole) {
                    Debug.LogError(
                        $"[EventBusFailure] event={record.eventType} publisher={record.publisherContext} " +
                        $"subscriber={record.subscriberDeclaringType}.{record.subscriberMethod} " +
                        $"exception={record.exceptionType}: {record.exceptionMessage} session={record.sessionId}");
                }
            } catch(Exception internalException) {
                if(_internalErrorLogged) {
                    return _settings.FailFastOnHandlerException;
                }

                _internalErrorLogged = true;
                Debug.LogError($"[EventBusFailure] Diagnostics pipeline failure: {internalException}");
            }

            return _settings.FailFastOnHandlerException;
        }

        internal static void ApplyLogSettings(EventBusLogSettings settings) {
            if(settings == null) return;

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

                if(_initialized) {
                    ApplyWriterStateLocked();
                }
            }
        }

        internal static void SetFileLoggingEnabled(bool enabled) {
            lock(Gate) {
                _settings.FileLoggingEnabled = enabled;
                if(_initialized) {
                    ApplyWriterStateLocked();
                }
            }
        }

        internal static void SetFailureCaptureEnabled(bool enabled) {
            lock(Gate) {
                _settings.FailureCaptureEnabled = enabled;
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
                    _settings.FileLoggingEnabled = false;
                }

                CreateDriverLocked();
                ApplyWriterStateLocked();

                if(_sessionStartWritten == false) {
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
                if(_writer == null) return;
                try {
                    _writer.Flush();
                    _writer.Dispose();
                } catch {
                    // No-op: best effort close.
                } finally {
                    _writer = null;
                }

                return;
            }

            if(_writer != null) return;

            try {
                var directory = Path.Combine(Application.persistentDataPath, "Logs", "EventBus");
                Directory.CreateDirectory(directory);

                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                _activeLogPath = Path.Combine(directory, $"eventbus_{timestamp}_{_sessionId}.ndjson");

                var stream = new FileStream(_activeLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) {
                    AutoFlush = false
                };
                _recordCount = 0;
                _bytesWritten = 0;
                _maxFileSizeReached = false;
                _nextFlushAt = Time.unscaledTime + _settings.FlushIntervalSeconds;
            } catch(Exception writerException) {
                if(_internalErrorLogged == false) {
                    _internalErrorLogged = true;
                    Debug.LogError($"[EventBusFailure] Failed opening diagnostics log file: {writerException}");
                }

                _writer = null;
            }
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
                subscriberMethod = string.Empty,
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
            if(_recordCount >= _settings.MaxRecordsPerSession) return;

            _recordCount++;

            if(_writer == null) return;
            if(_maxFileSizeReached) return;

            try {
                var json = JsonUtility.ToJson(record);
                var byteCount = Encoding.UTF8.GetByteCount(json) + 1;
                if(_bytesWritten + byteCount > _settings.MaxFileSizeBytes) {
                    _maxFileSizeReached = true;
                    Debug.LogWarning(
                        $"[EventBusFailure] Log file size cap reached for session {_sessionId}. " +
                        $"Further EventBus failure records will be dropped.");
                    return;
                }

                _writer.WriteLine(json);
                _bytesWritten += byteCount;

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

        private static string MaybeRedactIdentifier(string input) {
            if(_settings.RedactIdentifiers == false) return input;
            if(string.IsNullOrEmpty(input)) return input;
            return "<redacted>";
        }

        private static string BuildHierarchyPath(Transform transform) {
            if(transform == null) return string.Empty;
            var path = transform.name;
            var current = transform.parent;
            while(current != null) {
                path = current.name + "/" + path;
                current = current.parent;
            }

            return path;
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
            public string subscriberMethod;
            public string subscriberDeclaringType;
            public string subscriberTargetType;
            public int subscriberInstanceId;
            public string subscriberObjectName;
            public string subscriberObjectPath;
            public string subscriberNetworkObjectId;
            public string subscriberOwnerClientId;
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
