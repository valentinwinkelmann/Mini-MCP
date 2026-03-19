namespace MiniMCP
{
    public sealed class MiniMcpToolCallResult
    {
        private MiniMcpToolCallResult(string text, bool isError)
        {
            this.Text = text ?? string.Empty;
            this.IsError = isError;
        }

        public string Text { get; }
        public bool IsError { get; }

        public static MiniMcpToolCallResult Ok(string text)
        {
            return new MiniMcpToolCallResult(text, false);
        }

        public static MiniMcpToolCallResult Error(string text)
        {
            return new MiniMcpToolCallResult(text, true);
        }
    }
}
