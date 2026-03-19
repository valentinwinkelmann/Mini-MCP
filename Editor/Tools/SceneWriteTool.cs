using MiniMCP;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "scene_write",
        "Performs safe scene mutations such as creating objects, creating primitives, adding components, renaming objects, and setting supported component members.",
        Group = "Scene")]
    public sealed class SceneWriteTool : MiniMcpTypedMainThreadTool<SceneWriteTool.Arguments>
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Requested action.", Required = true, EnumValues = new[] { "create_game_object", "create_primitive", "add_component", "set_component_member", "set_transform", "rename_object", "batch_mutate" })]
            public string action;

            [MiniMcpSchemaProperty(Description = "Optional target scene object id for actions that operate on an existing object.")]
            public string targetObjectId;

            [MiniMcpSchemaProperty(Description = "Optional target hierarchy path such as Parent/Child when targetObjectId is not provided.")]
            public string targetPath;

            [MiniMcpSchemaProperty(Description = "Optional parent scene object id for creation actions.")]
            public string parentObjectId;

            [MiniMcpSchemaProperty(Description = "Optional parent hierarchy path such as Parent/Child when parentObjectId is not provided.")]
            public string parentPath;

            [MiniMcpSchemaProperty(Description = "Object name for create or rename actions.")]
            public string name;

            [MiniMcpSchemaProperty(Description = "Primitive type for create_primitive. Valid values are Sphere, Capsule, Cylinder, Cube, Plane, and Quad.")]
            public string primitiveType;

            [MiniMcpSchemaProperty(Description = "Component type name to add or modify. Short names work when unique; otherwise use the full type name.")]
            public string componentType;

            [MiniMcpSchemaProperty(Description = "Zero-based component index when multiple components of the same type exist on the object.", Minimum = 0, Maximum = 32)]
            public int componentIndex;

            [MiniMcpSchemaProperty(Name = "propertyName", Description = "Writable public field or property name for set_component_member.")]
            public string memberName;

            [MiniMcpSchemaProperty(Description = "Raw string value for set_component_member. Numbers should use invariant culture and vectors/colors should use comma-separated values.")]
            public string propertyValue;

            [MiniMcpSchemaProperty(Description = "Optional transform position as x,y,z. Used by create actions and set_transform.")]
            public string position;

            [MiniMcpSchemaProperty(Description = "Optional transform rotation Euler angles as x,y,z. Used by create actions and set_transform.")]
            public string rotation;

            [MiniMcpSchemaProperty(Description = "Optional transform scale as x,y,z. Used by create actions and set_transform.")]
            public string scale;

            [MiniMcpSchemaProperty(Description = "Whether transform position and rotation should be interpreted in world space. Scale remains local.")]
            public bool useWorldSpace;

            [MiniMcpSchemaProperty(Description = "When true, validates and previews the requested mutation without applying it.")]
            public bool preview;

            [MiniMcpSchemaProperty(Description = "For batch_mutate, an array of operation objects using the same fields as scene_write arguments except preview.")]
            public string operations;
        }

        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            string action = string.Empty;
            string targetObjectId = string.Empty;
            string targetPath = string.Empty;
            string parentObjectId = string.Empty;
            string parentPath = string.Empty;
            string name = string.Empty;
            string primitiveType = string.Empty;
            string componentType = string.Empty;
            string memberName = string.Empty;
            string propertyValue = string.Empty;
            string position = string.Empty;
            string rotation = string.Empty;
            string scale = string.Empty;
            string operationsJson = "[]";
            int componentIndex = 0;
            bool useWorldSpace = false;
            bool preview = false;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "action", out action);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "targetObjectId", out targetObjectId);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "targetPath", out targetPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "parentObjectId", out parentObjectId);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "parentPath", out parentPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "name", out name);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "primitiveType", out primitiveType);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "componentType", out componentType);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "propertyName", out memberName);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "propertyValue", out propertyValue);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "position", out position);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "rotation", out rotation);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "scale", out scale);
            MiniMcpJson.TryExtractIntProperty(argumentsJson, "componentIndex", out componentIndex);
            MiniMcpJson.TryExtractBoolProperty(argumentsJson, "useWorldSpace", out useWorldSpace);
            MiniMcpJson.TryExtractBoolProperty(argumentsJson, "preview", out preview);
            MiniMcpJson.TryExtractArrayProperty(argumentsJson, "operations", out operationsJson);

            string normalizedAction = (action ?? string.Empty).Trim().ToLowerInvariant();
            switch (normalizedAction)
            {
                case "create_game_object":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.CreateGameObject(name, parentObjectId, parentPath, position, rotation, scale, useWorldSpace, preview));
                case "create_primitive":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.CreatePrimitive(primitiveType, name, parentObjectId, parentPath, position, rotation, scale, useWorldSpace, preview));
                case "add_component":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.AddComponent(targetObjectId, targetPath, componentType, preview));
                case "set_component_member":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.SetComponentMember(targetObjectId, targetPath, componentType, componentIndex, memberName, propertyValue, preview));
                case "set_transform":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.SetTransform(targetObjectId, targetPath, position, rotation, scale, useWorldSpace, preview));
                case "rename_object":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.RenameObject(targetObjectId, targetPath, name, preview));
                case "batch_mutate":
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.ExecuteBatchOperations(operationsJson, preview));
                default:
                    return MiniMcpToolCallResult.Ok(MiniMcpSceneToolSupport.BuildValidationResult("invalid_action", "Invalid action. Use create_game_object, create_primitive, add_component, set_component_member, set_transform, rename_object, or batch_mutate.", "invalid_action"));
            }
        }
    }
}