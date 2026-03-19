using UnityEditor;
using UnityEditor.Callbacks;

namespace MiniMCP.Editor
{
    internal static class MiniMcpDidReloadScriptsHook
    {
        [DidReloadScripts]
        private static void OnDidReloadScripts()
        {
            MiniMcpRelayService.NotifyRelayStatus("ready", "did_reload_scripts");
            MiniMcpAwaitedOperationStore.TryCompleteRunningOperation(
                "recompile",
                "completed",
                true,
                "Unity completed script reload after recompilation.");

            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling)
                {
                    return;
                }

                MiniMcpRelayService.NotifyRelayStatus("ready", "did_reload_scripts_delay");
                MiniMcpEditorService.EnsureRunning("did_reload_scripts");
            };
        }
    }
}
