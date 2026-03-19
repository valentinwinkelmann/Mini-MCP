using MiniMCP;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "scene_hierarchy_read",
        "Returns a structured view of the active scene hierarchy with optional root scoping, depth limiting, and component summaries.",
        Group = "Scene")]
    public sealed class SceneHierarchyReadTool : MiniMcpTypedMainThreadTool<SceneHierarchyReadTool.Arguments>
    {
        public sealed class Arguments
        {
            [MiniMcpSchemaProperty(Description = "Optional root scene object id. When provided, the hierarchy read starts from that object.")]
            public string rootObjectId;

            [MiniMcpSchemaProperty(Description = "Optional hierarchy path such as Parent/Child. Used when rootObjectId is not provided.")]
            public string rootPath;

            [MiniMcpSchemaProperty(Description = "How many levels below the requested root should be included.", Minimum = 0, Maximum = 8)]
            public int maxDepth;

            [MiniMcpSchemaProperty(Description = "Whether to include component summaries and writable member names on each object.")]
            public bool includeComponents;

            [MiniMcpSchemaProperty(Description = "Whether inactive objects should be included in the hierarchy result.")]
            public bool includeInactive;

            [MiniMcpSchemaProperty(Description = "Read mode. Use 'full' for component-centric hierarchy data or 'layout' for compact spatial reasoning output.", EnumValues = new[] { "full", "layout" })]
            public string viewMode;
        }

        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            string rootObjectId = string.Empty;
            string rootPath = string.Empty;
            string viewMode = string.Empty;
            int maxDepth = 2;
            bool includeComponents = true;
            bool includeInactive = true;
            bool includeComponentsSpecified = false;

            MiniMcpJson.TryExtractStringProperty(argumentsJson, "rootObjectId", out rootObjectId);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "rootPath", out rootPath);
            MiniMcpJson.TryExtractStringProperty(argumentsJson, "viewMode", out viewMode);
            if (MiniMcpJson.TryExtractIntProperty(argumentsJson, "maxDepth", out int parsedDepth))
            {
                maxDepth = parsedDepth;
            }

            if (MiniMcpJson.TryExtractBoolProperty(argumentsJson, "includeComponents", out bool parsedIncludeComponents))
            {
                includeComponents = parsedIncludeComponents;
                includeComponentsSpecified = true;
            }

            if (MiniMcpJson.TryExtractBoolProperty(argumentsJson, "includeInactive", out bool parsedIncludeInactive))
            {
                includeInactive = parsedIncludeInactive;
            }

            if (maxDepth < 0)
            {
                maxDepth = 0;
            }
            else if (maxDepth > 8)
            {
                maxDepth = 8;
            }

            string normalizedViewMode = string.IsNullOrWhiteSpace(viewMode) ? "full" : viewMode.Trim().ToLowerInvariant();
            if (normalizedViewMode != "full" && normalizedViewMode != "layout")
            {
                normalizedViewMode = "full";
            }

            if (normalizedViewMode == "layout" && !includeComponentsSpecified)
            {
                includeComponents = false;
            }

            string result = MiniMcpSceneToolSupport.BuildHierarchyResult(rootObjectId, rootPath, maxDepth, includeComponents, includeInactive, normalizedViewMode);
            return MiniMcpToolCallResult.Ok(result);
        }
    }
}