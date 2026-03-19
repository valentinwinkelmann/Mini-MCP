using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MiniMCP
{
    public sealed class MiniMcpServer : IDisposable
    {
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool isRunning;

        public bool IsRunning => this.isRunning;
        public int Port { get; private set; }
        public string LastStartErrorMessage { get; private set; }

        public event Action<string> LogReceived;

        public bool Start(int port, bool logFailure = true)
        {
            if (this.isRunning)
            {
                this.Log("Server is already running.");
                return false;
            }

            TcpListener localListener = null;
            try
            {
                this.LastStartErrorMessage = null;
                localListener = new TcpListener(IPAddress.Loopback, port);
                localListener.Start();

                MiniMcpToolRegistry.ReloadTools();

                this.Port = port;
                this.listener = localListener;
                this.isRunning = true;
                this.acceptThread = new Thread(this.AcceptLoop)
                {
                    IsBackground = true,
                    Name = "MiniMCP Accept Loop"
                };
                this.acceptThread.Start();

                this.Log($"MiniMCP server started on http://127.0.0.1:{port}/mcp");
                this.Log("MCP methods: initialize, notifications/initialized, tools/list, tools/call");
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    localListener?.Stop();
                    this.listener?.Stop();
                }
                catch
                {
                    // Best effort cleanup.
                }

                try
                {
                    if (this.acceptThread != null && this.acceptThread.IsAlive)
                    {
                        this.acceptThread.Join(250);
                    }
                }
                catch
                {
                    // Best effort cleanup.
                }

                this.acceptThread = null;
                this.listener = null;
                this.Port = 0;
                this.LastStartErrorMessage = BuildDetailedStartError(ex);
                if (logFailure)
                {
                    this.Log($"Start failed: {this.LastStartErrorMessage}");
                }
                this.isRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (!this.isRunning)
            {
                this.acceptThread = null;
                this.listener = null;
                this.Port = 0;
                return;
            }

            this.isRunning = false;

            try
            {
                this.listener?.Stop();
            }
            catch (Exception ex)
            {
                this.Log($"Stop warning: {ex.Message}");
            }

            try
            {
                if (this.acceptThread != null && this.acceptThread.IsAlive)
                {
                    this.acceptThread.Join(500);
                }
            }
            catch (Exception ex)
            {
                this.Log($"Thread join warning: {ex.Message}");
            }

            this.acceptThread = null;
            this.listener = null;
            this.Port = 0;
            this.Log("MiniMCP server stopped.");
        }

        private static string BuildDetailedStartError(Exception ex)
        {
            if (ex is SocketException socketException)
            {
                return socketException.Message
                    + " (SocketErrorCode=" + socketException.SocketErrorCode
                    + ", NativeErrorCode=" + socketException.ErrorCode + ")";
            }

            return ex.Message;
        }

        private void AcceptLoop()
        {
            while (this.isRunning)
            {
                TcpClient client = null;

                try
                {
                    client = this.listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(this.HandleClient, client);
                }
                catch (SocketException)
                {
                    if (this.isRunning)
                    {
                        this.Log("Listener error while waiting for requests.");
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Listener was closed while waiting.
                }
                catch (Exception ex)
                {
                    this.Log($"Accept loop error: {ex.Message}");
                }
            }
        }

        private void HandleClient(object state)
        {
            using (var client = (TcpClient)state)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { NewLine = "\r\n" })
            {
                try
                {
                    var requestLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(requestLine))
                    {
                        this.WriteHttpResponse(writer, 400, "Bad Request", "text/plain", "Bad Request");
                        return;
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2)
                    {
                        this.WriteHttpResponse(writer, 400, "Bad Request", "text/plain", "Bad Request");
                        return;
                    }

                    var method = parts[0];
                    var path = parts[1];
                    var contentLength = 0;

                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = reader.ReadLine()))
                    {
                        const string contentLengthHeader = "Content-Length:";
                        if (headerLine.StartsWith(contentLengthHeader, StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = headerLine.Substring(contentLengthHeader.Length).Trim();
                            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
                        }
                    }

                    if (!path.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
                    {
                        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/health", StringComparison.OrdinalIgnoreCase))
                        {
                            var diagnostics = MiniMcpRuntimeDiagnostics.GetSnapshot();
                            var healthJson = "{\"ok\":true,\"isRunning\":true,\"isCompiling\":" + (diagnostics.IsCompiling ? "true" : "false") + ",\"isPlaying\":" + (diagnostics.IsPlaying ? "true" : "false") + ",\"playModeState\":\"" + MiniMcpJson.EscapeJson(diagnostics.PlayModeState) + "\",\"sceneName\":\"" + MiniMcpJson.EscapeJson(diagnostics.ActiveSceneName) + "\",\"scenePath\":\"" + MiniMcpJson.EscapeJson(diagnostics.ActiveScenePath) + "\",\"port\":" + this.Port + "}";
                            this.WriteHttpResponse(writer, 200, "OK", "application/json", healthJson);
                            return;
                        }

                        this.WriteHttpResponse(writer, 404, "Not Found", "text/plain", "Not Found");
                        return;
                    }

                    if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                    {
                        this.WriteHttpResponse(writer, 405, "Method Not Allowed", "text/plain", "Method Not Allowed");
                        return;
                    }

                    if (contentLength < 0)
                    {
                        this.WriteHttpResponse(writer, 400, "Bad Request", "text/plain", "Invalid Content-Length");
                        return;
                    }

                    var body = string.Empty;
                    if (contentLength > 0)
                    {
                        body = ReadRequestBody(reader, contentLength);
                    }

                    var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    this.Log($"Request from {remote}: {TrimForLog(body)}");

                    if (!this.TryProcessRpc(body, out var rpcResponse, out var hasResponse))
                    {
                        this.WriteHttpResponse(writer, 400, "Bad Request", "application/json", this.MakeErrorResponse("null", -32700, "Parse error"));
                        return;
                    }

                    if (!hasResponse)
                    {
                        this.WriteHttpResponse(writer, 202, "Accepted", "application/json", string.Empty);
                        return;
                    }

                    this.WriteHttpResponse(writer, 200, "OK", "application/json", rpcResponse);
                }
                catch (Exception ex)
                {
                    this.Log($"Request handling error: {ex.Message}");

                    try
                    {
                        this.WriteHttpResponse(writer, 500, "Internal Server Error", "application/json", this.MakeErrorResponse("null", -32603, "Internal error"));
                    }
                    catch
                    {
                        // Ignore secondary write errors.
                    }
                }
            }
        }

        private bool TryProcessRpc(string requestJson, out string responseJson, out bool hasResponse)
        {
            responseJson = null;
            hasResponse = true;

            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return false;
            }

            if (!MiniMcpJson.TryExtractStringProperty(requestJson, "method", out var method))
            {
                return false;
            }

            var idRaw = ExtractIdRaw(requestJson);

            if (method.Equals("notifications/initialized", StringComparison.OrdinalIgnoreCase))
            {
                hasResponse = false;
                return true;
            }

            if (method.Equals("initialize", StringComparison.OrdinalIgnoreCase))
            {
                const string result = "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{\"listChanged\":true}},\"serverInfo\":{\"name\":\"mini-unity-mcp\",\"version\":\"0.2.0\"}}";
                responseJson = this.MakeResultResponse(idRaw, result);
                return true;
            }

            if (method.Equals("tools/list", StringComparison.OrdinalIgnoreCase))
            {
                var result = BuildToolsListResult();
                responseJson = this.MakeResultResponse(idRaw, result);
                return true;
            }

            if (method.Equals("tools/call", StringComparison.OrdinalIgnoreCase))
            {
                if (!MiniMcpJson.TryExtractStringProperty(requestJson, "name", out var toolName))
                {
                    responseJson = this.MakeErrorResponse(idRaw, -32602, "Missing tool name");
                    return true;
                }

                var diagnostics = MiniMcpRuntimeDiagnostics.GetSnapshot();
                if (diagnostics.IsCompiling && !toolName.Equals("request_recompile", StringComparison.OrdinalIgnoreCase))
                {
                    responseJson = this.MakeToolTextResponse(
                        idRaw,
                        "{\"status\":\"busy_compiling\",\"retryable\":true,\"retryAfterMs\":2000,\"message\":\"Unity is compiling right now. Retry this request in a moment.\"}",
                        true);
                    return true;
                }

                if (!MiniMcpJson.TryExtractArgumentsObject(requestJson, out var argumentsJson))
                {
                    responseJson = this.MakeErrorResponse(idRaw, -32602, "Invalid arguments");
                    return true;
                }

                if (!MiniMcpToolRegistry.TryInvokeTool(toolName, argumentsJson, out var result))
                {
                    responseJson = this.MakeErrorResponse(idRaw, -32601, result.Text);
                    return true;
                }

                var warningText = BuildCompileWarningText(diagnostics);
                responseJson = this.MakeToolTextResponse(idRaw, result.Text, result.IsError, warningText);
                return true;
            }

            responseJson = this.MakeErrorResponse(idRaw, -32601, "Method not found");
            return true;
        }

        private string MakeResultResponse(string idRaw, string resultJson)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"result\":" + resultJson + "}";
        }

        private string MakeToolTextResponse(string idRaw, string text, bool isError, string warningText = null)
        {
            var escaped = MiniMcpJson.EscapeJson(text);
            var builder = new StringBuilder();
            builder.Append("{\"content\":[");

            var hasWarning = !string.IsNullOrWhiteSpace(warningText);
            if (hasWarning)
            {
                builder.Append("{\"type\":\"text\",\"text\":\"");
                builder.Append(MiniMcpJson.EscapeJson(warningText));
                builder.Append("\"},");
            }

            builder.Append("{\"type\":\"text\",\"text\":\"");
            builder.Append(escaped);
            builder.Append("\"}],\"isError\":");
            builder.Append(isError ? "true" : "false");
            builder.Append('}');

            var result = builder.ToString();
            return this.MakeResultResponse(idRaw, result);
        }

        private string MakeErrorResponse(string idRaw, int code, string message)
        {
            return "{\"jsonrpc\":\"2.0\",\"id\":" + idRaw + ",\"error\":{\"code\":" + code + ",\"message\":\"" + MiniMcpJson.EscapeJson(message) + "\"}}";
        }

        private void WriteHttpResponse(StreamWriter writer, int statusCode, string statusText, string contentType, string body)
        {
            var payload = body ?? string.Empty;
            var byteLength = Encoding.UTF8.GetByteCount(payload);

            writer.WriteLine($"HTTP/1.1 {statusCode} {statusText}");
            writer.WriteLine($"Content-Type: {contentType}; charset=utf-8");
            writer.WriteLine($"Content-Length: {byteLength}");
            writer.WriteLine("Connection: close");
            writer.WriteLine();
            writer.Write(payload);
            writer.Flush();
        }

        private static string ReadRequestBody(StreamReader reader, int contentLength)
        {
            if (reader == null || contentLength <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var bytesRead = 0;
            while (bytesRead < contentLength)
            {
                var nextChar = reader.Read();
                if (nextChar < 0)
                {
                    break;
                }

                char value = (char)nextChar;
                builder.Append(value);
                bytesRead += Encoding.UTF8.GetByteCount(new[] { value });
            }

            return builder.ToString();
        }

        private static string ExtractIdRaw(string requestJson)
        {
            if (string.IsNullOrEmpty(requestJson))
            {
                return "null";
            }

            var keyIndex = requestJson.IndexOf("\"id\"", StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return "null";
            }

            var colonIndex = requestJson.IndexOf(':', keyIndex);
            if (colonIndex < 0)
            {
                return "null";
            }

            var i = colonIndex + 1;
            while (i < requestJson.Length && char.IsWhiteSpace(requestJson[i]))
            {
                i++;
            }

            if (i >= requestJson.Length)
            {
                return "null";
            }

            if (requestJson[i] == '"')
            {
                var start = i;
                i++;
                var escaping = false;
                while (i < requestJson.Length)
                {
                    var ch = requestJson[i];
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (ch == '\\')
                    {
                        escaping = true;
                    }
                    else if (ch == '"')
                    {
                        return requestJson.Substring(start, i - start + 1);
                    }

                    i++;
                }

                return "null";
            }

            var end = i;
            while (end < requestJson.Length)
            {
                var ch = requestJson[end];
                if (ch == ',' || ch == '}' || char.IsWhiteSpace(ch))
                {
                    break;
                }

                end++;
            }

            var raw = requestJson.Substring(i, end - i).Trim();
            return string.IsNullOrEmpty(raw) ? "null" : raw;
        }

        private static string BuildToolsListResult()
        {
            var tools = MiniMcpToolRegistry.GetEnabledToolDescriptors();
            var builder = new StringBuilder();
            builder.Append("{\"tools\":[");

            for (var i = 0; i < tools.Count; i++)
            {
                var tool = tools[i];
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":\"");
                builder.Append(MiniMcpJson.EscapeJson(tool.Name));
                builder.Append("\",\"description\":\"");
                builder.Append(MiniMcpJson.EscapeJson(tool.Description));
                builder.Append("\",\"group\":");
                builder.Append(string.IsNullOrWhiteSpace(tool.Group)
                    ? "null"
                    : "\"" + MiniMcpJson.EscapeJson(tool.Group) + "\"");
                builder.Append(",\"inputSchema\":");
                builder.Append(string.IsNullOrWhiteSpace(tool.InputSchemaJson)
                    ? MiniMcpToolAttribute.DefaultInputSchemaJson
                    : tool.InputSchemaJson);
                builder.Append(",\"await\":{");
                builder.Append("\"supported\":");
                builder.Append(tool.SupportsAwait ? "true" : "false");
                builder.Append(",\"kind\":");
                builder.Append(string.IsNullOrWhiteSpace(tool.AwaitKind)
                    ? "null"
                    : "\"" + MiniMcpJson.EscapeJson(tool.AwaitKind) + "\"");
                builder.Append(",\"defaultTimeoutMs\":");
                builder.Append(tool.DefaultAwaitTimeoutMs);
                builder.Append(",\"maxTimeoutMs\":");
                builder.Append(tool.MaxAwaitTimeoutMs);
                builder.Append("}");
                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static string BuildCompileWarningText(MiniMcpRuntimeDiagnostics.Snapshot diagnostics)
        {
            if (diagnostics.CompileErrorCount <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append("Warning: Unity currently has ");
            builder.Append(diagnostics.CompileErrorCount);
            builder.Append(" compile error(s).");
            if (diagnostics.LastCompileFinishedUtc.HasValue)
            {
                builder.Append(" Last compile finished at ");
                builder.Append(diagnostics.LastCompileFinishedUtc.Value.ToString("O", CultureInfo.InvariantCulture));
                builder.Append('.');
            }

            return builder.ToString();
        }

        private static string TrimForLog(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var trimmed = value.Replace("\r", " ").Replace("\n", " ");
            return trimmed.Length <= 220 ? trimmed : trimmed.Substring(0, 220) + "...";
        }

        private void Log(string message)
        {
            this.LogReceived?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        public void Dispose()
        {
            this.Stop();
        }
    }
}
