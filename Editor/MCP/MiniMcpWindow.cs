using System;
using System.Collections.Generic;
using System.Linq;
using MiniMCP;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace MiniMCP.Editor
{
    public sealed class MiniMcpWindow : EditorWindow
    {
        private const string WindowUxmlPath = "Packages/com.vwgamedev.mini-mcp/Editor/UI/MiniMcpWindow.uxml";
        private const string WindowUssPath = "Packages/com.vwgamedev.mini-mcp/Editor/UI/MiniMcpWindow.uss";
        private const string ConsoleLogsKey = "MiniMCP.Window.ConsoleLogs";
        private static readonly Queue<string> PendingLogs = new Queue<string>();
        private static readonly object LogLock = new object();

        private readonly List<MiniMcpToolDescriptor> toolDescriptors = new List<MiniMcpToolDescriptor>();
        private int port;
        private int relayPort;
        private bool publicRelayEnabled;
        private int publicRelayPort;
        private string publicRelaySharedSecret;
        private bool consoleLogsEnabled;
        private bool relayReachable;
        private string relayTarget = "unreachable";

        private Label noticeLabel;
        private IntegerField portField;
        private IntegerField relayPortField;
        private Toggle consoleLogsToggle;
        private Toggle publicRelayEnabledToggle;
        private IntegerField publicRelayPortField;
        private TextField publicRelaySecretField;
        private Button startButton;
        private Button stopButton;
        private Button refreshToolsButton;
        private Label backendStatusValue;
        private Label relayStatusValue;
        private Label relayEndpointValue;
        private Label relayTargetValue;
        private Label backendEndpointValue;
        private Label publicUpstreamValue;
        private Label toolCountLabel;
        private VisualElement toolListContainer;

        [MenuItem("Tools/MiniMCP")]
        public static void Open()
        {
            var window = GetWindow<MiniMcpWindow>("MiniMCP");
            window.minSize = new Vector2(520f, 320f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += this.OnEditorUpdate;

            MiniMcpEditorService.LogReceived += EnqueueLog;
            MiniMcpRelayService.LogReceived += EnqueueLog;
            this.port = MiniMcpEditorService.DesiredPort;
            this.relayPort = MiniMcpRelayService.RelayPort;
            this.publicRelayEnabled = MiniMcpRelayService.PublicRelayEnabled;
            this.publicRelayPort = MiniMcpRelayService.PublicRelayPort;
            this.publicRelaySharedSecret = MiniMcpRelayService.PublicRelaySharedSecret;
            this.consoleLogsEnabled = EditorPrefs.GetBool(ConsoleLogsKey, false);
            MiniMcpEditorService.EnsureRunning("window_enabled");
            this.RefreshRelayState();
            this.RefreshToolList();
            this.RefreshUiFromState();
        }

        private void OnDisable()
        {
            EditorApplication.update -= this.OnEditorUpdate;

            MiniMcpEditorService.LogReceived -= EnqueueLog;
            MiniMcpRelayService.LogReceived -= EnqueueLog;
        }

        public void CreateGUI()
        {
            this.rootVisualElement.Clear();

            var windowTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(WindowUxmlPath);
            var windowStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>(WindowUssPath);
            if (windowTree == null || windowStyle == null)
            {
                this.rootVisualElement.Add(new HelpBox("MiniMCP window UI assets could not be loaded.", HelpBoxMessageType.Error));
                return;
            }

            windowTree.CloneTree(this.rootVisualElement);
            this.rootVisualElement.styleSheets.Add(windowStyle);

            this.noticeLabel = this.rootVisualElement.Q<Label>("NoticeLabel");
            this.portField = this.rootVisualElement.Q<IntegerField>("PortField");
            this.relayPortField = this.rootVisualElement.Q<IntegerField>("RelayPortField");
            this.consoleLogsToggle = this.rootVisualElement.Q<Toggle>("ConsoleLogsToggle");
            this.publicRelayEnabledToggle = this.rootVisualElement.Q<Toggle>("PublicRelayEnabledToggle");
            this.publicRelayPortField = this.rootVisualElement.Q<IntegerField>("PublicRelayPortField");
            this.publicRelaySecretField = this.rootVisualElement.Q<TextField>("PublicRelaySecretField");
            this.startButton = this.rootVisualElement.Q<Button>("StartButton");
            this.stopButton = this.rootVisualElement.Q<Button>("StopButton");
            this.refreshToolsButton = this.rootVisualElement.Q<Button>("RefreshToolsButton");
            this.backendStatusValue = this.rootVisualElement.Q<Label>("BackendStatusValue");
            this.relayStatusValue = this.rootVisualElement.Q<Label>("RelayStatusValue");
            this.relayEndpointValue = this.rootVisualElement.Q<Label>("RelayEndpointValue");
            this.relayTargetValue = this.rootVisualElement.Q<Label>("RelayTargetValue");
            this.backendEndpointValue = this.rootVisualElement.Q<Label>("BackendEndpointValue");
            this.publicUpstreamValue = this.rootVisualElement.Q<Label>("PublicUpstreamValue");
            this.toolCountLabel = this.rootVisualElement.Q<Label>("ToolCountLabel");
            this.toolListContainer = this.rootVisualElement.Q<VisualElement>("ToolListContainer");

            if (this.noticeLabel == null
                || this.portField == null
                || this.relayPortField == null
                || this.consoleLogsToggle == null
                || this.publicRelayEnabledToggle == null
                || this.publicRelayPortField == null
                || this.publicRelaySecretField == null
                || this.startButton == null
                || this.stopButton == null
                || this.refreshToolsButton == null
                || this.backendStatusValue == null
                || this.relayStatusValue == null
                || this.relayEndpointValue == null
                || this.relayTargetValue == null
                || this.backendEndpointValue == null
                || this.publicUpstreamValue == null
                || this.toolCountLabel == null
                || this.toolListContainer == null)
            {
                this.rootVisualElement.Clear();
                this.rootVisualElement.Add(new HelpBox("MiniMCP window UI is missing required elements.", HelpBoxMessageType.Error));
                return;
            }

            this.publicRelaySecretField.isPasswordField = true;

            this.portField.RegisterValueChangedCallback(evt => this.port = ClampPort(evt.newValue));
            this.relayPortField.RegisterValueChangedCallback(evt => this.relayPort = ClampPort(evt.newValue));
            this.consoleLogsToggle.RegisterValueChangedCallback(evt =>
            {
                this.consoleLogsEnabled = evt.newValue;
                EditorPrefs.SetBool(ConsoleLogsKey, this.consoleLogsEnabled);
            });
            this.publicRelayEnabledToggle.RegisterValueChangedCallback(evt =>
            {
                this.publicRelayEnabled = evt.newValue;
                MiniMcpRelayService.PublicRelayEnabled = this.publicRelayEnabled;
                this.RefreshUiFromState();
            });
            this.publicRelayPortField.RegisterValueChangedCallback(evt =>
            {
                this.publicRelayPort = ClampPort(evt.newValue);
                MiniMcpRelayService.PublicRelayPort = this.publicRelayPort;
                this.RefreshUiFromState();
            });
            this.publicRelaySecretField.RegisterValueChangedCallback(evt =>
            {
                this.publicRelaySharedSecret = evt.newValue ?? string.Empty;
                MiniMcpRelayService.PublicRelaySharedSecret = this.publicRelaySharedSecret;
                this.RefreshUiFromState();
            });

            this.startButton.clicked += this.StartMiniMcp;
            this.stopButton.clicked += this.StopMiniMcp;
            this.refreshToolsButton.clicked += this.HandleRefreshTools;

            this.RefreshUiFromState();
            this.RebuildToolList();
        }

        private static void EnqueueLog(string message)
        {
            lock (LogLock)
            {
                PendingLogs.Enqueue(message);
                if (PendingLogs.Count > 500)
                {
                    PendingLogs.Dequeue();
                }
            } 
        }

        private void OnEditorUpdate()
        {
            lock (LogLock)
            {
                while (PendingLogs.Count > 0)
                {
                    string nextLog = PendingLogs.Dequeue();
                    if (this.consoleLogsEnabled)
                    {
                        Debug.Log("[MiniMCP] " + nextLog);
                    }
                }
            }

            this.RefreshRelayState();
            this.RefreshStatusLabels();
        }

        private void RefreshToolList()
        {
            MiniMcpToolRegistry.ReloadTools();
            var descriptors = MiniMcpToolRegistry.GetToolDescriptors();
            this.toolDescriptors.Clear();
            for (var i = 0; i < descriptors.Count; i++)
            {
                this.toolDescriptors.Add(descriptors[i]);
            }

            this.RebuildToolList();
        }

        private void RefreshRelayState()
        {
            MiniMcpRelayService.GetCachedRelayState(out this.relayReachable, out this.relayTarget);
        }

        private void StartMiniMcp()
        {
            bool backendStarted = MiniMcpEditorService.Start(this.port);
            if (!backendStarted)
            {
                this.SetNotice("Backend start failed.");
                return;
            }

            int relayBackendPort = MiniMcpEditorService.ActivePort;
            if (!MiniMcpRelayService.StartRelay(this.relayPort, relayBackendPort))
            {
                this.SetNotice("Relay start failed.");
            }
            else
            {
                this.SetNotice("MiniMCP started.");
            }

            this.RefreshRelayState();
            this.RefreshUiFromState();
        }

        private void StopMiniMcp()
        {
            MiniMcpEditorService.Stop("ui-stop-button");
            if (!MiniMcpRelayService.StopRelay("ui-stop-button"))
            {
                this.SetNotice("Relay stop could not be fully confirmed.");
            }
            else
            {
                this.SetNotice("MiniMCP stopped.");
            }

            this.RefreshRelayState();
            this.RefreshUiFromState();
        }

        private void HandleRefreshTools()
        {
            this.RefreshToolList();
            this.SetNotice($"Tool scan refreshed ({this.toolDescriptors.Count}).");
        }

        private void RefreshUiFromState()
        {
            if (this.portField == null)
            {
                return;
            }

            this.portField.SetValueWithoutNotify(this.port);
            this.relayPortField.SetValueWithoutNotify(this.relayPort);
            this.consoleLogsToggle.SetValueWithoutNotify(this.consoleLogsEnabled);
            this.publicRelayEnabledToggle.SetValueWithoutNotify(this.publicRelayEnabled);
            this.publicRelayPortField.SetValueWithoutNotify(this.publicRelayPort);
            this.publicRelaySecretField.SetValueWithoutNotify(this.publicRelaySharedSecret ?? string.Empty);
            this.RefreshStatusLabels();
        }

        private void RefreshStatusLabels()
        {
            if (this.backendStatusValue == null)
            {
                return;
            }

            this.backendStatusValue.text = MiniMcpEditorService.BackendStatus;
            this.relayStatusValue.text = this.relayReachable ? "Running" : "Stopped";
            this.relayEndpointValue.text = $"http://127.0.0.1:{this.relayPort}/mcp";
            this.relayTargetValue.text = this.relayTarget;
            this.backendEndpointValue.text = $"http://127.0.0.1:{MiniMcpEditorService.ActivePort}/mcp";
            this.publicUpstreamValue.text = this.publicRelayEnabled
                ? $"http://0.0.0.0:{this.publicRelayPort}/mcp ({(string.IsNullOrWhiteSpace(this.publicRelaySharedSecret) ? "missing secret" : "secret configured")})"
                : "disabled";
            this.stopButton.SetEnabled(this.relayReachable || MiniMcpEditorService.IsEnabledForCurrentSession || MiniMcpEditorService.IsRunning);
            this.toolCountLabel.text = $"Count: {this.toolDescriptors.Count}";
        }

        private void RebuildToolList()
        {
            if (this.toolListContainer == null)
            {
                return;
            }

            this.toolListContainer.Clear();
            var groupedDescriptors = this.toolDescriptors
                .GroupBy(descriptor => string.IsNullOrWhiteSpace(descriptor.Group) ? "Ungrouped" : descriptor.Group)
                .OrderBy(group => string.Equals(group.Key, "Ungrouped", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int groupIndex = 0; groupIndex < groupedDescriptors.Count; groupIndex++)
            {
                IGrouping<string, MiniMcpToolDescriptor> group = groupedDescriptors[groupIndex];
                Foldout foldout = new Foldout
                {
                    text = $"{group.Key} ({group.Count()})",
                    value = true
                };

                for (int itemIndex = 0; itemIndex < group.Count(); itemIndex++)
                {
                    MiniMcpToolDescriptor descriptor = group.ElementAt(itemIndex);
                    VisualElement item = new VisualElement();
                    item.AddToClassList("mini-mcp-window__tool-item");

                    VisualElement row = new VisualElement();
                    row.AddToClassList("mini-mcp-window__tool-row");

                    Label nameLabel = new Label(descriptor.Name);
                    nameLabel.AddToClassList("mini-mcp-window__tool-name");
                    row.Add(nameLabel);

                    Toggle enabledToggle = new Toggle();
                    enabledToggle.SetValueWithoutNotify(descriptor.IsEnabled);
                    enabledToggle.RegisterValueChangedCallback(evt =>
                    {
                        MiniMcpToolRegistry.SetToolEnabled(descriptor.Name, evt.newValue);
                        this.RefreshToolList();
                    });
                    row.Add(enabledToggle);
                    item.Add(row);

                    Label typeLabel = new Label(descriptor.TypeName);
                    typeLabel.AddToClassList("mini-mcp-window__tool-type");
                    item.Add(typeLabel);

                    Label descriptionLabel = new Label(descriptor.Description ?? string.Empty);
                    descriptionLabel.AddToClassList("mini-mcp-window__tool-description");
                    item.Add(descriptionLabel);

                    foldout.Add(item);
                }

                this.toolListContainer.Add(foldout);
            }

            this.toolCountLabel.text = $"Count: {this.toolDescriptors.Count}";
        }

        private void SetNotice(string message)
        {
            if (this.noticeLabel != null)
            {
                this.noticeLabel.text = message ?? string.Empty;
            }
        }

        private static int ClampPort(int value)
        {
            return Mathf.Clamp(value, 1, 65535);
        }
    }
}
