using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using MiniMCP;
using UnityEditor;

namespace MiniMCP.Editor
{
    [InitializeOnLoad]
    internal static class MiniMcpEditorService
    {
        private const string SessionEnabledKey = "MiniMCP.Backend.Enabled.Session";
        private const string PortKey = "MiniMCP.Port";
        private const int DefaultPort = 7777;
        private static readonly string BackendLockPath = Path.Combine(Path.GetTempPath(), "mini-unity-mcp-backend.lock.json");

        private static MiniMcpServer server;
        private static double nextStartRetryTime;
        private static double nextHealthProbeTime;
        private static double nextRelaySyncTime;
        private static int consecutiveHealthFailures;
        private static bool ownsBackendLock;
        private static readonly object HealthProbeGate = new object();
        private static bool isHealthProbeInFlight;
        private static int lastPortConflictPort = -1;
        private static double lastPortConflictLogTime;
        private static string backendStatus = "Restarting";
        private static bool isUsingExistingBackend;
        private static bool isStartBlockedByPortConflict;

        public static event Action<string> LogReceived;

        static MiniMcpEditorService()
        {
            EnsureServer();
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnEditorQuitting;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            nextStartRetryTime = 0;
            backendStatus = "Idle";

            if (IsEnabledForCurrentSession)
            {
                TryStartNow();
            }
        }

        public static bool IsRunning => (server != null && server.IsRunning) || isUsingExistingBackend;

        public static int DesiredPort
        {
            get => EditorPrefs.GetInt(PortKey, DefaultPort);
            private set => EditorPrefs.SetInt(PortKey, value);
        }

        public static int ActivePort => server != null && server.IsRunning ? server.Port : DesiredPort;

        public static string BackendStatus => backendStatus;

        public static bool IsEnabledForCurrentSession
        {
            get => SessionState.GetBool(SessionEnabledKey, false);
            private set => SessionState.SetBool(SessionEnabledKey, value);
        }

        public static bool Start(int port)
        {
            EnsureServer();
            port = ClampPort(port);
            DesiredPort = port;
            IsEnabledForCurrentSession = true;
            isStartBlockedByPortConflict = false;
            lastPortConflictPort = -1;
            lastPortConflictLogTime = 0;

            if (server.IsRunning)
            {
                if (server.Port == port)
                {
                    isUsingExistingBackend = false;
                    backendStatus = "Listening";
                    MiniMcpRelayService.UpdateRelayTarget(server.Port, "backend_already_running");
                    return true;
                }

                ForwardLog($"[Backend] Restart requested: switching port {server.Port} -> {port}.");
                server.Stop();
                ReleaseBackendLockIfOwned();
            }

            isUsingExistingBackend = false;

            nextStartRetryTime = 0;
            return TryStartNow();
        }

        public static void Stop(string reason = "unspecified")
        {
            IsEnabledForCurrentSession = false;
            isStartBlockedByPortConflict = false;
            isUsingExistingBackend = false;
            StopBackend(reason);
        }

        public static bool EnsureRunning(string reason = "unspecified")
        {
            EnsureServer();

            if (!IsEnabledForCurrentSession)
            {
                backendStatus = "Idle";
                return false;
            }

            if (server.IsRunning)
            {
                isUsingExistingBackend = false;
                backendStatus = "Listening";
                MiniMcpRelayService.UpdateRelayTarget(server.Port, "backend_running_sync");
                return true;
            }

            if (isUsingExistingBackend)
            {
                if (IsBackendHealthy(DesiredPort))
                {
                    backendStatus = "Listening";
                    MiniMcpRelayService.UpdateRelayTarget(DesiredPort, "backend_existing_running_sync");
                    return true;
                }

                isUsingExistingBackend = false;
                backendStatus = "Restarting";
            }

            nextStartRetryTime = 0;
            return TryStartNow();
        }

        private static void OnEditorUpdate()
        {
            if (!IsEnabledForCurrentSession)
            {
                isUsingExistingBackend = false;
                backendStatus = "Idle";
                return;
            }

            if (isStartBlockedByPortConflict)
            {
                backendStatus = $"Port {DesiredPort} blocked; manual restart required";
                return;
            }

            if (server != null && server.IsRunning)
            {
                isUsingExistingBackend = false;
                SyncRelayTargetIfDue();
                BeginBackendHealthProbeIfDue();
                return;
            }

            if (isUsingExistingBackend)
            {
                if (EditorApplication.timeSinceStartup >= nextHealthProbeTime)
                {
                    nextHealthProbeTime = EditorApplication.timeSinceStartup + 2.0d;
                    if (IsBackendHealthy(DesiredPort))
                    {
                        backendStatus = "Listening";
                        SyncRelayTargetIfDue();
                        return;
                    }

                    isUsingExistingBackend = false;
                    backendStatus = "Restarting";
                    nextStartRetryTime = 0;
                }
                else
                {
                    backendStatus = "Listening";
                    return;
                }
            }

            if (EditorApplication.isCompiling)
            {
                backendStatus = "Waiting for compile to finish";
                return;
            }

            if (EditorApplication.timeSinceStartup < nextStartRetryTime)
            {
                return;
            }

            TryStartNow();
        }

        private static void OnAfterAssemblyReload()
        {
            if (!IsEnabledForCurrentSession)
            {
                backendStatus = "Idle";
                return;
            }

            nextStartRetryTime = 0;
            TryStartNow();
        }

        private static void OnBeforeAssemblyReload()
        {
            if (server == null || !server.IsRunning)
            {
                return;
            }

            StopBackend("before_assembly_reload");
        }

        private static void OnEditorQuitting()
        {
            Stop("editor_quitting");
        }

        private static bool TryStartNow()
        {
            EnsureServer();

            if (EditorApplication.isCompiling)
            {
                isUsingExistingBackend = false;
                backendStatus = "Waiting for compile to finish";
                nextStartRetryTime = EditorApplication.timeSinceStartup + 1.0d;
                return false;
            }

            if (!TryAcquireBackendLock())
            {
                isUsingExistingBackend = false;
                backendStatus = "Waiting for MCP lock";
                nextStartRetryTime = EditorApplication.timeSinceStartup + 1.5d;
                return false;
            }

            if (IsPortInUse(DesiredPort))
            {
                if (IsBackendHealthy(DesiredPort))
                {
                    ReleaseBackendLockIfOwned();
                    isUsingExistingBackend = true;
                    isStartBlockedByPortConflict = false;
                    backendStatus = "Listening";
                    nextStartRetryTime = 0;
                    nextHealthProbeTime = EditorApplication.timeSinceStartup + 2.0d;
                    nextRelaySyncTime = 0;
                    MiniMcpRelayService.UpdateRelayTarget(DesiredPort, "backend_existing_detected");
                    return true;
                }

                ReleaseBackendLockIfOwned();
                isUsingExistingBackend = false;
                isStartBlockedByPortConflict = true;
                backendStatus = $"Port {DesiredPort} is occupied";
                LogPortConflictIfNeeded(DesiredPort);
                nextStartRetryTime = double.MaxValue;
                return false;
            }

            var ok = server.Start(DesiredPort, false);
            if (!ok)
            {
                ReleaseBackendLockIfOwned();
                isUsingExistingBackend = false;
                isStartBlockedByPortConflict = true;
                backendStatus = $"Port {DesiredPort} failed to bind";
                ForwardLog($"[Backend] Start failed on port {DesiredPort}: {server.LastStartErrorMessage ?? "Unknown socket error"}");
                nextStartRetryTime = double.MaxValue;
                return false;
            }

            nextStartRetryTime = 0;
            nextHealthProbeTime = EditorApplication.timeSinceStartup + 2.0d;
            nextRelaySyncTime = EditorApplication.timeSinceStartup + 0.5d;
            consecutiveHealthFailures = 0;
            lastPortConflictPort = -1;
            lastPortConflictLogTime = 0;
            isUsingExistingBackend = false;
            isStartBlockedByPortConflict = false;
            backendStatus = "Listening";
            MiniMcpRelayService.UpdateRelayTarget(server.Port, "backend_started");
            return true;
        }

        private static void StopBackend(string reason)
        {
            ForwardLog($"[Backend] Stop requested. reason={reason}");
            nextStartRetryTime = 0;
            backendStatus = "Stopped";
            isUsingExistingBackend = false;
            isStartBlockedByPortConflict = false;

            if (server != null && server.IsRunning)
            {
                server.Stop();
            }

            ReleaseBackendLockIfOwned();
        }

        private static void SyncRelayTargetIfDue()
        {
            if (EditorApplication.timeSinceStartup < nextRelaySyncTime)
            {
                return;
            }

            nextRelaySyncTime = EditorApplication.timeSinceStartup + 2.0d;
            MiniMcpRelayService.UpdateRelayTarget(server.Port, "backend_running_sync");
        }

        private static void BeginBackendHealthProbeIfDue()
        {
            if (EditorApplication.timeSinceStartup < nextHealthProbeTime)
            {
                return;
            }

            nextHealthProbeTime = EditorApplication.timeSinceStartup + 2.0d;

            if (EditorApplication.isCompiling)
            {
                consecutiveHealthFailures = 0;
                return;
            }

            lock (HealthProbeGate)
            {
                if (isHealthProbeInFlight)
                {
                    return;
                }

                isHealthProbeInFlight = true;
            }

            var port = ActivePort;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var healthy = IsBackendHealthy(port);

                lock (HealthProbeGate)
                {
                    isHealthProbeInFlight = false;
                }

                if (healthy)
                {
                    consecutiveHealthFailures = 0;
                    return;
                }

                consecutiveHealthFailures++;
                ForwardLog($"[Backend] Health probe failed ({consecutiveHealthFailures}/3) on port {port}.");
                if (consecutiveHealthFailures < 3)
                {
                    backendStatus = "Health probe failed";
                    return;
                }

                ForwardLog("[Backend] Health watchdog restarting unresponsive MCP backend.");
                try
                {
                    server.Stop();
                }
                catch
                {
                    // Best effort stop before restart.
                }

                ReleaseBackendLockIfOwned();
                consecutiveHealthFailures = 0;
                backendStatus = "Restarting";
                nextStartRetryTime = EditorApplication.timeSinceStartup + 0.3d;
            });
        }

        private static void LogPortConflictIfNeeded(int port)
        {
            var now = EditorApplication.timeSinceStartup;
            if (lastPortConflictPort == port && now - lastPortConflictLogTime < 8.0d)
            {
                return;
            }

            lastPortConflictPort = port;
            lastPortConflictLogTime = now;
            ForwardLog($"[Backend] Port {port} is already occupied by another process or stale listener. Retry is throttled.");
        }

        private static bool IsBackendHealthy(int port)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"http://127.0.0.1:{port}/health");
                request.Method = "GET";
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    var body = reader.ReadToEnd();
                    if (!MiniMcpJson.TryExtractBoolProperty(body, "ok", out var ok))
                    {
                        return false;
                    }

                    return ok;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureServer()
        {
            if (server != null)
            {
                return;
            }

            server = new MiniMcpServer();
            server.LogReceived += ForwardLog;
        }

        private static int ClampPort(int port)
        {
            if (port < 1024)
            {
                return 1024;
            }

            return port > 65535 ? 65535 : port;
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                for (var i = 0; i < listeners.Length; i++)
                {
                    var endpoint = listeners[i];
                    if (endpoint == null)
                    {
                        continue;
                    }

                    if (endpoint.Port != port)
                    {
                        continue;
                    }

                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void ForwardLog(string message)
        {
            LogReceived?.Invoke(message);
        }

        private static void OnProcessExit(object sender, EventArgs e)
        {
            ReleaseBackendLockIfOwned();
        }

        private static bool TryAcquireBackendLock()
        {
            if (ownsBackendLock)
            {
                return true;
            }

            var currentPid = Process.GetCurrentProcess().Id;

            try
            {
                if (!File.Exists(BackendLockPath))
                {
                    WriteBackendLock(currentPid, DesiredPort);
                    ownsBackendLock = true;
                    return true;
                }

                var existing = File.ReadAllText(BackendLockPath);
                if (!MiniMcpJson.TryExtractIntProperty(existing, "pid", out var existingPid) || existingPid <= 0)
                {
                    File.Delete(BackendLockPath);
                    WriteBackendLock(currentPid, DesiredPort);
                    ownsBackendLock = true;
                    ForwardLog("[Backend] Replaced unreadable backend lock.");
                    return true;
                }

                if (existingPid == currentPid)
                {
                    WriteBackendLock(currentPid, DesiredPort);
                    ownsBackendLock = true;
                    return true;
                }

                if (!IsProcessAlive(existingPid))
                {
                    File.Delete(BackendLockPath);
                    WriteBackendLock(currentPid, DesiredPort);
                    ownsBackendLock = true;
                    ForwardLog($"[Backend] Replaced stale backend lock from pid {existingPid}.");
                    return true;
                }

                ForwardLog($"[Backend] Another Unity process owns MCP lock (pid {existingPid}). Start skipped.");
                return false;
            }
            catch (Exception ex)
            {
                ForwardLog($"[Backend] Lock acquisition failed: {ex.Message}");
                return false;
            }
        }

        private static void WriteBackendLock(int pid, int port)
        {
            var payload = "{\"pid\":" + pid + ",\"port\":" + port + ",\"updatedUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}";
            File.WriteAllText(BackendLockPath, payload);
        }

        private static void ReleaseBackendLockIfOwned()
        {
            if (!ownsBackendLock)
            {
                return;
            }

            try
            {
                if (File.Exists(BackendLockPath))
                {
                    var existing = File.ReadAllText(BackendLockPath);
                    if (MiniMcpJson.TryExtractIntProperty(existing, "pid", out var existingPid) && existingPid == Process.GetCurrentProcess().Id)
                    {
                        File.Delete(BackendLockPath);
                    }
                }
            }
            catch
            {
                // Best effort lock cleanup.
            }
            finally
            {
                ownsBackendLock = false;
            }
        }

        private static bool IsProcessAlive(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }
}
