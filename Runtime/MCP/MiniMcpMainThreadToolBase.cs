using System;

namespace MiniMCP
{
    public abstract class MiniMcpMainThreadToolBase : MiniMcpToolBase, IMiniMcpToolThreadingValidated
    {
        protected virtual TimeSpan MainThreadTimeout => TimeSpan.FromSeconds(10);

        public sealed override MiniMcpToolCallResult Execute(string argumentsJson)
        {
            MiniMcpToolCallResult result = null;
            string dispatchError;
            bool dispatched = MiniMcpEditorThread.Invoke(() =>
            {
                result = ExecuteOnMainThread(argumentsJson ?? "{}");
            }, this.MainThreadTimeout, out dispatchError);

            if (!dispatched)
            {
                return MiniMcpToolCallResult.Error("{\"status\":\"error\",\"message\":\"" + MiniMcpJson.EscapeJson(dispatchError) + "\"}");
            }

            return result ?? MiniMcpToolCallResult.Ok(string.Empty);
        }

        protected abstract MiniMcpToolCallResult ExecuteOnMainThread(string argumentsJson);
    }
}