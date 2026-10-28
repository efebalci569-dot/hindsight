using System.Text;

namespace Hindsight.Core.Games;

/// <summary>A node in Valve's text KeyValues format: either a string value or a set of children.</summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode>? _children;

    private VdfNode(string? value, Dictionary<string, VdfNode>? children)
    {
        Value = value;
        _children = children;
    }

    public string? Value { get; }

    public bool IsObject => _children is not null;

    public IReadOnlyDictionary<string, VdfNode> Children =>
        _children ?? (IReadOnlyDictionary<string, VdfNode>)new Dictionary<string, VdfNode>();

    public VdfNode? this[string key] => _children is not null && _children.TryGetValue(key, out var node) ? node : null;

    public string? GetString(string key) => this[key]?.Value;

    internal static VdfNode Leaf(string value) => new(value, null);

    internal static VdfNode Object(Dictionary<string, VdfNode> children) => new(null, children);
}

public sealed class VdfFormatException(string message) : FormatException(message);

/// <summary>
/// Parser for the text KeyValues files Steam writes (libraryfolders.vdf, appmanifest_*.acf).
/// Supports quoted and unquoted tokens, escapes, comments and conditional suffixes like [$WIN32].
/// Keys are case-insensitive; when a key repeats, the last value wins.
/// </summary>
public static class VdfParser
{
    public static VdfNode Parse(string text)
    {
        var reader = new Reader(text);
        var root = ParseObject(reader, isRoot: true);
        return VdfNode.Object(root);
    }

    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path));

    private static Dictionary<string, VdfNode> ParseObject(Reader reader, bool isRoot)
    {
        var children = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var token = reader.Next();
            if (token is null)
            {
                if (isRoot)
                {
                    return children;
                }

                throw new VdfFormatException("Unexpected end of file: missing '}'.");
            }

            if (token.Value.Kind == TokenKind.CloseBrace)
            {
                if (isRoot)
                {
                    throw new VdfFormatException("Unexpected '}' at top level.");
                }

                return children;
            }

            if (token.Value.Kind != TokenKind.String)
            {
                throw new VdfFormatException($"Expected a key but found '{token.Value.Text}' at line {reader.Line}.");
            }

            var key = token.Value.Text;
            var value = reader.Next() ?? throw new VdfFormatException($"Key '{key}' has no value.");
            switch (value.Kind)
            {
                case TokenKind.OpenBrace:
                    children[key] = VdfNode.Object(ParseObject(reader, isRoot: false));
                    break;
                case TokenKind.String:
                    children[key] = VdfNode.Leaf(value.Text);
                    break;
                default:
                    throw new VdfFormatException($"Unexpected '}}' after key '{key}'.");
            }

            reader.SkipConditional();
        }
    }

    private enum TokenKind { String, OpenBrace, CloseBrace }

    private readonly record struct Token(TokenKind Kind, string Text);

    private sealed class Reader(string text)
    {
        private int _position;

        public int Line { get; private set; } = 1;

        public Token? Next()
        {
            SkipWhitespaceAndComments();
            if (_position >= text.Length)
            {
                return null;
            }

            var ch = text[_position];
            switch (ch)
            {
                case '{':
                    _position++;
                    return new Token(TokenKind.OpenBrace, "{");
                case '}':
                    _position++;
                    return new Token(TokenKind.CloseBrace, "}");
                case '"':
                    return new Token(TokenKind.String, ReadQuoted());
                default:
                    return new Token(TokenKind.String, ReadUnquoted());
            }
        }

        /// <summary>Skips an optional platform conditional such as <c>[$WIN32]</c> after a value.</summary>
        public void SkipConditional()
        {
            var save = _position;
            SkipWhitespaceAndComments();
            if (_position < text.Length && text[_position] == '[')
            {
                var end = text.IndexOf(']', _position);
                _position = end < 0 ? text.Length : end + 1;
                return;
            }

            _position = save;
        }

        private string ReadQuoted()
        {
            _position++; // opening quote
            var builder = new StringBuilder();
            while (_position < text.Length)
            {
                var ch = text[_position++];
                if (ch == '"')
                {
                    return builder.ToString();
                }

                if (ch == '\\' && _position < text.Length)
                {
                    var escaped = text[_position++];
                    builder.Append(escaped switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => escaped,
                    });
                    continue;
                }

                if (ch == '\n')
                {
                    Line++;
                }

                builder.Append(ch);
            }

            throw new VdfFormatException("Unterminated quoted string.");
        }

        private string ReadUnquoted()
        {
            var start = _position;
            while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && text[_position] is not ('{' or '}' or '"'))
            {
                _position++;
            }

            return text[start.._position];
        }

        private void SkipWhitespaceAndComments()
        {
            while (_position < text.Length)
            {
                var ch = text[_position];
                if (ch == '\n')
                {
                    Line++;
                    _position++;
                }
                else if (char.IsWhiteSpace(ch) || ch == '﻿')
                {
                    _position++;
                }
                else if (ch == '/' && _position + 1 < text.Length && text[_position + 1] == '/')
                {
                    while (_position < text.Length && text[_position] != '\n')
                    {
                        _position++;
                    }
                }
                else
                {
                    return;
                }
            }
        }
    }
}
