using System.Text;
using System.Text.RegularExpressions;

namespace Janet.Core;

/// <summary>
/// A blob path, row key or container name with <c>{name}</c> placeholders. Each placeholder except
/// <c>{client}</c> captures exactly one path segment; <c>{client}</c> is the client the caller names, so
/// matching with the source client and expanding with the target client is the rename.
/// </summary>
public sealed class StoragePattern
{
    public const string ClientPlaceholder = "client";

    // Alternating literal and placeholder parts; a placeholder part is flagged by IsPlaceholder.
    private readonly List<(bool IsPlaceholder, string Value)> _parts = [];

    private StoragePattern(string text)
    {
        Text = text;
    }

    /// <summary>The pattern as written; doubles as the rule's name.</summary>
    public string Text { get; }

    /// <summary>Placeholder names in order of first use, <c>client</c> included if used.</summary>
    public IReadOnlyList<string> Placeholders => _names;

    private readonly List<string> _names = [];

    public static StoragePattern Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pattern = new StoragePattern(text);
        var names = pattern._names;
        var literal = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new GraphException($"Pattern '{text}' has an unclosed '{{'.");
                }

                var name = text[(i + 1)..close];
                if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9]*$"))
                {
                    throw new GraphException($"Pattern '{text}' has a placeholder '{{{name}}}' that is not a plain name.");
                }

                if (names.Contains(name))
                {
                    throw new GraphException($"Pattern '{text}' uses {{{name}}} more than once.");
                }

                if (literal.Length > 0)
                {
                    pattern._parts.Add((false, literal.ToString()));
                    literal.Clear();
                }

                pattern._parts.Add((true, name));
                names.Add(name);
                i = close + 1;
            }
            else if (c == '}')
            {
                throw new GraphException($"Pattern '{text}' has a '}}' with no '{{'.");
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        if (literal.Length > 0)
        {
            pattern._parts.Add((false, literal.ToString()));
        }

        return pattern;
    }

    /// <summary>The captures keyed by placeholder name (never <c>client</c>), or null when value does not match.</summary>
    public IReadOnlyDictionary<string, string>? Match(string value, string client)
    {
        var rx = new StringBuilder("^");
        foreach (var (isPlaceholder, part) in _parts)
        {
            if (!isPlaceholder)
            {
                rx.Append(Regex.Escape(part));
            }
            else if (part == ClientPlaceholder)
            {
                rx.Append(Regex.Escape(client));
            }
            else
            {
                rx.Append("(?<").Append(part).Append(">[^/]+?)");
            }
        }

        rx.Append('$');
        var m = Regex.Match(value, rx.ToString(), RegexOptions.CultureInvariant);
        if (!m.Success)
        {
            return null;
        }

        var captures = new Dictionary<string, string>();
        foreach (var name in Placeholders)
        {
            if (name != ClientPlaceholder)
            {
                captures[name] = m.Groups[name].Value;
            }
        }

        return captures;
    }

    /// <summary>Writes the pattern with the captures filled in and <c>{client}</c> set to client.</summary>
    public string Expand(IReadOnlyDictionary<string, string> captures, string client)
    {
        var sb = new StringBuilder();
        foreach (var (isPlaceholder, part) in _parts)
        {
            if (!isPlaceholder)
            {
                sb.Append(part);
            }
            else if (part == ClientPlaceholder)
            {
                sb.Append(client);
            }
            else if (captures.TryGetValue(part, out var v))
            {
                sb.Append(v);
            }
            else
            {
                throw new GraphException($"Pattern '{Text}' needs {{{part}}}, which was not captured.");
            }
        }

        return sb.ToString();
    }

    public override string ToString() => Text;
}
