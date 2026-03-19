using System;
using System.Threading;
using MiniMCP;
using MiniMCP.Editor;
using UnityEditor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "playmode_control",
        "Starts or stops Unity Play Mode and returns the resulting editor status. Useful for running the game loop and exiting it again from MCP.",
        Group = "Editor")]
    public sealed class PlayModeControlTool : MiniMcpTypedTool<PlayModeControlTool.Arguments>, IMiniMcpToolThreadingValidated
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Requested playmode action.", Required = true, EnumValues = new[] { "start", "stop", "toggle" })]
            public string action;

            [MiniMcpSchemaProperty(Description = "How long to wait for the requested transition before returning transition_pending.", Minimum = 0, Maximum = 30000)]
            public int timeoutMs;
        }

        public override MiniMcpToolCallResult Execute(string argumentsJson)
        {
            var action = string.Empty;
            var timeoutMs = 5000;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "action", out action);
            MiniMcpJson.TryExtractIntProperty(argumentsJson, "timeoutMs", out timeoutMs);

            if (timeoutMs < 0)
            {
                timeoutMs = 0;
            }
            else if (timeoutMs > 30000)
            {
                timeoutMs = 30000;
            }

            var normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedAction != "start" && normalizedAction != "stop" && normalizedAction != "toggle")
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"Invalid action. Use 'start', 'stop', or 'toggle'.\"}");
            }

            var initial = MiniMcpEditorState.GetStatusSnapshot();
            if (initial.IsCompiling)
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"busy_compiling\",\"retryable\":true,\"retryAfterMs\":2000,\"message\":\"Unity is compiling. Retry Play Mode control after compilation finishes.\"}");
            }

            var targetIsPlaying = normalizedAction == "start"
                || (normalizedAction == "toggle" && !initial.IsPlaying);

            if (normalizedAction == "start" && initial.IsPlaying && !initial.IsPlayingOrWillChangePlaymode)
            {
                return MiniMcpToolCallResult.Ok(BuildResultJson("already_in_requested_state", normalizedAction, initial));
            }

            if (normalizedAction == "stop" && !initial.IsPlaying && !initial.IsPlayingOrWillChangePlaymode)
            {
                return MiniMcpToolCallResult.Ok(BuildResultJson("already_in_requested_state", normalizedAction, initial));
            }

            string dispatchError;
            var dispatched = MiniMcpEditorThread.Invoke(() =>
            {
                switch (normalizedAction)
                {
                    case "start":
                        EditorApplication.EnterPlaymode();
                        break;
                    case "stop":
                        EditorApplication.ExitPlaymode();
                        break;
                    default:
                        if (EditorApplication.isPlaying)
                        {
                            EditorApplication.ExitPlaymode();
                        }
                        else
                        {
                            EditorApplication.EnterPlaymode();
                        }
                        break;
                }
            }, TimeSpan.FromSeconds(5), out dispatchError);

            if (!dispatched)
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(dispatchError) + "\"}");
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            var snapshot = MiniMcpEditorState.GetStatusSnapshot();
            while (timeoutMs > 0 && DateTime.UtcNow < deadline)
            {
                snapshot = MiniMcpEditorState.GetStatusSnapshot();
                if (HasReachedRequestedState(normalizedAction, targetIsPlaying, snapshot))
                {
                    return MiniMcpToolCallResult.Ok(BuildResultJson("completed", normalizedAction, snapshot));
                }

                Thread.Sleep(50);
            }

            snapshot = MiniMcpEditorState.GetStatusSnapshot();
            return MiniMcpToolCallResult.Ok(BuildResultJson("transition_pending", normalizedAction, snapshot));
        }

        private static bool HasReachedRequestedState(string action, bool targetIsPlaying, MiniMcpEditorState.EditorStatusSnapshot snapshot)
        {
            if (action == "start")
            {
                return snapshot.IsPlaying && string.Equals(snapshot.PlayModeState, "play", StringComparison.OrdinalIgnoreCase);
            }

            if (action == "stop")
            {
                return !snapshot.IsPlaying && string.Equals(snapshot.PlayModeState, "edit", StringComparison.OrdinalIgnoreCase);
            }

            if (targetIsPlaying)
            {
                return snapshot.IsPlaying && string.Equals(snapshot.PlayModeState, "play", StringComparison.OrdinalIgnoreCase);
            }

            return !snapshot.IsPlaying && string.Equals(snapshot.PlayModeState, "edit", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildResultJson(string status, string action, MiniMcpEditorState.EditorStatusSnapshot snapshot)
        {
            return "{\"status\":\""
                + MiniMcpJson.EscapeJson(status)
                + "\",\"action\":\""
                + MiniMcpJson.EscapeJson(action)
                + "\",\"isPlaying\":"
                + (snapshot.IsPlaying ? "true" : "false")
                + ",\"isPlayingOrWillChangePlaymode\":"
                + (snapshot.IsPlayingOrWillChangePlaymode ? "true" : "false")
                + ",\"playModeState\":\""
                + MiniMcpJson.EscapeJson(snapshot.PlayModeState ?? string.Empty)
                + "\",\"sceneName\":\""
                + MiniMcpJson.EscapeJson(snapshot.ActiveSceneName ?? string.Empty)
                + "\",\"scenePath\":\""
                + MiniMcpJson.EscapeJson(snapshot.ActiveScenePath ?? string.Empty)
                + "\"}";
        }
    }
}