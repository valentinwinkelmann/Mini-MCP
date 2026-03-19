using System;

namespace MiniMCP
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true, AllowMultiple = false)]
    public sealed class MiniMcpSchemaPropertyAttribute : Attribute
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public double Minimum { get; set; } = double.NaN;
        public double Maximum { get; set; } = double.NaN;
        public string[] EnumValues { get; set; }
    }
}