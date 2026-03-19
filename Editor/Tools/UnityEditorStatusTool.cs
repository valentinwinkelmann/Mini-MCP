using MiniMCP;
using MiniMCP.Editor;

namespace MiniMCP.Tools
{
    [MiniMcpTool(
        "unity_editor_status",
        "Returns the current Unity editor state including compile status, play/edit mode, and active scene information.",
        Group = "Editor")]
    public sealed class UnityEditorStatusTool : MiniMcpMainThreadToolBase
    {
        protected override MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson)
        {
            return MiniMcpToolCallResult.Ok(MiniMcpEditorState.BuildStatusJson());
        }
    }
}