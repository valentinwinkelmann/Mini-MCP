namespace MiniMCP
{
    public sealed class MiniMcpToolDescriptor
    {
        public string Name;
        public string Description;
        public string InputSchemaJson;
        public string Group;
        public string TypeName;
        public bool IsEnabled;
        public bool SupportsAwait;
        public string AwaitKind;
        public int DefaultAwaitTimeoutMs;
        public int MaxAwaitTimeoutMs;
    }
}
