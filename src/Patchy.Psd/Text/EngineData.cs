using System.Globalization;
using System.Text;

namespace Patchy.Psd.Text;

/// <summary>Kinds of value in a Photoshop text-engine (<c>EngineData</c>) tree.</summary>
public enum EngineValueKind
{
    Dictionary,
    List,
    Number,
    Boolean,
    String,
    Name,
}

/// <summary>
/// One node of the PostScript-like <c>EngineData</c> structure stored in type
/// layers (<c>TySh</c>) and the global <c>Txt2</c> block. Strings are decoded
/// from their UTF-16 form.
/// </summary>
public sealed class EngineValue
{
    private static readonly IReadOnlyList<KeyValuePair<string, EngineValue>> EmptyEntries = [];
    private static readonly IReadOnlyList<EngineValue> EmptyItems = [];

    public EngineValueKind Kind { get; init; }

    public double Number { get; init; }

    public bool Boolean { get; init; }

    /// <summary>Decoded text for <see cref="EngineValueKind.String"/>, or the name for <see cref="EngineValueKind.Name"/>.</summary>
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<KeyValuePair<string, EngineValue>> Entries { get; init; } = EmptyEntries;

    public IReadOnlyList<EngineValue> Items { get; init; } = EmptyItems;

    public EngineValue? this[string key]
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.Key == key)
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }

    public EngineValue? this[int index] => index >= 0 && index < Items.Count ? Items[index] : null;

    /// <summary>Follows a path of dictionary keys and list indices, e.g. <c>Get("EngineDict", "Editor", "Text")</c>.</summary>
    public EngineValue? Get(params object[] path)
    {
        EngineValue? current = this;
        foreach (var step in path)
        {
            current = step switch
            {
                string key => current?[key],
                int index => current?[index],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public override string ToString() => Kind switch
    {
        EngineValueKind.Number => Number.ToString(CultureInfo.InvariantCulture),
        EngineValueKind.Boolean => Boolean ? "true" : "false",
        EngineValueKind.String => $"({Text})",
        EngineValueKind.Name => "/" + Text,
        EngineValueKind.List => $"[{Items.Count}]",
        _ => $"<<{Entries.Count}>>",
    };
}

/// <summary>Parser for the text-engine data format.</summary>
public static class EngineDataParser
{
    private const int MaxDepth = 256;

    /// <summary>Parses EngineData bytes. Returns null when the data is malformed.</summary>
    public static EngineValue? Parse(ReadOnlySpan<byte> data)
    {
        var parser = new Parser(data);
        try
        {
            return parser.ParseRoot();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Decodes an EngineData string body (between the parentheses), unescaping and decoding UTF-16.</summary>
    public static string DecodeString(ReadOnlySpan<byte> body)
    {
        var unescaped = new byte[body.Length];
        var length = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var b = body[i];
            if (b == (byte)'\\' && i + 1 < body.Length)
            {
                i++;
                b = body[i];
            }

            unescaped[length++] = b;
        }

        var bytes = unescaped.AsSpan(0, length);
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(bytes[2..]);
        }
        else
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return text;
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Parser(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public EngineValue ParseRoot()
        {
            SkipWhitespace();
            if (Peek2("<<"))
            {
                var root = ParseValue(0);
                return root;
            }

            // Txt2-style data can be a bare sequence of /key value pairs.
            return ParseDictionaryEntries(0, bare: true);
        }

        private static bool IsSpace(byte c) => c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0;

        private static bool IsDelimiter(byte c) => c is (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'/';

        private void SkipWhitespace()
        {
            while (_pos < _data.Length && IsSpace(_data[_pos]))
            {
                _pos++;
            }
        }

        private readonly bool Peek2(string token) =>
            _pos + 1 < _data.Length && _data[_pos] == token[0] && _data[_pos + 1] == token[1];

        private EngineValue ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("EngineData nesting is too deep.");
            }

            SkipWhitespace();
            if (_pos >= _data.Length)
            {
                throw new FormatException("Unexpected end of EngineData.");
            }

            var c = _data[_pos];
            if (Peek2("<<"))
            {
                _pos += 2;
                return ParseDictionaryEntries(depth + 1, bare: false);
            }

            if (c == (byte)'[')
            {
                _pos++;
                var items = new List<EngineValue>();
                while (true)
                {
                    SkipWhitespace();
                    if (_pos >= _data.Length)
                    {
                        throw new FormatException("Unterminated EngineData list.");
                    }

                    if (_data[_pos] == (byte)']')
                    {
                        _pos++;
                        return new EngineValue { Kind = EngineValueKind.List, Items = items };
                    }

                    items.Add(ParseValue(depth + 1));
                }
            }

            if (c == (byte)'(')
            {
                _pos++;
                var start = _pos;
                while (_pos < _data.Length)
                {
                    if (_data[_pos] == (byte)'\\')
                    {
                        _pos += 2;
                        continue;
                    }

                    if (_data[_pos] == (byte)')')
                    {
                        break;
                    }

                    _pos++;
                }

                if (_pos >= _data.Length)
                {
                    throw new FormatException("Unterminated EngineData string.");
                }

                var text = DecodeString(_data[start.._pos]);
                _pos++;
                return new EngineValue { Kind = EngineValueKind.String, Text = text };
            }

            if (c == (byte)'/')
            {
                _pos++;
                return new EngineValue { Kind = EngineValueKind.Name, Text = ReadBareToken() };
            }

            if (c == (byte)'>' || c == (byte)']' || c == (byte)')')
            {
                throw new FormatException("Unexpected EngineData delimiter.");
            }

            var token = ReadBareToken();
            if (token == "true" || token == "false")
            {
                return new EngineValue { Kind = EngineValueKind.Boolean, Boolean = token == "true" };
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return new EngineValue { Kind = EngineValueKind.Number, Number = number };
            }

            // Unknown bare tokens (rare) are kept as names.
            return new EngineValue { Kind = EngineValueKind.Name, Text = token };
        }

        private string ReadBareToken()
        {
            var start = _pos;
            while (_pos < _data.Length && !IsSpace(_data[_pos]) && !IsDelimiter(_data[_pos]))
            {
                _pos++;
            }

            if (_pos == start)
            {
                throw new FormatException("Empty EngineData token.");
            }

            return Encoding.Latin1.GetString(_data[start.._pos]);
        }

        private EngineValue ParseDictionaryEntries(int depth, bool bare)
        {
            var entries = new List<KeyValuePair<string, EngineValue>>();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _data.Length)
                {
                    if (bare)
                    {
                        return new EngineValue { Kind = EngineValueKind.Dictionary, Entries = entries };
                    }

                    throw new FormatException("Unterminated EngineData dictionary.");
                }

                if (Peek2(">>"))
                {
                    _pos += 2;
                    if (bare)
                    {
                        throw new FormatException("Unbalanced EngineData dictionary.");
                    }

                    return new EngineValue { Kind = EngineValueKind.Dictionary, Entries = entries };
                }

                if (_data[_pos] != (byte)'/')
                {
                    throw new FormatException("EngineData dictionary key expected.");
                }

                _pos++;
                var key = ReadBareToken();
                entries.Add(new(key, ParseValue(depth + 1)));
            }
        }
    }
}
