using System;
using MiniMCP;
using UnityEditor;
using UnityEditor.Compilation;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "request_recompile",
        "Requests Unity script recompilation immediately. After triggering, short transient MCP disconnects are expected; retry tool calls after a few seconds.",
        Group = "Editor",
        SupportsAwait = true,
        AwaitKind = "recompile",
        DefaultAwaitTimeoutMs = 60000,
        MaxAwaitTimeoutMs = 600000)]
    public sealed class RequestRecompileTool : MiniMcpMainThreadToolBase
    {
        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            var attribute = (MiniMcpToolAttribute)Attribute.GetCustomAttribute(typeof(RequestRecompileTool), typeof(MiniMcpToolAttribute));
            var effectiveTimeoutMs = ResolveEffectiveTimeoutMs(argumentsJson, attribute);

            if (EditorApplication.isCompiling)
            {
                return MiniMcpToolCallResult.Ok("{\"status\":\"busy_compiling\",\"retryable\":true,\"retryAfterMs\":2000,\"message\":\"Unity is already compiling. Retry in a moment.\"}");
            }

            var operation = MiniMCP.Editor.MiniMcpAwaitedOperationStore.BeginOrReuseOperation(
                "request_recompile",
                attribute != null && !string.IsNullOrWhiteSpace(attribute.AwaitKind) ? attribute.AwaitKind : "recompile",
                effectiveTimeoutMs);

            CompilationPipeline.RequestScriptCompilation();
            return MiniMcpToolCallResult.Ok(
                "{\"status\":\"await_started\","
                + "\"operationId\":\"" + MiniMcpJson.EscapeJson(operation.OperationId) + "\"," 
                + "\"kind\":\"recompile\","
                + "\"timeoutMs\":" + effectiveTimeoutMs + ","
                + "\"outcomeKnown\":false,"
                + "\"message\":\"Recompile started. MCP is expected to await the terminal completion state.\"}");
        }

        private static int ResolveEffectiveTimeoutMs(string argumentsJson, MiniMcpToolAttribute attribute)
        {
            var defaultTimeoutMs = attribute != null && attribute.DefaultAwaitTimeoutMs > 0
                ? attribute.DefaultAwaitTimeoutMs
                : MiniMcpToolAttribute.DefaultAwaitTimeoutMsValue;
            var maxTimeoutMs = attribute != null && attribute.MaxAwaitTimeoutMs > 0
                ? attribute.MaxAwaitTimeoutMs
                : MiniMcpToolAttribute.DefaultMaxAwaitTimeoutMs;

            if (!MiniMcpJson.TryExtractIntProperty(argumentsJson ?? "{}", "timeoutMs", out var requestedTimeoutMs))
            {
                return defaultTimeoutMs;
            }

            if (requestedTimeoutMs <= 0)
            {
                return defaultTimeoutMs;
            }

            return Math.Min(requestedTimeoutMs, maxTimeoutMs);
        }
    }
}
