using System.Text;
using MiniMCP;
using MiniMCP.Editor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "current_scene_read",
        "Returns details about the currently active Unity scene, including path, dirty state, and current play/edit mode context.",
        Group = "Scene")]
    public sealed class CurrentSceneTool : MiniMcpMainThreadToolBase
    {
        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            var snapshot = MiniMcpEditorState.GetStatusSnapshot();
            var builder = new StringBuilder();
            builder.Append("{");
            builder.Append("\"scene\":{");
            builder.Append("\"name\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.ActiveSceneName ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"path\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.ActiveScenePath ?? string.Empty));
            builder.Append("\",");
            builder.Append("\"buildIndex\":");
            builder.Append(snapshot.ActiveSceneBuildIndex);
            builder.Append(",\"isDirty\":");
            builder.Append(snapshot.IsSceneDirty ? "true" : "false");
            builder.Append(",\"isLoaded\":");
            builder.Append(snapshot.IsSceneLoaded ? "true" : "false");
            builder.Append("},");
            builder.Append("\"editor\":{");
            builder.Append("\"isPlaying\":");
            builder.Append(snapshot.IsPlaying ? "true" : "false");
            builder.Append(",\"isPlayingOrWillChangePlaymode\":");
            builder.Append(snapshot.IsPlayingOrWillChangePlaymode ? "true" : "false");
            builder.Append(",\"playModeState\":\"");
            builder.Append(MiniMcpJson.EscapeJson(snapshot.PlayModeState ?? string.Empty));
            builder.Append("\"");
            builder.Append("}");
            builder.Append("}");
            return MiniMcpToolCallResult.Ok(builder.ToString());
        }
    }
}