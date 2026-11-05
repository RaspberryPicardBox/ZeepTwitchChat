using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZeepTwitchChat.Util
{
    /// <summary>
    /// Tiny JSON reader for the emote provider APIs (BTTV, 7TV, FFZ).
    ///
    /// The mod has no JSON dependency on purpose: BepInEx 5 ships no Newtonsoft, and
    /// pulling one in would mean shipping another DLL with the mod. This reader covers
    /// everything JSON defines (objects, arrays, strings with escapes, numbers, literals)
    /// and returns plain .NET collections, so it can be unit tested without the game.
    /// </summary>
    public static class MiniJson
    {
        private const int MaxDepth = 64;

        /// <summary>Parses a JSON document into Dictionary/List/string/double/bool/null.</summary>
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                throw new FormatException("Empty JSON document.");
            }

            int index = 0;
            object value = ParseValue(json, ref index, 0);
            SkipWhitespace(json, ref index);
            return value;
        }

        /// <summary>Parses, returning false instead of throwing. Used on network responses.</summary>
        public static bool TryParse(string json, out object value)
        {
            try
            {
                value = Parse(json);
                return true;
            }
            catch (Exception)
            {
                value = null;
                return false;
            }
        }

        private static object ParseValue(string json, ref int index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("JSON nesting too deep.");
            }

            SkipWhitespace(json, ref index);
            if (index >= json.Length)
            {
                throw new FormatException("Unexpected end of JSON.");
            }

            char c = json[index];
            switch (c)
            {
                case '{':
                    return ParseObject(json, ref index, depth);
                case '[':
                    return ParseArray(json, ref index, depth);
                case '"':
                    return ParseString(json, ref index);
                case 't':
                    Expect(json, ref index, "true");
                    return true;
                case 'f':
                    Expect(json, ref index, "false");
                    return false;
                case 'n':
                    Expect(json, ref index, "null");
                    return null;
                default:
                    return ParseNumber(json, ref index);
            }
        }

        private static Dictionary<string, object> ParseObject(string json, ref int index, int depth)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            index++; // '{'
            SkipWhitespace(json, ref index);

            if (index < json.Length && json[index] == '}')
            {
                index++;
                return result;
            }

            while (true)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"')
                {
                    throw new FormatException("Expected a JSON property name at " + index + ".");
                }

                string key = ParseString(json, ref index);

                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    throw new FormatException("Expected ':' at " + index + ".");
                }

                index++;
                result[key] = ParseValue(json, ref index, depth + 1);

                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException("Unterminated JSON object.");
                }

                char next = json[index++];
                if (next == '}')
                {
                    return result;
                }

                if (next != ',')
                {
                    throw new FormatException("Expected ',' or '}' at " + (index - 1) + ".");
                }
            }
        }

        private static List<object> ParseArray(string json, ref int index, int depth)
        {
            List<object> result = new List<object>();
            index++; // '['
            SkipWhitespace(json, ref index);

            if (index < json.Length && json[index] == ']')
            {
                index++;
                return result;
            }

            while (true)
            {
                result.Add(ParseValue(json, ref index, depth + 1));

                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException("Unterminated JSON array.");
                }

                char next = json[index++];
                if (next == ']')
                {
                    return result;
                }

                if (next != ',')
                {
                    throw new FormatException("Expected ',' or ']' at " + (index - 1) + ".");
                }
            }
        }

        private static string ParseString(string json, ref int index)
        {
            index++; // opening quote
            StringBuilder builder = new StringBuilder();

            while (true)
            {
                if (index >= json.Length)
                {
                    throw new FormatException("Unterminated JSON string.");
                }

                char c = json[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= json.Length)
                {
                    throw new FormatException("Unterminated JSON escape.");
                }

                char escape = json[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length)
                        {
                            throw new FormatException("Truncated \\u escape.");
                        }

                        builder.Append((char)ushort.Parse(
                            json.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }
        }

        private static double ParseNumber(string json, ref int index)
        {
            int start = index;
            while (index < json.Length)
            {
                char c = json[index];
                bool partOfNumber = (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
                if (!partOfNumber)
                {
                    break;
                }

                index++;
            }

            if (start == index)
            {
                throw new FormatException("Unexpected character '" + json[start] + "' at " + start + ".");
            }

            return double.Parse(json.Substring(start, index - start), CultureInfo.InvariantCulture);
        }

        private static void Expect(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length ||
                string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("Expected '" + literal + "' at " + index + ".");
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                char c = json[index];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    return;
                }

                index++;
            }
        }
    }
}
