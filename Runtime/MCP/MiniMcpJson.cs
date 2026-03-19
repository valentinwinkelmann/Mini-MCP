using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MiniMCP
{
    public static class MiniMcpJson
    {
        public static bool TryExtractStringProperty(string json, string propertyName, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            var pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\\\"])*)\\\"";
            var match = Regex.Match(json, pattern, RegexOptions.Singleline);
            if (!match.Success)
            {
                return false;
            }

            value = Regex.Unescape(match.Groups[1].Value);
            return true;
        }

        public static bool TryExtractArgumentsObject(string json, out string argumentsJson)
        {
            argumentsJson = "{}";
            if (string.IsNullOrEmpty(json))
            {
                return false;
            }

            var keyIndex = json.IndexOf("\"arguments\"", StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return true;
            }

            var colonIndex = json.IndexOf(':', keyIndex);
            if (colonIndex < 0)
            {
                return false;
            }

            var i = colonIndex + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length)
            {
                return false;
            }

            if (json[i] != '{')
            {
                return false;
            }

            if (!TryExtractBalancedObject(json, i, out argumentsJson))
            {
                return false;
            }

            return true;
        }

        public static bool TryExtractIntProperty(string json, string propertyName, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            var pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*(-?[0-9]+)";
            var match = Regex.Match(json, pattern, RegexOptions.Singleline);
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        public static bool TryExtractArrayProperty(string json, string propertyName, out string arrayJson)
        {
            arrayJson = "[]";
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            var keyIndex = json.IndexOf("\"" + propertyName + "\"", StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                return false;
            }

            var colonIndex = json.IndexOf(':', keyIndex);
            if (colonIndex < 0)
            {
                return false;
            }

            var i = colonIndex + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i]))
            {
                i++;
            }

            if (i >= json.Length || json[i] != '[')
            {
                return false;
            }

            return TryExtractBalancedArray(json, i, out arrayJson);
        }

        public static bool TryExtractBoolProperty(string json, string propertyName, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            {
                return false;
            }

            var pattern = "\\\"" + Regex.Escape(propertyName) + "\\\"\\s*:\\s*(true|false)";
            var match = Regex.Match(json, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            return bool.TryParse(match.Groups[1].Value, out value);
        }

        public static bool TrySplitTopLevelArrayElements(string arrayJson, out List<string> elements)
        {
            elements = new List<string>();
            if (string.IsNullOrWhiteSpace(arrayJson))
            {
                return false;
            }

            var trimmed = arrayJson.Trim();
            if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[trimmed.Length - 1] != ']')
            {
                return false;
            }

            var startIndex = 1;
            var endIndex = trimmed.Length - 1;
            var tokenStart = -1;
            var objectDepth = 0;
            var arrayDepth = 0;
            var inString = false;
            var escaping = false;

            for (var index = startIndex; index < endIndex; index++)
            {
                var ch = trimmed[index];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (ch == '\\')
                    {
                        escaping = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    if (tokenStart < 0)
                    {
                        tokenStart = index;
                    }

                    continue;
                }

                if (char.IsWhiteSpace(ch) && tokenStart < 0)
                {
                    continue;
                }

                if (ch == ',' && objectDepth == 0 && arrayDepth == 0)
                {
                    if (tokenStart >= 0)
                    {
                        var element = trimmed.Substring(tokenStart, index - tokenStart).Trim();
                        if (element.Length > 0)
                        {
                            elements.Add(element);
                        }

                        tokenStart = -1;
                    }

                    continue;
                }

                if (tokenStart < 0)
                {
                    tokenStart = index;
                }

                if (ch == '{')
                {
                    objectDepth++;
                }
                else if (ch == '}')
                {
                    objectDepth--;
                }
                else if (ch == '[')
                {
                    arrayDepth++;
                }
                else if (ch == ']')
                {
                    arrayDepth--;
                }

                if (objectDepth < 0 || arrayDepth < 0)
                {
                    return false;
                }
            }

            if (inString || objectDepth != 0 || arrayDepth != 0)
            {
                return false;
            }

            if (tokenStart >= 0)
            {
                var element = trimmed.Substring(tokenStart, endIndex - tokenStart).Trim();
                if (element.Length > 0)
                {
                    elements.Add(element);
                }
            }

            return true;
        }

        private static bool TryExtractBalancedObject(string json, int startIndex, out string objectJson)
        {
            objectJson = string.Empty;
            var depth = 0;
            var inString = false;
            var escaping = false;

            for (var i = startIndex; i < json.Length; i++)
            {
                var ch = json[i];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (ch == '\\')
                    {
                        escaping = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        objectJson = json.Substring(startIndex, i - startIndex + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryExtractBalancedArray(string json, int startIndex, out string arrayJson)
        {
            arrayJson = string.Empty;
            var depth = 0;
            var inString = false;
            var escaping = false;

            for (var i = startIndex; i < json.Length; i++)
            {
                var ch = json[i];

                if (inString)
                {
                    if (escaping)
                    {
                        escaping = false;
                    }
                    else if (ch == '\\')
                    {
                        escaping = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '[')
                {
                    depth++;
                }
                else if (ch == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        arrayJson = json.Substring(startIndex, i - startIndex + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        public static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }
    }
}
