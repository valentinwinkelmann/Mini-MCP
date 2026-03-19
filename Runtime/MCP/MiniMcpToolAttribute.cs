using System;

namespace MiniMCP
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class MiniMcpToolAttribute : Attribute
    {
        public const string DefaultInputSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        public const int DefaultAwaitTimeoutMsValue = 60000;
        public const int DefaultMaxAwaitTimeoutMs = 600000;

        public MiniMcpToolAttribute(string name, string description)
            : this(name, description, DefaultInputSchemaJson)
        {
        }

        public MiniMcpToolAttribute(string name, string description, string inputSchemaJson)
        {
            this.Name = name ?? string.Empty;
            this.Description = description ?? string.Empty;
            this.InputSchemaJson = string.IsNullOrWhiteSpace(inputSchemaJson)
                ? DefaultInputSchemaJson
                : inputSchemaJson;
            this.Group = string.Empty;
            this.SupportsAwait = false;
            this.AwaitKind = string.Empty;
            this.DefaultAwaitTimeoutMs = DefaultAwaitTimeoutMsValue;
            this.MaxAwaitTimeoutMs = DefaultMaxAwaitTimeoutMs;
        }

        public string Name { get; }
        public string Description { get; }
        public string InputSchemaJson { get; }
        public string Group { get; set; }
        public bool SupportsAwait { get; set; }
        public string AwaitKind { get; set; }
        public int DefaultAwaitTimeoutMs { get; set; }
        public int MaxAwaitTimeoutMs { get; set; }
    }
}
