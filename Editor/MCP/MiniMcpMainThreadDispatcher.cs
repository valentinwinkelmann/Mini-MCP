using System;

namespace MiniMCP
{
    internal static class MiniMcpMainThreadDispatcher
    {
        public static bool Invoke(Action action, TimeSpan timeout, out string error)
        {
            return MiniMcpEditorThread.Invoke(action, timeout, out error);
        }
    }
}
