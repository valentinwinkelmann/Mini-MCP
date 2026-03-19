using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using MiniMCP;
using UnityEditor;
using UnityEngine;

namespace MiniMCP.Editor
{
    [InitializeOnLoad]
    internal static class MiniMcpRelayService
    {
        private const string RelayPortKey = "MiniMCP.Relay.Port";
        private const int DefaultRelayPort = 7788;
        private const string PublicRelayEnabledKey = "MiniMCP.Relay.Public.Enabled";
        private const string PublicRelayPortKey = "MiniMCP.Relay.Public.Port";
        private const string PublicRelaySharedSecretKey = "MiniMCP.Relay.Public.SharedSecret";
        private const string LegacyPublicRelayBearerTokenKey = "MiniMCP.Relay.Public.BearerToken";
        private const int DefaultPublicRelayPort = 8088;
        private static readonly object ProcessGate = new object();
        private static readonly object RelayStateGate = new object();

        private static Process relayProcess;
        private static bool cachedRelayReachable;
        private static string cachedRelayTarget = "unreachable";
        private static bool isRelayProbeInFlight;
        private static double nextRelayProbeTime;

        public static event Action<string> LogReceived;

        static MiniMcpRelayService()
        {
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.update += OnEditorUpdate;
        }

        public static int RelayPort
        {
            get => EditorPrefs.GetInt(RelayPortKey, DefaultRelayPort);
            private set => EditorPrefs.SetInt(RelayPortKey, value);
        }

        public static bool PublicRelayEnabled
        {
            get => EditorPrefs.GetBool(PublicRelayEnabledKey, false);
            set => EditorPrefs.SetBool(PublicRelayEnabledKey, value);
        }

        public static int PublicRelayPort
        {
            get => EditorPrefs.GetInt(PublicRelayPortKey, DefaultPublicRelayPort);
            set => EditorPrefs.SetInt(PublicRelayPortKey, ClampPort(value));
        }

        public static string PublicRelaySharedSecret
        {
            get
            {
                var value = NormalizeSecret(EditorPrefs.GetString(PublicRelaySharedSecretKey, string.Empty));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }

                return NormalizeSecret(EditorPrefs.GetString(LegacyPublicRelayBearerTokenKey, string.Empty));
            }

            set => EditorPrefs.SetString(PublicRelaySharedSecretKey, NormalizeSecret(value));
        }

        public static bool IsRelayReachable(int relayPort)
        {
            return TryGetRelayTarget(relayPort, out _);
        }

        public static void GetCachedRelayState(out bool isReachable, out string target)
        {
            lock (RelayStateGate)
            {
                isReachable = cachedRelayReachable;
                target = cachedRelayTarget;
            }
        }

        public static bool TryGetRelayTarget(int relayPort, out string target)
        {
            target = string.Empty;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{relayPort}/health");
                request.Method = "GET";
                request.Timeout = 700;
                request.ReadWriteTimeout = 700;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    var body = reader.ReadToEnd();
                    if (!MiniMcpJson.TryExtractStringProperty(body, "target", out target))
                    {
                        target = string.Empty;
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool StartRelay(int relayPort, int backendPort)
        {
            relayPort = ClampPort(relayPort);
            backendPort = ClampPort(backendPort);

            RelayPort = relayPort;

            if (TryGetRelayTarget(relayPort, out var currentTarget))
            {
                SetCachedRelayState(true, currentTarget);
                UpdateRelayTarget(backendPort, "relay_already_running");
                ForwardLog($"[Relay] Already reachable on http://127.0.0.1:{relayPort}/mcp (target: {currentTarget}).");
                return true;
            }

            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            var scriptPath = Path.GetFullPath(Path.Combine(projectRoot, "Packages", "com.vwgamedev.mini-mcp", "Editor", "MCP", "relay.js"));
            if (!File.Exists(scriptPath))
            {
                ForwardLog("[Relay] relay.js not found.");
                return false;
            }

            lock (ProcessGate)
            {
                TryKillTrackedProcessUnderLock("restart_before_new_start");
            }

            if (IsPortInUse(RelayPort))
            {
                ForwardLog($"[Relay] Port {RelayPort} is in use and not healthy. Close old relay terminal/process first.");
                return false;
            }

            if (PublicRelayEnabled)
            {
                if (string.IsNullOrWhiteSpace(PublicRelaySharedSecret))
                {
                    ForwardLog("[Relay] Public relay is enabled but Upstream Secret is empty.");
                    return false;
                }
            }

            try
            {
                if (PublicRelayEnabled)
                {
                    ForwardLog($"[Relay] Unity public secret before node start = \"{PublicRelaySharedSecret}\" (len={PublicRelaySharedSecret.Length})");
                }

                var nodeArgs = BuildNodeArguments(
                    scriptPath,
                    RelayPort,
                    backendPort,
                    PublicRelayEnabled,
                    PublicRelayPort,
                    PublicRelaySharedSecret);

                var info = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c " + nodeArgs,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Application.dataPath,
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };

                var process = new Process
                {
                    StartInfo = info,
                    EnableRaisingEvents = true
                };

                process.Exited += OnRelayExited;

                if (!process.Start())
                {
                    process.Dispose();
                    ForwardLog("[Relay] Failed to start node relay process.");
                    return false;
                }

                lock (ProcessGate)
                {
                    relayProcess = process;
                }

                ForwardLog($"[Relay] Starting on http://127.0.0.1:{RelayPort}/mcp -> http://127.0.0.1:{backendPort}/mcp");
                if (PublicRelayEnabled)
                {
                    ForwardLog($"[Relay] Public relay upstream listening on http://0.0.0.0:{PublicRelayPort}/mcp");
                    ForwardLog("[Relay] External HTTPS endpoint is expected to live in your PHP reverse proxy.");
                    ForwardLog("[Relay] Public relay requires header X-MiniMcp-Relay-Secret on every upstream request.");
                    ForwardLog($"[Relay] Node args public secret = \"{PublicRelaySharedSecret}\" (len={PublicRelaySharedSecret.Length})");
                }
                ForwardLog("[Relay] Visible terminal opened for relay process.");
                SetCachedRelayState(false, "starting");
                nextRelayProbeTime = 0;
                UpdateRelayTarget(backendPort, "relay_started");
                return true;
            }
            catch (Exception ex)
            {
                ForwardLog($"[Relay] Start failed: {ex.Message}");
                return false;
            }
        }

        public static bool StopRelay(string reason = "unspecified")
        {
            ForwardLog($"[Relay] Stop requested. reason={reason}");

            var shutdownRequested = TryRequestRelayShutdown(RelayPort);
            if (shutdownRequested && WaitRelayUnavailable(RelayPort, 3000))
            {
                SetCachedRelayState(false, "unreachable");
                ClearTrackedProcessIfExited();
                return true;
            }

            lock (ProcessGate)
            {
                if (!TryKillTrackedProcessUnderLock(reason))
                {
                    return !IsPortInUse(RelayPort);
                }
            }

            var stopped = WaitRelayUnavailable(RelayPort, 2000);
            if (stopped)
            {
                SetCachedRelayState(false, "unreachable");
            }

            return stopped;
        }

        public static void NotifyRelayStatus(string state, string reason)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{RelayPort}/status");
                    request.Method = "POST";
                    request.ContentType = "application/json; charset=utf-8";
                    request.Timeout = 450;
                    request.ReadWriteTimeout = 450;

                    var payload = "{\"state\":\"" + MiniMcpJson.EscapeJson(state) + "\",\"reason\":\"" + MiniMcpJson.EscapeJson(reason ?? string.Empty) + "\"}";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = bytes.Length;

                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        // Fire-and-forget status hint.
                    }
                }
                catch
                {
                    // Relay may not be running; status hints are best-effort.
                }
            });
        }

        public static bool TryNotifyAwaitedOperationCompletion(string payloadJson, out string operationId)
        {
            return TryNotifyAwaitedOperationCompletion(RelayPort, payloadJson, out operationId);
        }

        public static bool TryNotifyAwaitedOperationCompletion(int relayPort, string payloadJson, out string operationId)
        {
            operationId = string.Empty;
            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return false;
            }

            if (!MiniMcpJson.TryExtractStringProperty(payloadJson, "operationId", out operationId))
            {
                return false;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{relayPort}/operation-complete");
                request.Method = "POST";
                request.ContentType = "application/json; charset=utf-8";
                request.Timeout = 450;
                request.ReadWriteTimeout = 450;

                var bytes = Encoding.UTF8.GetBytes(payloadJson);
                request.ContentLength = bytes.Length;

                using (var stream = request.GetRequestStream())
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                operationId = string.Empty;
                return false;
            }
        }

        public static void UpdateRelayTarget(int backendPort, string reason)
        {
            if (backendPort < 1024 || backendPort > 65535)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{RelayPort}/target");
                    request.Method = "POST";
                    request.ContentType = "application/json; charset=utf-8";
                    request.Timeout = 450;
                    request.ReadWriteTimeout = 450;

                    var payload = "{\"target\":\"http://127.0.0.1:" + backendPort + "/mcp\",\"reason\":\"" + MiniMcpJson.EscapeJson(reason ?? string.Empty) + "\"}";
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    request.ContentLength = bytes.Length;

                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }

                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        // Best-effort target sync.
                    }
                }
                catch
                {
                    // Relay may not be running.
                }
            });
        }

        private static bool WaitRelayUnavailable(int relayPort, int timeoutMs)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (!IsPortInUse(relayPort) && !TryGetRelayTarget(relayPort, out _))
                {
                    return true;
                }

                Thread.Sleep(100);
            }

            return !IsPortInUse(relayPort) && !TryGetRelayTarget(relayPort, out _);
        }

        public static string GetPublicRelayEndpoint(bool includeToken)
        {
            if (!PublicRelayEnabled)
            {
                return "disabled";
            }

            return "configured externally in PHP/HTTPS proxy";
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < nextRelayProbeTime)
            {
                return;
            }

            lock (RelayStateGate)
            {
                if (isRelayProbeInFlight)
                {
                    return;
                }

                isRelayProbeInFlight = true;
            }

            nextRelayProbeTime = EditorApplication.timeSinceStartup + 1.0d;
            var relayPort = RelayPort;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var isReachable = TryGetRelayTarget(relayPort, out var target);
                SetCachedRelayState(isReachable, isReachable ? target : "unreachable");

                lock (RelayStateGate)
                {
                    isRelayProbeInFlight = false;
                }
            });
        }

        private static bool TryRequestRelayShutdown(int relayPort)
        {
            if (!IsPortInUse(relayPort))
            {
                return true;
            }

            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{relayPort}/shutdown");
                request.Method = "POST";
                request.Timeout = 800;
                request.ReadWriteTimeout = 800;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var ar = client.BeginConnect("127.0.0.1", port, null, null);
                    var ok = ar.AsyncWaitHandle.WaitOne(150);
                    if (!ok)
                    {
                        return false;
                    }

                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int ClampPort(int port)
        {
            if (port < 1)
            {
                return 1;
            }

            return port > 65535 ? 65535 : port;
        }

        private static string NormalizeSecret(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static string BuildNodeArguments(
            string scriptPath,
            int relayPort,
            int backendPort,
            bool publicEnabled,
            int publicPort,
            string publicSharedSecret)
        {
            var builder = new StringBuilder();
            builder.Append("node ");
            builder.Append(QuoteArg(scriptPath));
            builder.Append(" --port ");
            builder.Append(relayPort);
            builder.Append(" --target ");
            builder.Append(QuoteArg($"http://127.0.0.1:{backendPort}/mcp"));

            if (publicEnabled)
            {
                builder.Append(" --public-enabled");
                builder.Append(" --public-port ");
                builder.Append(publicPort);
                builder.Append(" --public-bind-host 0.0.0.0");
                if (!string.IsNullOrWhiteSpace(publicSharedSecret))
                {
                    builder.Append(" --public-secret ");
                    builder.Append(QuoteArg(publicSharedSecret));
                }
            }

            return builder.ToString();
        }

        private static string QuoteArg(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool TryKillTrackedProcessUnderLock(string reason)
        {
            if (relayProcess == null)
            {
                return false;
            }

            var process = relayProcess;
            relayProcess = null;

            try
            {
                if (!process.HasExited)
                {
                    ForwardLog($"[Relay] Killing tracked relay process. reason={reason}");
                    process.Kill();
                    process.WaitForExit(1500);
                }
            }
            catch (Exception ex)
            {
                ForwardLog($"[Relay] Failed to kill tracked process: {ex.Message}");
                return false;
            }
            finally
            {
                process.Dispose();
            }

            return true;
        }

        private static void ClearTrackedProcessIfExited()
        {
            lock (ProcessGate)
            {
                if (relayProcess == null)
                {
                    return;
                }

                try
                {
                    if (!relayProcess.HasExited)
                    {
                        return;
                    }

                    relayProcess.Dispose();
                    relayProcess = null;
                }
                catch
                {
                    relayProcess = null;
                }
            }
        }

        private static void OnRelayExited(object sender, EventArgs e)
        {
            var process = sender as Process;
            var exitCode = 0;

            try
            {
                if (process != null)
                {
                    exitCode = process.ExitCode;
                }
            }
            catch
            {
                exitCode = -1;
            }

            lock (ProcessGate)
            {
                if (ReferenceEquals(relayProcess, process))
                {
                    relayProcess = null;
                }
            }

            SetCachedRelayState(false, "unreachable");

            ForwardLog($"[Relay] Process exited with code {exitCode}.");

            try
            {
                process?.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private static void OnEditorQuitting()
        {
            StopRelay("editor_quitting");
        }

        private static void SetCachedRelayState(bool isReachable, string target)
        {
            lock (RelayStateGate)
            {
                cachedRelayReachable = isReachable;
                cachedRelayTarget = string.IsNullOrWhiteSpace(target) ? "unreachable" : target;
            }
        }

        private static void ForwardLog(string message)
        {
            LogReceived?.Invoke(message);
        }
    }
}
