using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using MiniMCP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MiniMCP.Tools
{
    internal static class MiniMcpSceneToolSupport
    {
        private static readonly BindingFlags PublicInstanceFlags = BindingFlags.Instance | BindingFlags.Public;
        private static readonly char[] ValueSeparators = { ',', ';', '|' };

        private sealed class SceneOperationRequest
        {
            public string Action;
            public string TargetObjectId;
            public string TargetPath;
            public string ParentObjectId;
            public string ParentPath;
            public string Name;
            public string PrimitiveType;
            public string ComponentType;
            public string MemberName;
            public string PropertyValue;
            public string Position;
            public string Rotation;
            public string Scale;
            public int ComponentIndex;
            public bool UseWorldSpace;
        }

        public static string BuildHierarchyResult(string rootObjectId, string rootPath, int maxDepth, bool includeComponents, bool includeInactive, string viewMode)
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return BuildValidationResult("scene_unavailable", "No valid active scene is available.", "scene_unavailable");
            }

            List<GameObject> roots = ResolveQueryRoots(scene, rootObjectId, rootPath, out GameObject resolvedRoot, out string resolutionError);
            if (roots == null)
            {
                return BuildValidationResult("root_not_found", resolutionError, "root_not_found");
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"ok\",\"scene\":");
            AppendSceneJson(builder, scene);
            builder.Append(",\"query\":{");
            builder.Append("\"rootObjectId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(rootObjectId ?? string.Empty));
            builder.Append("\",\"rootPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(rootPath ?? string.Empty));
            builder.Append("\",\"resolvedRootObjectId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(GetObjectId(resolvedRoot) ?? string.Empty));
            builder.Append("\",\"resolvedRootPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(BuildHierarchyPath(resolvedRoot) ?? string.Empty));
            builder.Append("\",\"maxDepth\":");
            builder.Append(maxDepth);
            builder.Append(",\"includeComponents\":");
            builder.Append(includeComponents ? "true" : "false");
            builder.Append(",\"includeInactive\":");
            builder.Append(includeInactive ? "true" : "false");
            builder.Append(",\"viewMode\":\"");
            builder.Append(MiniMcpJson.EscapeJson(viewMode ?? "full"));
            builder.Append("\"");
            builder.Append("},\"objects\":[");

            int objectCount = 0;
            bool firstObject = true;
            foreach (GameObject root in roots)
            {
                AppendHierarchyObjects(builder, root, 0, maxDepth, includeComponents, includeInactive, viewMode, ref firstObject, ref objectCount);
            }

            builder.Append("],\"objectCount\":");
            builder.Append(objectCount);
            builder.Append('}');
            return builder.ToString();
        }

        public static string CreateGameObject(string name, string parentObjectId, string parentPath, string position, string rotation, string scale, bool useWorldSpace, bool preview)
        {
            GameObject parent = ResolveTargetObject(parentObjectId, parentPath, out string resolutionError);
            if (!string.IsNullOrEmpty(resolutionError))
            {
                return BuildValidationResult("parent_not_found", resolutionError, "parent_not_found");
            }

            if (!TryParseOptionalTransform(position, rotation, scale, out bool hasPosition, out Vector3 parsedPosition, out bool hasRotation, out Vector3 parsedRotation, out bool hasScale, out Vector3 parsedScale, out string transformError))
            {
                return BuildValidationResult("transform_parse_failed", transformError, "transform_parse_failed");
            }

            string objectName = string.IsNullOrWhiteSpace(name) ? "GameObject" : name.Trim();
            if (preview)
            {
                return BuildCreatePreviewResult("preview_create_game_object", "GameObject creation preview ready.", objectName, parent, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace, string.Empty);
            }

            GameObject gameObject = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create GameObject via MCP");
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent GameObject via MCP");
            }

            ApplyTransformValues(gameObject.transform, parent != null, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace);

            PersistObjectChange(gameObject);
            return BuildObjectMutationResult("created_game_object", "GameObject created.", gameObject, null);
        }

        public static string CreatePrimitive(string primitiveType, string name, string parentObjectId, string parentPath, string position, string rotation, string scale, bool useWorldSpace, bool preview)
        {
            if (!TryParsePrimitiveType(primitiveType, out PrimitiveType parsedPrimitive))
            {
                return BuildValidationResult("primitive_not_supported", "Unknown primitiveType. Use Sphere, Capsule, Cylinder, Cube, Plane, or Quad.", "primitive_not_supported");
            }

            GameObject parent = ResolveTargetObject(parentObjectId, parentPath, out string resolutionError);
            if (!string.IsNullOrEmpty(resolutionError))
            {
                return BuildValidationResult("parent_not_found", resolutionError, "parent_not_found");
            }

            if (!TryParseOptionalTransform(position, rotation, scale, out bool hasPosition, out Vector3 parsedPosition, out bool hasRotation, out Vector3 parsedRotation, out bool hasScale, out Vector3 parsedScale, out string transformError))
            {
                return BuildValidationResult("transform_parse_failed", transformError, "transform_parse_failed");
            }

            if (preview)
            {
                string objectName = string.IsNullOrWhiteSpace(name) ? parsedPrimitive.ToString() : name.Trim();
                return BuildCreatePreviewResult("preview_create_primitive", "Primitive creation preview ready.", objectName, parent, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace, parsedPrimitive.ToString());
            }

            GameObject gameObject = GameObject.CreatePrimitive(parsedPrimitive);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Primitive via MCP");
            if (!string.IsNullOrWhiteSpace(name))
            {
                gameObject.name = name.Trim();
            }

            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent Primitive via MCP");
            }

            ApplyTransformValues(gameObject.transform, parent != null, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace);

            PersistObjectChange(gameObject);
            return BuildObjectMutationResult("created_primitive", "Primitive created.", gameObject, null);
        }

        public static string AddComponent(string targetObjectId, string targetPath, string componentTypeName, bool preview)
        {
            if (string.IsNullOrWhiteSpace(targetObjectId) && string.IsNullOrWhiteSpace(targetPath))
            {
                return BuildValidationResult("target_required", "targetObjectId or targetPath is required for add_component.", "target_required");
            }

            GameObject target = ResolveTargetObject(targetObjectId, targetPath, out string resolutionError);
            if (target == null)
            {
                return BuildValidationResult("target_not_found", resolutionError, "target_not_found");
            }

            Type componentType = ResolveComponentType(componentTypeName, out string typeError);
            if (componentType == null)
            {
                return BuildValidationResult("component_type_not_found", typeError, "component_type_not_found");
            }

            if (typeof(Transform).IsAssignableFrom(componentType))
            {
                return BuildValidationResult("component_not_allowed", "Transform-derived components cannot be added explicitly to a GameObject.", "component_not_allowed");
            }

            if (preview)
            {
                return BuildComponentPreviewResult("preview_add_component", "Component addition preview ready.", target, componentType, componentType.Name, null, null);
            }

            try
            {
                Component component = Undo.AddComponent(target, componentType);
                PersistObjectChange(target);
                return BuildObjectMutationResult("component_added", "Component added.", target, component);
            }
            catch (Exception ex)
            {
                return BuildValidationResult("component_add_failed", ex.Message, "component_add_failed");
            }
        }

        public static string SetComponentMember(string targetObjectId, string targetPath, string componentTypeName, int componentIndex, string memberName, string rawValue, bool preview)
        {
            if (string.IsNullOrWhiteSpace(targetObjectId) && string.IsNullOrWhiteSpace(targetPath))
            {
                return BuildValidationResult("target_required", "targetObjectId or targetPath is required for set_component_member.", "target_required");
            }

            GameObject target = ResolveTargetObject(targetObjectId, targetPath, out string resolutionError);
            if (target == null)
            {
                return BuildValidationResult("target_not_found", resolutionError, "target_not_found");
            }

            if (string.IsNullOrWhiteSpace(componentTypeName))
            {
                return BuildValidationResult("component_type_required", "componentType is required for set_component_member.", "component_type_required");
            }

            if (string.IsNullOrWhiteSpace(memberName))
            {
                return BuildValidationResult("member_required", "propertyName is required for set_component_member.", "member_required");
            }

            Type componentType = ResolveComponentType(componentTypeName, out string typeError);
            if (componentType == null)
            {
                return BuildValidationResult("component_type_not_found", typeError, "component_type_not_found");
            }

            Component component = ResolveComponentInstance(target, componentType, componentIndex, out string componentError);
            if (component == null)
            {
                return BuildValidationResult("component_not_found", componentError, "component_not_found");
            }

            MemberInfo member = ResolveWritableMember(component.GetType(), memberName, out string memberError);
            if (member == null)
            {
                return BuildValidationResult("member_not_found", memberError, "member_not_found");
            }

            Type memberType = GetMemberType(member);
            if (!IsSupportedValueType(memberType))
            {
                return BuildValidationResult("member_not_supported", "The requested member exists but is not supported by MCP value conversion.", "member_not_supported");
            }

            if (!TryConvertValue(rawValue, memberType, out object parsedValue, out string parseError))
            {
                return BuildValidationResult("value_parse_failed", parseError, "value_parse_failed");
            }

            if (preview)
            {
                return BuildComponentPreviewResult("preview_set_component_member", "Component member update preview ready.", target, component.GetType(), member.Name, rawValue, component);
            }

            try
            {
                Undo.RecordObject(component, "Set Component Member via MCP");
                SetMemberValue(component, member, parsedValue);
                EditorUtility.SetDirty(component);
                PersistObjectChange(target);
                return BuildObjectMutationResult("member_set", "Component member updated.", target, component, member.Name, rawValue);
            }
            catch (Exception ex)
            {
                return BuildValidationResult("member_set_failed", ex.Message, "member_set_failed");
            }
        }

        public static string SetTransform(string targetObjectId, string targetPath, string position, string rotation, string scale, bool useWorldSpace, bool preview)
        {
            if (string.IsNullOrWhiteSpace(targetObjectId) && string.IsNullOrWhiteSpace(targetPath))
            {
                return BuildValidationResult("target_required", "targetObjectId or targetPath is required for set_transform.", "target_required");
            }

            if (!TryParseOptionalTransform(position, rotation, scale, out bool hasPosition, out Vector3 parsedPosition, out bool hasRotation, out Vector3 parsedRotation, out bool hasScale, out Vector3 parsedScale, out string transformError))
            {
                return BuildValidationResult("transform_parse_failed", transformError, "transform_parse_failed");
            }

            if (!hasPosition && !hasRotation && !hasScale)
            {
                return BuildValidationResult("transform_required", "At least one of position, rotation, or scale is required for set_transform.", "transform_required");
            }

            GameObject target = ResolveTargetObject(targetObjectId, targetPath, out string resolutionError);
            if (target == null)
            {
                return BuildValidationResult("target_not_found", resolutionError, "target_not_found");
            }

            if (preview)
            {
                return BuildTransformPreviewResult("preview_set_transform", "Transform update preview ready.", target, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace);
            }

            Undo.RecordObject(target.transform, "Set Transform via MCP");
            ApplyTransformValues(target.transform, false, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale, useWorldSpace);
            EditorUtility.SetDirty(target.transform);
            PersistObjectChange(target);
            return BuildObjectMutationResult("transform_updated", "Transform updated.", target, target.transform);
        }

        public static string RenameObject(string targetObjectId, string targetPath, string name, bool preview)
        {
            if (string.IsNullOrWhiteSpace(targetObjectId) && string.IsNullOrWhiteSpace(targetPath))
            {
                return BuildValidationResult("target_required", "targetObjectId or targetPath is required for rename_object.", "target_required");
            }

            GameObject target = ResolveTargetObject(targetObjectId, targetPath, out string resolutionError);
            if (target == null)
            {
                return BuildValidationResult("target_not_found", resolutionError, "target_not_found");
            }

            string nextName = string.IsNullOrWhiteSpace(name) ? "GameObject" : name.Trim();
            if (preview)
            {
                return BuildRenamePreviewResult(target, nextName);
            }

            Undo.RecordObject(target, "Rename GameObject via MCP");
            target.name = nextName;
            PersistObjectChange(target);
            return BuildObjectMutationResult("renamed_object", "GameObject renamed.", target, null);
        }

        public static string ExecuteBatchOperations(string operationsJson, bool preview)
        {
            if (!MiniMcpJson.TrySplitTopLevelArrayElements(operationsJson, out List<string> operations))
            {
                return BuildValidationResult("operations_invalid", "operations must be a valid JSON array.", "operations_invalid");
            }

            if (operations.Count == 0)
            {
                return BuildValidationResult("operations_required", "batch_mutate requires at least one operation.", "operations_required");
            }

            int undoGroup = -1;
            if (!preview)
            {
                Undo.IncrementCurrentGroup();
                undoGroup = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("Scene Batch via MCP");
            }

            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(preview ? "batch_preview" : "batch_applied");
            builder.Append("\",\"applied\":");
            builder.Append(preview ? "false" : "true");
            builder.Append(",\"preview\":");
            builder.Append(preview ? "true" : "false");
            builder.Append(",\"operationCount\":");
            builder.Append(operations.Count);
            builder.Append(",\"results\":[");

            int appliedCount = 0;
            int failedOperationIndex = -1;
            bool first = true;
            for (int index = 0; index < operations.Count; index++)
            {
                string result = ExecuteOperation(ParseOperationRequest(operations[index]), preview);
                if (!first)
                {
                    builder.Append(',');
                }

                builder.Append(result);
                first = false;

                if (MiniMcpJson.TryExtractBoolProperty(result, "applied", out bool operationApplied) && operationApplied)
                {
                    appliedCount++;
                    continue;
                }

                if (preview)
                {
                    if (MiniMcpJson.TryExtractStringProperty(result, "status", out string previewStatus) && previewStatus.StartsWith("preview_", StringComparison.Ordinal))
                    {
                        appliedCount++;
                        continue;
                    }
                }

                failedOperationIndex = index;
                if (!preview && undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }

                break;
            }

            builder.Append(']');
            builder.Append(",\"appliedCount\":");
            builder.Append(appliedCount);
            builder.Append(",\"failedOperationIndex\":");
            builder.Append(failedOperationIndex);
            builder.Append(",\"rolledBack\":");
            builder.Append(!preview && failedOperationIndex >= 0 ? "true" : "false");
            builder.Append(",\"message\":\"");
            builder.Append(MiniMcpJson.EscapeJson(failedOperationIndex >= 0 ? "Batch processing stopped at the first invalid operation." : (preview ? "Batch preview ready." : "Batch applied.")));
            builder.Append("\"}");

            if (!preview && undoGroup >= 0 && failedOperationIndex < 0)
            {
                Undo.CollapseUndoOperations(undoGroup);
            }

            return builder.ToString();
        }

        private static SceneOperationRequest ParseOperationRequest(string operationJson)
        {
            SceneOperationRequest request = new SceneOperationRequest();
            MiniMcpJson.TryExtractStringProperty(operationJson, "action", out request.Action);
            MiniMcpJson.TryExtractStringProperty(operationJson, "targetObjectId", out request.TargetObjectId);
            MiniMcpJson.TryExtractStringProperty(operationJson, "targetPath", out request.TargetPath);
            MiniMcpJson.TryExtractStringProperty(operationJson, "parentObjectId", out request.ParentObjectId);
            MiniMcpJson.TryExtractStringProperty(operationJson, "parentPath", out request.ParentPath);
            MiniMcpJson.TryExtractStringProperty(operationJson, "name", out request.Name);
            MiniMcpJson.TryExtractStringProperty(operationJson, "primitiveType", out request.PrimitiveType);
            MiniMcpJson.TryExtractStringProperty(operationJson, "componentType", out request.ComponentType);
            MiniMcpJson.TryExtractStringProperty(operationJson, "propertyName", out request.MemberName);
            MiniMcpJson.TryExtractStringProperty(operationJson, "propertyValue", out request.PropertyValue);
            MiniMcpJson.TryExtractStringProperty(operationJson, "position", out request.Position);
            MiniMcpJson.TryExtractStringProperty(operationJson, "rotation", out request.Rotation);
            MiniMcpJson.TryExtractStringProperty(operationJson, "scale", out request.Scale);
            MiniMcpJson.TryExtractIntProperty(operationJson, "componentIndex", out request.ComponentIndex);
            MiniMcpJson.TryExtractBoolProperty(operationJson, "useWorldSpace", out request.UseWorldSpace);
            return request;
        }

        private static string ExecuteOperation(SceneOperationRequest request, bool preview)
        {
            string action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
            switch (action)
            {
                case "create_game_object":
                    return CreateGameObject(request.Name, request.ParentObjectId, request.ParentPath, request.Position, request.Rotation, request.Scale, request.UseWorldSpace, preview);
                case "create_primitive":
                    return CreatePrimitive(request.PrimitiveType, request.Name, request.ParentObjectId, request.ParentPath, request.Position, request.Rotation, request.Scale, request.UseWorldSpace, preview);
                case "add_component":
                    return AddComponent(request.TargetObjectId, request.TargetPath, request.ComponentType, preview);
                case "set_component_member":
                    return SetComponentMember(request.TargetObjectId, request.TargetPath, request.ComponentType, request.ComponentIndex, request.MemberName, request.PropertyValue, preview);
                case "set_transform":
                    return SetTransform(request.TargetObjectId, request.TargetPath, request.Position, request.Rotation, request.Scale, request.UseWorldSpace, preview);
                case "rename_object":
                    return RenameObject(request.TargetObjectId, request.TargetPath, request.Name, preview);
                default:
                    return BuildValidationResult("invalid_action", "Invalid action in batch operation.", "invalid_action");
            }
        }

        private static bool TryParseOptionalTransform(string position, string rotation, string scale, out bool hasPosition, out Vector3 parsedPosition, out bool hasRotation, out Vector3 parsedRotation, out bool hasScale, out Vector3 parsedScale, out string error)
        {
            hasPosition = false;
            parsedPosition = Vector3.zero;
            hasRotation = false;
            parsedRotation = Vector3.zero;
            hasScale = false;
            parsedScale = Vector3.one;
            error = string.Empty;

            if (!TryParseOptionalVector3(position, "position", out hasPosition, out parsedPosition, out error))
            {
                return false;
            }

            if (!TryParseOptionalVector3(rotation, "rotation", out hasRotation, out parsedRotation, out error))
            {
                return false;
            }

            if (!TryParseOptionalVector3(scale, "scale", out hasScale, out parsedScale, out error))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseOptionalVector3(string rawValue, string label, out bool hasValue, out Vector3 parsedValue, out string error)
        {
            hasValue = false;
            parsedValue = Vector3.zero;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return true;
            }

            if (!TryConvertValue(rawValue, typeof(Vector3), out object convertedValue, out error))
            {
                error = label + " must be a comma-separated x,y,z vector.";
                return false;
            }

            parsedValue = (Vector3)convertedValue;
            hasValue = true;
            return true;
        }

        private static void ApplyTransformValues(Transform transform, bool resetLocalDefaults, bool hasPosition, Vector3 parsedPosition, bool hasRotation, Vector3 parsedRotation, bool hasScale, Vector3 parsedScale, bool useWorldSpace)
        {
            if (transform == null)
            {
                return;
            }

            if (hasPosition)
            {
                if (useWorldSpace)
                {
                    transform.position = parsedPosition;
                }
                else
                {
                    transform.localPosition = parsedPosition;
                }
            }
            else if (resetLocalDefaults)
            {
                transform.localPosition = Vector3.zero;
            }

            if (hasRotation)
            {
                if (useWorldSpace)
                {
                    transform.eulerAngles = parsedRotation;
                }
                else
                {
                    transform.localEulerAngles = parsedRotation;
                }
            }
            else if (resetLocalDefaults)
            {
                transform.localEulerAngles = Vector3.zero;
            }

            if (hasScale)
            {
                transform.localScale = parsedScale;
            }
            else if (resetLocalDefaults)
            {
                transform.localScale = Vector3.one;
            }
        }

        private static string BuildCreatePreviewResult(string status, string message, string objectName, GameObject parent, bool hasPosition, Vector3 parsedPosition, bool hasRotation, Vector3 parsedRotation, bool hasScale, Vector3 parsedScale, bool useWorldSpace, string primitiveType)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(status));
            builder.Append("\",\"applied\":false,\"preview\":true,\"message\":\"");
            builder.Append(MiniMcpJson.EscapeJson(message));
            builder.Append("\",\"objectPreview\":{");
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(objectName ?? string.Empty));
            builder.Append("\",\"parentObjectId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(parent != null ? GetObjectId(parent) : string.Empty));
            builder.Append("\",\"parentPath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(parent != null ? BuildHierarchyPath(parent) : string.Empty));
            builder.Append("\",\"primitiveType\":\"");
            builder.Append(MiniMcpJson.EscapeJson(primitiveType ?? string.Empty));
            builder.Append("\",\"useWorldSpace\":");
            builder.Append(useWorldSpace ? "true" : "false");
            builder.Append(",\"transform\":");
            AppendTransformPreviewJson(builder, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale);
            builder.Append("}}");
            return builder.ToString();
        }

        private static string BuildTransformPreviewResult(string status, string message, GameObject target, bool hasPosition, Vector3 parsedPosition, bool hasRotation, Vector3 parsedRotation, bool hasScale, Vector3 parsedScale, bool useWorldSpace)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(status));
            builder.Append("\",\"applied\":false,\"preview\":true,\"message\":\"");
            builder.Append(MiniMcpJson.EscapeJson(message));
            builder.Append("\",\"object\":");
            AppendGameObjectJson(builder, target, 0, 0, true, "full");
            builder.Append(",\"useWorldSpace\":");
            builder.Append(useWorldSpace ? "true" : "false");
            builder.Append(",\"transformPreview\":");
            AppendTransformPreviewJson(builder, hasPosition, parsedPosition, hasRotation, parsedRotation, hasScale, parsedScale);
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendTransformPreviewJson(StringBuilder builder, bool hasPosition, Vector3 parsedPosition, bool hasRotation, Vector3 parsedRotation, bool hasScale, Vector3 parsedScale)
        {
            builder.Append('{');
            builder.Append("\"hasPosition\":");
            builder.Append(hasPosition ? "true" : "false");
            builder.Append(",\"position\":");
            AppendVector3Json(builder, parsedPosition);
            builder.Append(",\"hasRotation\":");
            builder.Append(hasRotation ? "true" : "false");
            builder.Append(",\"rotation\":");
            AppendVector3Json(builder, parsedRotation);
            builder.Append(",\"hasScale\":");
            builder.Append(hasScale ? "true" : "false");
            builder.Append(",\"scale\":");
            AppendVector3Json(builder, parsedScale);
            builder.Append('}');
        }

        private static string BuildComponentPreviewResult(string status, string message, GameObject target, Type componentType, string memberName, string rawValue, Component component)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(status));
            builder.Append("\",\"applied\":false,\"preview\":true,\"message\":\"");
            builder.Append(MiniMcpJson.EscapeJson(message));
            builder.Append("\",\"object\":");
            AppendGameObjectJson(builder, target, 0, 0, true, "full");
            builder.Append(",\"componentPreview\":{");
            builder.Append("\"type\":\"");
            builder.Append(MiniMcpJson.EscapeJson(componentType != null ? (componentType.FullName ?? componentType.Name) : string.Empty));
            builder.Append("\",\"memberName\":\"");
            builder.Append(MiniMcpJson.EscapeJson(memberName ?? string.Empty));
            builder.Append("\",\"value\":\"");
            builder.Append(MiniMcpJson.EscapeJson(rawValue ?? string.Empty));
            builder.Append("\"");
            if (component != null)
            {
                builder.Append(",\"component\":");
                AppendComponentJson(builder, component, GetComponentIndex(target, component));
            }

            builder.Append("}}");
            return builder.ToString();
        }

        private static string BuildRenamePreviewResult(GameObject target, string nextName)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"preview_rename_object\",\"applied\":false,\"preview\":true,\"message\":\"Rename preview ready.\",\"object\":");
            AppendGameObjectJson(builder, target, 0, 0, true, "full");
            builder.Append(",\"nextName\":\"");
            builder.Append(MiniMcpJson.EscapeJson(nextName ?? string.Empty));
            builder.Append("\"}");
            return builder.ToString();
        }

        private static List<GameObject> ResolveQueryRoots(Scene scene, string rootObjectId, string rootPath, out GameObject resolvedRoot, out string error)
        {
            error = string.Empty;
            resolvedRoot = null;

            if (!string.IsNullOrWhiteSpace(rootObjectId) || !string.IsNullOrWhiteSpace(rootPath))
            {
                resolvedRoot = ResolveTargetObject(rootObjectId, rootPath, out error);
                if (resolvedRoot == null)
                {
                    return null;
                }

                return new List<GameObject> { resolvedRoot };
            }

            return new List<GameObject>(scene.GetRootGameObjects());
        }

        private static GameObject ResolveTargetObject(string objectId, string hierarchyPath, out string error)
        {
            error = string.Empty;

            if (!string.IsNullOrWhiteSpace(objectId))
            {
                UnityEngine.Object resolvedObject = ResolveObjectByGlobalId(objectId.Trim());
                if (resolvedObject is GameObject resolvedGameObject)
                {
                    return resolvedGameObject;
                }

                if (resolvedObject is Component resolvedComponent)
                {
                    return resolvedComponent.gameObject;
                }

                error = "No scene object was found for the requested objectId.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(hierarchyPath))
            {
                GameObject resolvedByPath = ResolveObjectByHierarchyPath(hierarchyPath.Trim());
                if (resolvedByPath != null)
                {
                    return resolvedByPath;
                }

                error = "No scene object was found for the requested hierarchy path.";
                return null;
            }

            return null;
        }

        private static UnityEngine.Object ResolveObjectByGlobalId(string objectId)
        {
            if (!GlobalObjectId.TryParse(objectId, out GlobalObjectId globalObjectId))
            {
                return null;
            }

            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalObjectId);
        }

        private static GameObject ResolveObjectByHierarchyPath(string path)
        {
            string normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            if (normalized.Length == 0)
            {
                return null;
            }

            string[] segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return null;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (!string.Equals(root.name, segments[0], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Transform current = root.transform;
                bool matched = true;
                for (int index = 1; index < segments.Length; index++)
                {
                    current = FindChildByName(current, segments[index]);
                    if (current == null)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched && current != null)
                {
                    return current.gameObject;
                }
            }

            return null;
        }

        private static Transform FindChildByName(Transform parent, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            for (int index = 0; index < parent.childCount; index++)
            {
                Transform child = parent.GetChild(index);
                if (string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        private static void AppendHierarchyObjects(StringBuilder builder, GameObject gameObject, int depth, int maxDepth, bool includeComponents, bool includeInactive, string viewMode, ref bool firstObject, ref int objectCount)
        {
            if (gameObject == null)
            {
                return;
            }

            if (!includeInactive && !gameObject.activeInHierarchy)
            {
                return;
            }

            if (!firstObject)
            {
                builder.Append(',');
            }

            AppendGameObjectJson(builder, gameObject, depth, maxDepth, includeComponents, viewMode);
            firstObject = false;
            objectCount++;

            if (depth >= maxDepth)
            {
                return;
            }

            Transform transform = gameObject.transform;
            for (int childIndex = 0; childIndex < transform.childCount; childIndex++)
            {
                AppendHierarchyObjects(builder, transform.GetChild(childIndex).gameObject, depth + 1, maxDepth, includeComponents, includeInactive, viewMode, ref firstObject, ref objectCount);
            }
        }

        private static void AppendSceneJson(StringBuilder builder, Scene scene)
        {
            builder.Append('{');
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(scene.name));
            builder.Append("\",\"path\":\"");
            builder.Append(MiniMcpJson.EscapeJson(scene.path));
            builder.Append("\",\"isDirty\":");
            builder.Append(scene.isDirty ? "true" : "false");
            builder.Append(",\"rootCount\":");
            builder.Append(scene.rootCount);
            builder.Append('}');
        }

        private static void AppendGameObjectJson(StringBuilder builder, GameObject gameObject, int depth, int maxDepth, bool includeComponents, string viewMode)
        {
            builder.Append('{');
            builder.Append("\"objectId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(GetObjectId(gameObject) ?? string.Empty));
            builder.Append("\",\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(gameObject.name ?? string.Empty));
            builder.Append("\",\"path\":\"");
            builder.Append(MiniMcpJson.EscapeJson(BuildHierarchyPath(gameObject) ?? string.Empty));
            builder.Append("\",\"depth\":");
            builder.Append(depth);
            builder.Append(",\"siblingIndex\":");
            builder.Append(gameObject.transform.GetSiblingIndex());
            builder.Append(",\"childCount\":");
            builder.Append(gameObject.transform.childCount);
            builder.Append(",\"hasMoreChildren\":");
            builder.Append(gameObject.transform.childCount > 0 && depth >= maxDepth ? "true" : "false");
            builder.Append(",\"activeSelf\":");
            builder.Append(gameObject.activeSelf ? "true" : "false");
            builder.Append(",\"activeInHierarchy\":");
            builder.Append(gameObject.activeInHierarchy ? "true" : "false");
            builder.Append(",\"tag\":\"");
            builder.Append(MiniMcpJson.EscapeJson(gameObject.tag));
            builder.Append("\",\"layer\":");
            builder.Append(gameObject.layer);
            builder.Append(",\"scenePath\":\"");
            builder.Append(MiniMcpJson.EscapeJson(gameObject.scene.path ?? string.Empty));
            builder.Append("\",\"parentObjectId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(gameObject.transform.parent != null ? GetObjectId(gameObject.transform.parent.gameObject) : string.Empty));
            builder.Append('\"');
            builder.Append(",\"viewMode\":\"");
            builder.Append(MiniMcpJson.EscapeJson(viewMode ?? "full"));
            builder.Append("\",\"transform\":");
            AppendTransformJson(builder, gameObject.transform);
            builder.Append(",\"layoutSummary\":");
            AppendLayoutSummaryJson(builder, gameObject);

            if (includeComponents)
            {
                builder.Append(",\"components\":[");
                Component[] components = gameObject.GetComponents<Component>();
                for (int index = 0; index < components.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    AppendComponentJson(builder, components[index], index);
                }

                builder.Append(']');
            }

            builder.Append('}');
        }

        private static void AppendTransformJson(StringBuilder builder, Transform transform)
        {
            builder.Append('{');
            builder.Append("\"localPosition\":");
            AppendVector3Json(builder, transform.localPosition);
            builder.Append(",\"localRotationEuler\":");
            AppendVector3Json(builder, transform.localEulerAngles);
            builder.Append(",\"localScale\":");
            AppendVector3Json(builder, transform.localScale);
            builder.Append(",\"worldPosition\":");
            AppendVector3Json(builder, transform.position);
            builder.Append(",\"worldRotationEuler\":");
            AppendVector3Json(builder, transform.eulerAngles);
            builder.Append(",\"lossyScale\":");
            AppendVector3Json(builder, transform.lossyScale);
            builder.Append('}');
        }

        private static void AppendVector3Json(StringBuilder builder, Vector3 value)
        {
            builder.Append('{');
            builder.Append("\"x\":");
            builder.Append(value.x.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"y\":");
            builder.Append(value.y.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"z\":");
            builder.Append(value.z.ToString(CultureInfo.InvariantCulture));
            builder.Append('}');
        }

        private static void AppendLayoutSummaryJson(StringBuilder builder, GameObject gameObject)
        {
            bool hasRenderer = gameObject.GetComponent<Renderer>() != null;
            bool hasCollider = gameObject.GetComponent<Collider>() != null;
            bool hasLight = gameObject.GetComponent<Light>() != null;
            bool hasCamera = gameObject.GetComponent<Camera>() != null;
            bool hasRigidbody = gameObject.GetComponent<Rigidbody>() != null;
            string primitiveHint = string.Empty;
            MeshFilter meshFilter = gameObject.GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
            {
                primitiveHint = meshFilter.sharedMesh.name ?? string.Empty;
            }

            builder.Append('{');
            builder.Append("\"componentCount\":");
            builder.Append(gameObject.GetComponents<Component>().Length);
            builder.Append(",\"hasRenderer\":");
            builder.Append(hasRenderer ? "true" : "false");
            builder.Append(",\"hasCollider\":");
            builder.Append(hasCollider ? "true" : "false");
            builder.Append(",\"hasLight\":");
            builder.Append(hasLight ? "true" : "false");
            builder.Append(",\"hasCamera\":");
            builder.Append(hasCamera ? "true" : "false");
            builder.Append(",\"hasRigidbody\":");
            builder.Append(hasRigidbody ? "true" : "false");
            builder.Append(",\"primitiveHint\":\"");
            builder.Append(MiniMcpJson.EscapeJson(primitiveHint));
            builder.Append("\"}");
        }

        private static void AppendComponentJson(StringBuilder builder, Component component, int index)
        {
            Type componentType = component != null ? component.GetType() : typeof(Component);
            builder.Append('{');
            builder.Append("\"componentId\":\"");
            builder.Append(MiniMcpJson.EscapeJson(GetObjectId(component) ?? string.Empty));
            builder.Append("\",\"type\":\"");
            builder.Append(MiniMcpJson.EscapeJson(componentType.FullName ?? componentType.Name));
            builder.Append("\",\"shortType\":\"");
            builder.Append(MiniMcpJson.EscapeJson(componentType.Name));
            builder.Append("\",\"componentIndex\":");
            builder.Append(index);
            builder.Append(",\"writableMembers\":[");

            List<string> writableMembers = GetWritableMemberNames(componentType);
            for (int memberIndex = 0; memberIndex < writableMembers.Count; memberIndex++)
            {
                if (memberIndex > 0)
                {
                    builder.Append(',');
                }

                builder.Append('\"');
                builder.Append(MiniMcpJson.EscapeJson(writableMembers[memberIndex]));
                builder.Append('\"');
            }

            builder.Append("]}");
        }

        private static string BuildObjectMutationResult(string status, string message, GameObject gameObject, Component component, string memberName = null, string rawValue = null)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"status\":\"");
            builder.Append(MiniMcpJson.EscapeJson(status));
            builder.Append("\",\"applied\":true,\"message\":\"");
            builder.Append(MiniMcpJson.EscapeJson(message));
            builder.Append("\",\"object\":");
            AppendGameObjectJson(builder, gameObject, 0, 0, true, "full");

            if (component != null)
            {
                builder.Append(",\"component\":");
                AppendComponentJson(builder, component, GetComponentIndex(gameObject, component));
            }

            if (!string.IsNullOrWhiteSpace(memberName))
            {
                builder.Append(",\"member\":{\"name\":\"");
                builder.Append(MiniMcpJson.EscapeJson(memberName));
                builder.Append("\",\"value\":\"");
                builder.Append(MiniMcpJson.EscapeJson(rawValue ?? string.Empty));
                builder.Append("\"}");
            }

            builder.Append('}');
            return builder.ToString();
        }

        public static string BuildValidationResult(string status, string message, string code)
        {
            return "{\"status\":\"" + MiniMcpJson.EscapeJson(status) + "\",\"applied\":false,\"code\":\"" + MiniMcpJson.EscapeJson(code) + "\",\"message\":\"" + MiniMcpJson.EscapeJson(message ?? string.Empty) + "\"}";
        }

        private static string GetObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            GlobalObjectId globalObjectId = GlobalObjectId.GetGlobalObjectIdSlow(obj);
            return globalObjectId.ToString();
        }

        private static string BuildHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            List<string> segments = new List<string>();
            Transform current = gameObject.transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }

            segments.Reverse();
            return string.Join("/", segments.ToArray());
        }

        private static Type ResolveComponentType(string componentTypeName, out string error)
        {
            error = string.Empty;
            string normalizedName = (componentTypeName ?? string.Empty).Trim();
            if (normalizedName.Length == 0)
            {
                error = "componentType is required.";
                return null;
            }

            Type exact = null;
            Type shortNameMatch = null;
            bool shortNameAmbiguous = false;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                Type[] types;
                try
                {
                    types = assemblies[assemblyIndex].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    Type type = types[typeIndex];
                    if (type == null || !typeof(Component).IsAssignableFrom(type) || type.IsAbstract || type.IsGenericTypeDefinition)
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        exact = type;
                        break;
                    }

                    if (string.Equals(type.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (shortNameMatch == null)
                        {
                            shortNameMatch = type;
                        }
                        else if (shortNameMatch != type)
                        {
                            shortNameAmbiguous = true;
                        }
                    }
                }

                if (exact != null)
                {
                    break;
                }
            }

            if (exact != null)
            {
                return exact;
            }

            if (shortNameAmbiguous)
            {
                error = "The requested component type name is ambiguous. Use the full type name including namespace.";
                return null;
            }

            if (shortNameMatch != null)
            {
                return shortNameMatch;
            }

            error = "No component type was found for '" + normalizedName + "'.";
            return null;
        }

        private static Component ResolveComponentInstance(GameObject target, Type componentType, int componentIndex, out string error)
        {
            error = string.Empty;
            if (target == null || componentType == null)
            {
                error = "Target object or component type is missing.";
                return null;
            }

            Component[] components = target.GetComponents(componentType);
            if (components == null || components.Length == 0)
            {
                error = "The target object does not contain the requested component type.";
                return null;
            }

            if (componentIndex < 0 || componentIndex >= components.Length)
            {
                error = "componentIndex is out of range for the requested component type on the target object.";
                return null;
            }

            return components[componentIndex];
        }

        private static int GetComponentIndex(GameObject gameObject, Component component)
        {
            if (gameObject == null || component == null)
            {
                return 0;
            }

            Component[] components = gameObject.GetComponents(component.GetType());
            for (int index = 0; index < components.Length; index++)
            {
                if (components[index] == component)
                {
                    return index;
                }
            }

            return 0;
        }

        private static MemberInfo ResolveWritableMember(Type type, string memberName, out string error)
        {
            error = string.Empty;
            string normalizedName = (memberName ?? string.Empty).Trim();
            if (type == null || normalizedName.Length == 0)
            {
                error = "propertyName is required.";
                return null;
            }

            PropertyInfo exactProperty = null;
            FieldInfo exactField = null;
            PropertyInfo caseInsensitiveProperty = null;
            FieldInfo caseInsensitiveField = null;

            foreach (PropertyInfo property in type.GetProperties(PublicInstanceFlags))
            {
                if (!property.CanWrite || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (string.Equals(property.Name, normalizedName, StringComparison.Ordinal))
                {
                    exactProperty = property;
                    break;
                }

                if (caseInsensitiveProperty == null && string.Equals(property.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveProperty = property;
                }
            }

            if (exactProperty != null)
            {
                return exactProperty;
            }

            foreach (FieldInfo field in type.GetFields(PublicInstanceFlags))
            {
                if (field.IsInitOnly || field.IsLiteral)
                {
                    continue;
                }

                if (string.Equals(field.Name, normalizedName, StringComparison.Ordinal))
                {
                    exactField = field;
                    break;
                }

                if (caseInsensitiveField == null && string.Equals(field.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitiveField = field;
                }
            }

            if (exactField != null)
            {
                return exactField;
            }

            if (caseInsensitiveProperty != null)
            {
                return caseInsensitiveProperty;
            }

            if (caseInsensitiveField != null)
            {
                return caseInsensitiveField;
            }

            error = "The requested writable member was not found on the component.";
            return null;
        }

        private static Type GetMemberType(MemberInfo member)
        {
            if (member is PropertyInfo property)
            {
                return property.PropertyType;
            }

            if (member is FieldInfo field)
            {
                return field.FieldType;
            }

            return typeof(string);
        }

        private static void SetMemberValue(Component component, MemberInfo member, object value)
        {
            if (member is PropertyInfo property)
            {
                property.SetValue(component, value, null);
                return;
            }

            if (member is FieldInfo field)
            {
                field.SetValue(component, value);
            }
        }

        private static List<string> GetWritableMemberNames(Type type)
        {
            List<string> names = new List<string>();
            if (type == null)
            {
                return names;
            }

            foreach (PropertyInfo property in type.GetProperties(PublicInstanceFlags))
            {
                if (!property.CanWrite || property.GetIndexParameters().Length > 0 || !IsSupportedValueType(property.PropertyType))
                {
                    continue;
                }

                if (!names.Contains(property.Name))
                {
                    names.Add(property.Name);
                }
            }

            foreach (FieldInfo field in type.GetFields(PublicInstanceFlags))
            {
                if (field.IsInitOnly || field.IsLiteral || !IsSupportedValueType(field.FieldType))
                {
                    continue;
                }

                if (!names.Contains(field.Name))
                {
                    names.Add(field.Name);
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static bool IsSupportedValueType(Type type)
        {
            Type normalized = Nullable.GetUnderlyingType(type) ?? type;
            return normalized == typeof(string)
                || normalized == typeof(bool)
                || normalized == typeof(int)
                || normalized == typeof(float)
                || normalized == typeof(double)
                || normalized == typeof(Vector2)
                || normalized == typeof(Vector3)
                || normalized == typeof(Vector4)
                || normalized == typeof(Color)
                || normalized.IsEnum;
        }

        private static bool TryConvertValue(string rawValue, Type targetType, out object convertedValue, out string error)
        {
            error = string.Empty;
            convertedValue = null;
            Type normalized = Nullable.GetUnderlyingType(targetType) ?? targetType;
            string value = rawValue ?? string.Empty;

            if (normalized == typeof(string))
            {
                convertedValue = value;
                return true;
            }

            if (normalized == typeof(bool))
            {
                if (bool.TryParse(value, out bool boolValue))
                {
                    convertedValue = boolValue;
                    return true;
                }

                error = "Expected a boolean value like true or false.";
                return false;
            }

            if (normalized == typeof(int))
            {
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                {
                    convertedValue = intValue;
                    return true;
                }

                error = "Expected an integer value.";
                return false;
            }

            if (normalized == typeof(float))
            {
                if (float.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float floatValue))
                {
                    convertedValue = floatValue;
                    return true;
                }

                error = "Expected a float value using invariant culture formatting.";
                return false;
            }

            if (normalized == typeof(double))
            {
                if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double doubleValue))
                {
                    convertedValue = doubleValue;
                    return true;
                }

                error = "Expected a number using invariant culture formatting.";
                return false;
            }

            if (normalized == typeof(Vector2))
            {
                if (TryParseFloatList(value, 2, out float[] values, out error))
                {
                    convertedValue = new Vector2(values[0], values[1]);
                    return true;
                }

                return false;
            }

            if (normalized == typeof(Vector3))
            {
                if (TryParseFloatList(value, 3, out float[] values, out error))
                {
                    convertedValue = new Vector3(values[0], values[1], values[2]);
                    return true;
                }

                return false;
            }

            if (normalized == typeof(Vector4))
            {
                if (TryParseFloatList(value, 4, out float[] values, out error))
                {
                    convertedValue = new Vector4(values[0], values[1], values[2], values[3]);
                    return true;
                }

                return false;
            }

            if (normalized == typeof(Color))
            {
                if (ColorUtility.TryParseHtmlString(value, out Color color))
                {
                    convertedValue = color;
                    return true;
                }

                if (TryParseFloatList(value, 3, out float[] rgbValues, out error) || TryParseFloatList(value, 4, out rgbValues, out error))
                {
                    convertedValue = rgbValues.Length == 3
                        ? new Color(rgbValues[0], rgbValues[1], rgbValues[2], 1f)
                        : new Color(rgbValues[0], rgbValues[1], rgbValues[2], rgbValues[3]);
                    return true;
                }

                error = "Expected a color as #RRGGBB, #RRGGBBAA, or comma-separated float values.";
                return false;
            }

            if (normalized.IsEnum)
            {
                try
                {
                    convertedValue = Enum.Parse(normalized, value, true);
                    return true;
                }
                catch
                {
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int enumIndex))
                    {
                        convertedValue = Enum.ToObject(normalized, enumIndex);
                        return true;
                    }
                }

                error = "Expected an enum name or numeric enum value.";
                return false;
            }

            error = "The requested member type is not supported for MCP value conversion.";
            return false;
        }

        private static bool TryParseFloatList(string rawValue, int expectedCount, out float[] values, out string error)
        {
            values = null;
            error = string.Empty;
            string[] parts = (rawValue ?? string.Empty).Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != expectedCount)
            {
                error = "Expected " + expectedCount.ToString(CultureInfo.InvariantCulture) + " comma-separated numeric values.";
                return false;
            }

            values = new float[expectedCount];
            for (int index = 0; index < parts.Length; index++)
            {
                if (!float.TryParse(parts[index].Trim(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out values[index]))
                {
                    error = "Expected " + expectedCount.ToString(CultureInfo.InvariantCulture) + " comma-separated numeric values.";
                    values = null;
                    return false;
                }
            }

            return true;
        }

        private static bool TryParsePrimitiveType(string rawType, out PrimitiveType primitiveType)
        {
            primitiveType = PrimitiveType.Cube;
            string normalized = (rawType ?? string.Empty).Trim();
            if (normalized.Length == 0)
            {
                return false;
            }

            return Enum.TryParse(normalized, true, out primitiveType);
        }

        private static void PersistObjectChange(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            EditorUtility.SetDirty(gameObject);
            if (gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }
    }
}