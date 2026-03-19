using System.Text;
using MiniMCP;
using MiniMCP.Editor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "console_read",
        "Reads Unity console logs with filters. Returns compact entries by default (type + message). During active script recompilation a transient cancellation can happen; retry shortly.",
        "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"type\":{\"type\":\"string\",\"description\":\"Optional LogType filter: Error, Assert, Warning, Log, Exception\"},\"contains\":{\"type\":\"string\",\"description\":\"Case-insensitive text filter\"},\"includeStackTrace\":{\"type\":\"boolean\",\"default\":false},\"includeTimestamp\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}",
        Group = "Editor")]
    public sealed class ConsoleReadTool : MiniMcpMainThreadToolBase
    {
        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            var limit = 40;
            var includeStackTrace = false;
            var includeTimestamp = false;
            var type = string.Empty;
            var contains = string.Empty;

            MiniMcpJson.TryExtractIntProperty(argumentsJson, "limit", out limit);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "type", out type);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "contains", out contains);
            MiniMcpJson.TryExtractBoolProperty(argumentsJson, "includeStackTrace", out includeStackTrace);
            MiniMcpJson.TryExtractBoolProperty(argumentsJson, "includeTimestamp", out includeTimestamp);

            if (limit < 1)
            {
                limit = 1; 
            }
            else if (limit > 200)
            {
                limit = 200;
            }

            var query = new MiniMcpEditorState.ConsoleQuery
            {
                Limit = limit,
                Type = type ?? string.Empty,
                Contains = contains ?? string.Empty
            };

            var logs = MiniMcpEditorState.QueryLogs(query);
            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"items\":[");

            for (var i = 0; i < logs.Count; i++)
            {
                var log = logs[i];
                var message = ToSingleLineMessage(log.Message);
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"type\":\"");
                builder.Append(MiniMcpJson.EscapeJson(log.Type.ToString()));
                builder.Append("\",\"message\":\"");
                builder.Append(MiniMcpJson.EscapeJson(message));
                builder.Append('"');

                if (includeTimestamp)
                {
                    builder.Append(",\"timestampUtc\":\"");
                    builder.Append(MiniMcpJson.EscapeJson(log.TimestampUtc.ToString("O")));
                    builder.Append('"');
                }

                if (includeStackTrace)
                {
                    builder.Append(",\"stackTrace\":\"");
                    builder.Append(MiniMcpJson.EscapeJson(log.StackTrace));
                    builder.Append('"');
                }

                builder.Append('}');
            }

            builder.Append("]}");
            return MiniMcpToolCallResult.Ok(builder.ToString());
        }

        private static string ToSingleLineMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            var normalized = message.Replace("\r\n", "\n").Replace('\r', '\n');
            var firstLineEnd = normalized.IndexOf('\n');
            if (firstLineEnd < 0)
            {
                return normalized;
            }

            return normalized.Substring(0, firstLineEnd);
        }
    }
}
