using System;
using System.Collections.Generic;
using System.Text;
using MiniMCP;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MiniMCP.Editor
{
    [InitializeOnLoad]
    internal static class MiniMcpEditorState
    {
        private const string SessionCompileStartedUtcKey = "MiniMCP.EditorState.LastCompileStartedUtc";
        private const string SessionCompileFinishedUtcKey = "MiniMCP.EditorState.LastCompileFinishedUtc";
        private const string SessionCompilePendingReadyKey = "MiniMCP.EditorState.CompilePendingReady";
        private const int MaxLogs = 2000;
        private const int MaxCompileErrors = 400;
        private static readonly object Gate = new object();
        private static readonly List<LogRecord> Logs = new List<LogRecord>();
        private static readonly List<CompileErrorRecord> CompileErrors = new List<CompileErrorRecord>();

        private static bool isCompiling;
        private static bool isPlaying;
        private static bool isPlayingOrWillChangePlaymode;
        private static string playModeState = "edit";
        private static DateTime? lastCompileStartedUtc;
        private static DateTime? lastCompileFinishedUtc;
        private static DateTime? lastPlayModeStateChangedUtc;
        private static string activeSceneName = string.Empty;
        private static string activeScenePath = string.Empty;
        private static int activeSceneBuildIndex = -1;
        private static bool isSceneDirty;
        private static bool isSceneLoaded;

        static MiniMcpEditorState()
        {
            isCompiling = EditorApplication.isCompiling;
            isPlaying = EditorApplication.isPlaying;
            isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            lastCompileStartedUtc = ReadSessionDateTime(SessionCompileStartedUtcKey);
            lastCompileFinishedUtc = ReadSessionDateTime(SessionCompileFinishedUtcKey);
            RefreshSceneState();
            PushRuntimeDiagnostics();
            NotifyRelayState(isCompiling ? "compiling" : "ready", "editor_initialized");
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
        }

        public static bool IsCompiling
        {
            get
            {
                lock (Gate)
                {
                    return isCompiling;
                }
            }
        }

        public static IReadOnlyList<LogRecord> QueryLogs(ConsoleQuery query)
        {
            lock (Gate)
            {
                var result = new List<LogRecord>();
                for (var i = Logs.Count - 1; i >= 0; i--)
                {
                    var entry = Logs[i];
                    if (!MatchesLog(entry, query))
                    {
                        continue;
                    }

                    result.Add(entry);
                    if (result.Count >= query.Limit)
                    {
                        break;
                    }
                }

                return result;
            }
        }

        public static IReadOnlyList<CompileErrorRecord> QueryCompileErrors(CompileErrorQuery query)
        {
            lock (Gate)
            {
                var result = new List<CompileErrorRecord>();
                for (var i = CompileErrors.Count - 1; i >= 0; i--)
                {
                    var entry = CompileErrors[i];
                    if (!MatchesCompileError(entry, query))
                    {
                        continue;
                    }

                    result.Add(entry);
                    if (result.Count >= query.Limit)
                    {
                        break;
                    }
                }

                return result;
            }
        }

        public static EditorStatusSnapshot GetStatusSnapshot()
        {
            lock (Gate)
            {
                return new EditorStatusSnapshot
                {
                    IsCompiling = isCompiling,
                    CompileErrorCount = CompileErrors.Count,
                    LastCompileStartedUtc = lastCompileStartedUtc,
                    LastCompileFinishedUtc = lastCompileFinishedUtc,
                    IsPlaying = isPlaying,
                    IsPlayingOrWillChangePlaymode = isPlayingOrWillChangePlaymode,
                    PlayModeState = playModeState ?? string.Empty,
                    LastPlayModeStateChangedUtc = lastPlayModeStateChangedUtc,
                    ActiveSceneName = activeSceneName ?? string.Empty,
                    ActiveScenePath = activeScenePath ?? string.Empty,
                    ActiveSceneBuildIndex = activeSceneBuildIndex,
                    IsSceneDirty = isSceneDirty,
                    IsSceneLoaded = isSceneLoaded
                };
            }
        }

        public static string BuildStatusJson()
        {
            var snapshot = GetStatusSnapshot();
            return BuildStatusJson(snapshot);
        }

        private static void OnEditorUpdate()
        {
            lock (Gate)
            {
                isCompiling = EditorApplication.isCompiling;
                isPlaying = EditorApplication.isPlaying;
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
                RefreshSceneState();
                PushRuntimeDiagnostics();
            }

            TryFinalizeCompileReady("editor_update");
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            lock (Gate)
            {
                isPlaying = EditorApplication.isPlaying;
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
                playModeState = ConvertPlayModeState(change);
                lastPlayModeStateChangedUtc = DateTime.UtcNow;
                RefreshSceneState();
                PushRuntimeDiagnostics();
            }
        }

        private static void OnCompilationStarted(object context)
        {
            lock (Gate)
            {
                isCompiling = true;
                lastCompileStartedUtc = DateTime.UtcNow;
                lastCompileFinishedUtc = null;
                CompileErrors.Clear();
                PersistCompileStateUnderLock(isCompileReadyPending: true);
                PushRuntimeDiagnostics();
            }

            NotifyRelayState("compiling", "compilation_started");
        }

        private static void OnCompilationFinished(object context)
        {
            lock (Gate)
            {
                isCompiling = EditorApplication.isCompiling;
                if (!isCompiling)
                {
                    lastCompileFinishedUtc = DateTime.UtcNow;
                }

                PersistCompileStateUnderLock(isCompileReadyPending: EditorApplication.isCompiling || SessionState.GetBool(SessionCompilePendingReadyKey, false));
                PushRuntimeDiagnostics();
            }

            NotifyRelayState(EditorApplication.isCompiling ? "compiling" : "ready", "compilation_finished");

            if (!EditorApplication.isCompiling && CompileErrors.Count > 0)
            {
                MiniMcpAwaitedOperationStore.TryCompleteRunningOperation(
                    "recompile",
                    "completed_with_errors",
                    true,
                    "Unity finished recompiling with compile errors.",
                    "Compile errors were reported during recompilation.");
            }

            if (!EditorApplication.isCompiling)
            {
                MiniMcpEditorService.EnsureRunning("compilation_finished");
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            SessionState.SetBool(SessionCompilePendingReadyKey, true);
            NotifyRelayState("compiling", "before_assembly_reload");
        }

        private static void OnAfterAssemblyReload()
        {
            NotifyRelayState(EditorApplication.isCompiling ? "compiling" : "ready", "after_assembly_reload");

            TryFinalizeCompileReady("after_assembly_reload");

            if (!EditorApplication.isCompiling)
            {
                MiniMcpEditorService.EnsureRunning("after_assembly_reload");
            }
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            lock (Gate)
            {
                for (var i = 0; i < messages.Length; i++)
                {
                    var msg = messages[i];
                    if (msg.type != CompilerMessageType.Error)
                    {
                        continue;
                    }

                    CompileErrors.Add(new CompileErrorRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        AssemblyPath = assemblyPath ?? string.Empty,
                        File = msg.file ?? string.Empty,
                        Line = msg.line,
                        Column = msg.column,
                        Message = msg.message ?? string.Empty
                    });
                }

                if (CompileErrors.Count > MaxCompileErrors)
                {
                    CompileErrors.RemoveRange(0, CompileErrors.Count - MaxCompileErrors);
                }

                PushRuntimeDiagnostics();
            }
        }

        private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            lock (Gate)
            {
                Logs.Add(new LogRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    Type = type,
                    Message = condition ?? string.Empty,
                    StackTrace = stackTrace ?? string.Empty
                });

                if (Logs.Count > MaxLogs)
                {
                    Logs.RemoveRange(0, Logs.Count - MaxLogs);
                }
            }
        }

        private static bool MatchesLog(LogRecord entry, ConsoleQuery query)
        {
            if (!string.IsNullOrEmpty(query.Type) && !entry.Type.ToString().Equals(query.Type, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrEmpty(query.Contains))
            {
                return true;
            }

            return entry.Message.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.StackTrace.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool MatchesCompileError(CompileErrorRecord entry, CompileErrorQuery query)
        {
            if (string.IsNullOrEmpty(query.Contains))
            {
                return true;
            }

            return entry.Message.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.File.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.AssemblyPath.IndexOf(query.Contains, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void PushRuntimeDiagnostics()
        {
            MiniMcpRuntimeDiagnostics.UpdateFromEditor(
                isCompiling,
                CompileErrors.Count,
                lastCompileStartedUtc,
                lastCompileFinishedUtc,
                isPlaying,
                isPlayingOrWillChangePlaymode,
                playModeState,
                lastPlayModeStateChangedUtc,
                activeSceneName,
                activeScenePath,
                activeSceneBuildIndex,
                isSceneDirty,
                isSceneLoaded);
        }

        private static string BuildStatusJson(EditorStatusSnapshot snapshot)
        {
            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"isCompiling\":");
            builder.Append(snapshot.IsCompiling ? "true" : "false");
            builder.Append(",\"compileErrorCount\":");
            builder.Append(snapshot.CompileErrorCount);
            builder.Append(",\"isPlaying\":");
            builder.Append(snapshot.IsPlaying ? "true" : "false");
            builder.Append(",\"isPlayingOrWillChangePlaymode\":");
            builder.Append(snapshot.IsPlayingOrWillChangePlaymode ? "true" : "false");
            builder.Append(",\"playModeState\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.PlayModeState ?? string.Empty));
            builder.Append("\"");
            builder.Append(",\"activeScene\":{");
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.ActiveSceneName ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"path\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.ActiveScenePath ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"buildIndex\":");
            builder.Append(snapshot.ActiveSceneBuildIndex);
            builder.Append(",\"isDirty\":");
            builder.Append(snapshot.IsSceneDirty ? "true" : "false");
            builder.Append(",\"isLoaded\":");
            builder.Append(snapshot.IsSceneLoaded ? "true" : "false");
            builder.Append("}");
            builder.Append(",\"lastCompileStartedUtc\":");
            builder.Append(snapshot.LastCompileStartedUtc.HasValue
                ? "\"" + MiniMcpJson.EscapeJson(snapshot.LastCompileStartedUtc.Value.ToString("O")) + "\""
                : "null");
            builder.Append(",\"lastCompileFinishedUtc\":");
            builder.Append(snapshot.LastCompileFinishedUtc.HasValue
                ? "\"" + MiniMcpJson.EscapeJson(snapshot.LastCompileFinishedUtc.Value.ToString("O")) + "\""
                : "null");
            builder.Append(",\"lastPlayModeStateChangedUtc\":");
            builder.Append(snapshot.LastPlayModeStateChangedUtc.HasValue
                ? "\"" + MiniMcpJson.EscapeJson(snapshot.LastPlayModeStateChangedUtc.Value.ToString("O")) + "\""
                : "null");
            builder.Append("}");
            return builder.ToString();
        }

        private static void RefreshSceneState()
        {
            var scene = EditorSceneManager.GetActiveScene();
            activeSceneName = scene.name ?? string.Empty;
            activeScenePath = scene.path ?? string.Empty;
            activeSceneBuildIndex = scene.buildIndex;
            isSceneDirty = scene.isDirty;
            isSceneLoaded = scene.isLoaded;
        }

        private static string ConvertPlayModeState(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    return "exiting_edit_mode";
                case PlayModeStateChange.EnteredPlayMode:
                    return "play";
                case PlayModeStateChange.ExitingPlayMode:
                    return "exiting_play_mode";
                case PlayModeStateChange.EnteredEditMode:
                    return "edit";
                default:
                    return EditorApplication.isPlaying ? "play" : "edit";
            }
        }

        private static void NotifyRelayState(string state, string reason)
        {
            MiniMcpRelayService.NotifyRelayStatus(state, reason);
        }

        private static void TryFinalizeCompileReady(string reason)
        {
            if (EditorApplication.isCompiling || !SessionState.GetBool(SessionCompilePendingReadyKey, false))
            {
                return;
            }

            lock (Gate)
            {
                isCompiling = false;
                if (!lastCompileFinishedUtc.HasValue)
                {
                    lastCompileFinishedUtc = DateTime.UtcNow;
                }

                PersistCompileStateUnderLock(isCompileReadyPending: false);
                PushRuntimeDiagnostics();
            }

            NotifyRelayState("ready", reason);
        }

        private static void PersistCompileStateUnderLock(bool isCompileReadyPending)
        {
            WriteSessionDateTime(SessionCompileStartedUtcKey, lastCompileStartedUtc);
            WriteSessionDateTime(SessionCompileFinishedUtcKey, lastCompileFinishedUtc);
            SessionState.SetBool(SessionCompilePendingReadyKey, isCompileReadyPending);
        }

        private static DateTime? ReadSessionDateTime(string key)
        {
            string raw = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static void WriteSessionDateTime(string key, DateTime? value)
        {
            SessionState.SetString(key, value.HasValue ? value.Value.ToString("O") : string.Empty);
        }

        public sealed class LogRecord
        {
            public DateTime TimestampUtc;
            public LogType Type;
            public string Message;
            public string StackTrace;
        }

        public sealed class CompileErrorRecord
        {
            public DateTime TimestampUtc;
            public string AssemblyPath;
            public string File;
            public int Line;
            public int Column;
            public string Message;
        }

        public struct ConsoleQuery
        {
            public int Limit;
            public string Type;
            public string Contains;
        }

        public struct CompileErrorQuery
        {
            public int Limit;
            public string Contains;
        }

        public struct EditorStatusSnapshot
        {
            public bool IsCompiling;
            public int CompileErrorCount;
            public DateTime? LastCompileStartedUtc;
            public DateTime? LastCompileFinishedUtc;
            public bool IsPlaying;
            public bool IsPlayingOrWillChangePlaymode;
            public string PlayModeState;
            public DateTime? LastPlayModeStateChangedUtc;
            public string ActiveSceneName;
            public string ActiveScenePath;
            public int ActiveSceneBuildIndex;
            public bool IsSceneDirty;
            public bool IsSceneLoaded;
        }
    }
}
