using System;
using System.Reflection;
using MiniMCP;
using UnityEditor;

namespace MiniMCP.Kanban.Editor
{
    internal static class KanbanUserContext
    {
        internal readonly struct Identity
        {
            public Identity(string displayName, string id, string kind)
            {
                this.DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Editor User" : displayName.Trim();
                this.Id = id ?? string.Empty;
                this.Kind = string.IsNullOrWhiteSpace(kind) ? "editor" : kind.Trim();
            }

            public string DisplayName { get; }

            public string Id { get; }

            public string Kind { get; }
        }

        public static Identity GetCurrentEditorIdentity()
        {
            try
            {
                Type unityConnectType = FindUnityConnectType();
                if (unityConnectType != null)
                {
                    object unityConnectInstance = GetUnityConnectInstance(unityConnectType) ?? CreateUnityConnectInstance(unityConnectType);
                    if (TryCreateIdentity(unityConnectInstance, out Identity identity))
                    {
                        return identity;
                    }
                }
            }
            catch
            {
            }

            return new Identity(Environment.UserName, Environment.UserName, "editor");
        }

        public static Identity CreateMcpIdentity()
        {
            return new Identity("MCP", "mcp", "mcp");
        }

        private static Type FindUnityConnectType()
        {
            Type unityConnectType = Type.GetType("UnityEditor.Connect.UnityConnect, UnityEditor.UnityConnectModule", false)
                ?? Type.GetType("UnityEditor.Connect.UnityConnect, UnityEditor", false);
            if (unityConnectType != null)
            {
                return unityConnectType;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                unityConnectType = assemblies[index].GetType("UnityEditor.Connect.UnityConnect", false);
                if (unityConnectType != null)
                {
                    return unityConnectType;
                }
            }

            return null;
        }

        private static object GetUnityConnectInstance(Type unityConnectType)
        {
            PropertyInfo instanceProperty = unityConnectType.GetProperty("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return instanceProperty?.GetValue(null);
        }

        private static object CreateUnityConnectInstance(Type unityConnectType)
        {
            ConstructorInfo constructor = unityConnectType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (constructor != null)
            {
                return constructor.Invoke(null);
            }

            return typeof(EditorWindow).Assembly.CreateInstance(
                unityConnectType.FullName,
                false,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                args: null,
                culture: null,
                activationAttributes: null);
        }

        private static bool TryCreateIdentity(object unityConnectInstance, out Identity identity)
        {
            identity = default;
            if (unityConnectInstance == null)
            {
                return false;
            }

            Type unityConnectType = unityConnectInstance.GetType();
            if (TryGetBooleanMember(unityConnectType, unityConnectInstance, out bool loggedIn, "loggedIn", "m_LoggedIn") && !loggedIn)
            {
                return false;
            }

            object userInfo = GetMemberValue(unityConnectType, unityConnectInstance, "userInfo", "m_UserInfo");
            if (userInfo == null)
            {
                return false;
            }

            Type userInfoType = userInfo.GetType();
            string displayName = GetStringMember(userInfoType, userInfo, "displayName", "m_DisplayName", "userName", "m_UserName");
            string userId = GetStringMember(userInfoType, userInfo, "userId", "m_UserId");
            if (string.IsNullOrWhiteSpace(displayName) && string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            identity = new Identity(displayName, userId, "editor");
            return true;
        }

        private static string GetStringMember(Type targetType, object instance, params string[] memberNames)
        {
            foreach (string memberName in memberNames)
            {
                object value = GetMemberValue(targetType, instance, memberName);
                if (value is string stringValue)
                {
                    return stringValue;
                }
            }

            return string.Empty;
        }

        private static bool TryGetBooleanMember(Type targetType, object instance, out bool value, params string[] memberNames)
        {
            foreach (string memberName in memberNames)
            {
                object candidate = GetMemberValue(targetType, instance, memberName);
                if (candidate is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }
            }

            value = false;
            return false;
        }

        private static object GetMemberValue(Type targetType, object instance, params string[] memberNames)
        {
            foreach (string memberName in memberNames)
            {
                PropertyInfo property = targetType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null)
                {
                    return property.GetValue(instance);
                }

                FieldInfo field = targetType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    return field.GetValue(instance);
                }
            }

            return null;
        }
    }
}