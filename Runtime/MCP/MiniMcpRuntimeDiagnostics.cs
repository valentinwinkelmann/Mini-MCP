using System;

namespace MiniMCP
{
    public static class MiniMcpRuntimeDiagnostics
    {
        private static readonly object Gate = new object();
        private static bool isCompiling;
        private static int compileErrorCount;
        private static DateTime? lastCompileStartedUtc;
        private static DateTime? lastCompileFinishedUtc;
        private static bool isPlaying;
        private static bool isPlayingOrWillChangePlaymode;
        private static string playModeState;
        private static DateTime? lastPlayModeStateChangedUtc;
        private static string activeSceneName;
        private static string activeScenePath;
        private static int activeSceneBuildIndex;
        private static bool isSceneDirty;
        private static bool isSceneLoaded;

        public readonly struct Snapshot
        {
            public Snapshot(
                bool isCompiling,
                int compileErrorCount,
                DateTime? lastCompileStartedUtc,
                DateTime? lastCompileFinishedUtc,
                bool isPlaying,
                bool isPlayingOrWillChangePlaymode,
                string playModeState,
                DateTime? lastPlayModeStateChangedUtc,
                string activeSceneName,
                string activeScenePath,
                int activeSceneBuildIndex,
                bool isSceneDirty,
                bool isSceneLoaded)
            {
                this.IsCompiling = isCompiling;
                this.CompileErrorCount = compileErrorCount;
                this.LastCompileStartedUtc = lastCompileStartedUtc;
                this.LastCompileFinishedUtc = lastCompileFinishedUtc;
                this.IsPlaying = isPlaying;
                this.IsPlayingOrWillChangePlaymode = isPlayingOrWillChangePlaymode;
                this.PlayModeState = playModeState ?? string.Empty;
                this.LastPlayModeStateChangedUtc = lastPlayModeStateChangedUtc;
                this.ActiveSceneName = activeSceneName ?? string.Empty;
                this.ActiveScenePath = activeScenePath ?? string.Empty;
                this.ActiveSceneBuildIndex = activeSceneBuildIndex;
                this.IsSceneDirty = isSceneDirty;
                this.IsSceneLoaded = isSceneLoaded;
            }

            public bool IsCompiling { get; }
            public int CompileErrorCount { get; }
            public DateTime? LastCompileStartedUtc { get; }
            public DateTime? LastCompileFinishedUtc { get; }
            public bool IsPlaying { get; }
            public bool IsPlayingOrWillChangePlaymode { get; }
            public string PlayModeState { get; }
            public DateTime? LastPlayModeStateChangedUtc { get; }
            public string ActiveSceneName { get; }
            public string ActiveScenePath { get; }
            public int ActiveSceneBuildIndex { get; }
            public bool IsSceneDirty { get; }
            public bool IsSceneLoaded { get; }
        }

        public static Snapshot GetSnapshot()
        {
            lock (Gate)
            {
                return new Snapshot(
                    isCompiling,
                    compileErrorCount,
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
        }

        public static void UpdateFromEditor(
            bool compiling,
            int errorCount,
            DateTime? compileStartedUtc,
            DateTime? compileFinishedUtc,
            bool playing,
            bool playingOrWillChangePlaymode,
            string currentPlayModeState,
            DateTime? playModeStateChangedUtc,
            string sceneName,
            string scenePath,
            int sceneBuildIndex,
            bool sceneDirty,
            bool sceneLoaded)
        {
            lock (Gate)
            {
                isCompiling = compiling;
                compileErrorCount = errorCount < 0 ? 0 : errorCount;
                lastCompileStartedUtc = compileStartedUtc;
                lastCompileFinishedUtc = compileFinishedUtc;
                isPlaying = playing;
                isPlayingOrWillChangePlaymode = playingOrWillChangePlaymode;
                playModeState = currentPlayModeState ?? string.Empty;
                lastPlayModeStateChangedUtc = playModeStateChangedUtc;
                activeSceneName = sceneName ?? string.Empty;
                activeScenePath = scenePath ?? string.Empty;
                activeSceneBuildIndex = sceneBuildIndex;
                isSceneDirty = sceneDirty;
                isSceneLoaded = sceneLoaded;
            }
        }
    }
}
