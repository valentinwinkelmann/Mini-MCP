using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#if UNITY_EDITOR
using UnityEngine;
#endif

namespace MiniMCP
{
    public static class MiniMcpToolRegistry
    {
        private sealed class RegisteredTool
        {
            public MiniMcpToolDescriptor Descriptor;
            public MiniMcpToolBase Instance;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, RegisteredTool> Tools = new Dictionary<string, RegisteredTool>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DisabledTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static MiniMcpToolRegistry()
        {
            ReloadTools();
        }

        public static void ReloadTools()
        {
            lock (Gate)
            {
                var existingDisabled = new HashSet<string>(DisabledTools, StringComparer.OrdinalIgnoreCase);
                Tools.Clear();

                foreach (var type in EnumerateToolTypes())
                {
                    var attribute = type.GetCustomAttribute<MiniMcpToolAttribute>();
                    if (attribute == null)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(attribute.Name))
                    {
                        continue;
                    }

                    if (Tools.ContainsKey(attribute.Name))
                    {
                        continue;
                    }

                    MiniMcpToolBase instance;
                    try
                    {
                        instance = Activator.CreateInstance(type) as MiniMcpToolBase;
                    }
                    catch
                    {
                        continue;
                    }

                    if (instance == null)
                    {
                        continue;
                    }

                    WarnIfThreadingIsNotValidated(type, attribute);

                    var descriptor = new MiniMcpToolDescriptor
                    {
                        Name = attribute.Name,
                        Description = attribute.Description,
                        InputSchemaJson = instance is IMiniMcpToolSchemaProvider schemaProvider
                            ? schemaProvider.GetInputSchemaJson()
                            : attribute.InputSchemaJson,
                        Group = attribute.Group ?? string.Empty,
                        TypeName = type.FullName ?? type.Name,
                        IsEnabled = !existingDisabled.Contains(attribute.Name),
                        SupportsAwait = attribute.SupportsAwait,
                        AwaitKind = attribute.AwaitKind ?? string.Empty,
                        DefaultAwaitTimeoutMs = attribute.DefaultAwaitTimeoutMs,
                        MaxAwaitTimeoutMs = attribute.MaxAwaitTimeoutMs
                    };

                    if (!descriptor.IsEnabled)
                    {
                        DisabledTools.Add(descriptor.Name);
                    }

                    Tools.Add(descriptor.Name, new RegisteredTool
                    {
                        Descriptor = descriptor,
                        Instance = instance
                    });
                }

                var validNames = new HashSet<string>(Tools.Keys, StringComparer.OrdinalIgnoreCase);
                DisabledTools.RemoveWhere(name => !validNames.Contains(name));
            }
        }

        public static IReadOnlyList<MiniMcpToolDescriptor> GetToolDescriptors()
        {
            lock (Gate)
            {
                return Tools.Values
                    .Select(t => CloneDescriptor(t.Descriptor))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static IReadOnlyList<MiniMcpToolDescriptor> GetEnabledToolDescriptors()
        {
            lock (Gate)
            {
                return Tools.Values
                    .Where(t => t.Descriptor.IsEnabled)
                    .Select(t => CloneDescriptor(t.Descriptor))
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public static bool SetToolEnabled(string toolName, bool enabled)
        {
            lock (Gate)
            {
                if (!Tools.TryGetValue(toolName, out var tool))
                {
                    return false;
                }

                tool.Descriptor.IsEnabled = enabled;
                if (enabled)
                {
                    DisabledTools.Remove(toolName);
                }
                else
                {
                    DisabledTools.Add(toolName);
                }

                return true;
            }
        }

        public static bool TryInvokeTool(string toolName, string argumentsJson, out MiniMcpToolCallResult result)
        {
            lock (Gate)
            {
                if (!Tools.TryGetValue(toolName, out var tool))
                {
                    result = MiniMcpToolCallResult.Error("Unknown tool");
                    return false;
                }

                if (!tool.Descriptor.IsEnabled)
                {
                    result = MiniMcpToolCallResult.Error("Tool is disabled");
                    return false;
                }

                try
                {
                    result = tool.Instance.Execute(argumentsJson ?? "{}");
                    if (result == null)
                    {
                        result = MiniMcpToolCallResult.Ok(string.Empty);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    result = MiniMcpToolCallResult.Error($"Tool execution failed: {ex.Message}");
                    return true;
                }
            }
        }

        private static IEnumerable<Type> EnumerateToolTypes()
        {
            var baseType = typeof(MiniMcpToolBase);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }

                for (var i = 0; i < types.Length; i++)
                {
                    var type = types[i];
                    if (type == null || !baseType.IsAssignableFrom(type) || type.IsAbstract)
                    {
                        continue;
                    }

                    yield return type;
                }
            }
        }

        private static MiniMcpToolDescriptor CloneDescriptor(MiniMcpToolDescriptor descriptor)
        {
            return new MiniMcpToolDescriptor
            {
                Name = descriptor.Name,
                Description = descriptor.Description,
                InputSchemaJson = descriptor.InputSchemaJson,
                Group = descriptor.Group,
                TypeName = descriptor.TypeName,
                IsEnabled = descriptor.IsEnabled,
                SupportsAwait = descriptor.SupportsAwait,
                AwaitKind = descriptor.AwaitKind,
                DefaultAwaitTimeoutMs = descriptor.DefaultAwaitTimeoutMs,
                MaxAwaitTimeoutMs = descriptor.MaxAwaitTimeoutMs
            };
        }

        private static void WarnIfThreadingIsNotValidated(Type type, MiniMcpToolAttribute attribute)
        {
#if UNITY_EDITOR
            if (type == null || attribute == null)
            {
                return;
            }

            if (typeof(IMiniMcpToolThreadingValidated).IsAssignableFrom(type))
            {
                return;
            }

            string namespaceText = type.Namespace ?? string.Empty;
            if (!namespaceText.StartsWith("MiniMCP.Tools", StringComparison.Ordinal))
            {
                return;
            }

            Debug.LogWarning($"MiniMCP tool '{attribute.Name}' ({type.FullName}) does not implement IMiniMcpToolThreadingValidated. Derive from a shared main-thread base or mark an intentional mixed-thread implementation.");
#endif
        }
    }
}
