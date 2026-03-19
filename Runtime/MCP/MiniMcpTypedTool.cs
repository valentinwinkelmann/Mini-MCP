using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace MiniMCP
{
    public abstract class MiniMcpTypedTool<TArguments> : MiniMcpToolBase, IMiniMcpToolSchemaProvider
    {
        public virtual string GetInputSchemaJson()
        {
            return BuildObjectSchema(typeof(TArguments));
        }

        protected virtual string BuildObjectSchema(Type type)
        {
            var members = GetSchemaMembers(type);
            var builder = new StringBuilder();
            builder.Append("{\"type\":\"object\",\"properties\":{");

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(MiniMcpJson.EscapeJson(member.Name));
                builder.Append("\":");
                builder.Append(BuildSchemaForMember(member));
            }

            builder.Append("},\"additionalProperties\":false");

            var requiredNames = new List<string>();
            for (var i = 0; i < members.Count; i++)
            {
                if (members[i].Attribute != null && members[i].Attribute.Required)
                {
                    requiredNames.Add(members[i].Name);
                }
            }

            if (requiredNames.Count > 0)
            {
                builder.Append(",\"required\":[");
                for (var i = 0; i < requiredNames.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append('"');
                    builder.Append(MiniMcpJson.EscapeJson(requiredNames[i]));
                    builder.Append('"');
                }

                builder.Append(']');
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static List<SchemaMember> GetSchemaMembers(Type type)
        {
            var members = new List<SchemaMember>();
            var flags = BindingFlags.Instance | BindingFlags.Public;

            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead)
                {
                    continue;
                }

                members.Add(new SchemaMember
                {
                    Name = ResolveName(property.Name, property.GetCustomAttribute<MiniMcpSchemaPropertyAttribute>()),
                    ValueType = property.PropertyType,
                    Attribute = property.GetCustomAttribute<MiniMcpSchemaPropertyAttribute>()
                });
            }

            foreach (var field in type.GetFields(flags))
            {
                members.Add(new SchemaMember
                {
                    Name = ResolveName(field.Name, field.GetCustomAttribute<MiniMcpSchemaPropertyAttribute>()),
                    ValueType = field.FieldType,
                    Attribute = field.GetCustomAttribute<MiniMcpSchemaPropertyAttribute>()
                });
            }

            return members;
        }

        private static string ResolveName(string fallbackName, MiniMcpSchemaPropertyAttribute attribute)
        {
            if (attribute == null || string.IsNullOrWhiteSpace(attribute.Name))
            {
                return fallbackName;
            }

            return attribute.Name;
        }

        private static string BuildSchemaForMember(SchemaMember member)
        {
            var builder = new StringBuilder();
            builder.Append('{');

            var normalizedType = Nullable.GetUnderlyingType(member.ValueType) ?? member.ValueType;
            builder.Append("\"type\":\"");
            builder.Append(GetJsonTypeName(normalizedType));
            builder.Append('"');

            if (member.Attribute != null && !string.IsNullOrWhiteSpace(member.Attribute.Description))
            {
                builder.Append(",\"description\":\"");
                builder.Append(MiniMcpJson.EscapeJson(member.Attribute.Description));
                builder.Append('"');
            }

            if (normalizedType.IsEnum)
            {
                AppendEnumValues(builder, Enum.GetNames(normalizedType));
            }
            else if (member.Attribute != null && member.Attribute.EnumValues != null && member.Attribute.EnumValues.Length > 0)
            {
                AppendEnumValues(builder, member.Attribute.EnumValues);
            }

            if (member.Attribute != null)
            {
                if (!double.IsNaN(member.Attribute.Minimum))
                {
                    builder.Append(",\"minimum\":");
                    builder.Append(member.Attribute.Minimum.ToString(CultureInfo.InvariantCulture));
                }

                if (!double.IsNaN(member.Attribute.Maximum))
                {
                    builder.Append(",\"maximum\":");
                    builder.Append(member.Attribute.Maximum.ToString(CultureInfo.InvariantCulture));
                }
            }

            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendEnumValues(StringBuilder builder, IReadOnlyList<string> values)
        {
            builder.Append(",\"enum\":[");
            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append('"');
                builder.Append(MiniMcpJson.EscapeJson(values[i] ?? string.Empty));
                builder.Append('"');
            }

            builder.Append(']');
        }

        private static string GetJsonTypeName(Type type)
        {
            if (type == typeof(bool))
            {
                return "boolean";
            }

            if (type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong))
            {
                return "integer";
            }

            if (type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal))
            {
                return "number";
            }

            return "string";
        }

        private sealed class SchemaMember
        {
            public string Name;
            public Type ValueType;
            public MiniMcpSchemaPropertyAttribute Attribute;
        }
    }
}