using System.Text;

namespace PCBoost.Gaming.Detection;

/// <summary>Nœud KeyValues (format VDF de Valve) : une valeur texte ou un bloc d'enfants.</summary>
public sealed class VdfNode
{
    private readonly List<VdfNode> _children;

    internal VdfNode(string key, string? value, List<VdfNode>? children = null)
    {
        Key = key;
        Value = value;
        _children = children ?? [];
    }

    public string Key { get; }

    /// <summary>Valeur texte, ou null pour un bloc.</summary>
    public string? Value { get; }

    public bool IsBlock => Value is null;

    public IReadOnlyList<VdfNode> Children => _children;

    /// <summary>Premier enfant portant cette clé (insensible à la casse), ou null.</summary>
    public VdfNode? this[string key] => _children.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valeur texte de l'enfant <paramref name="key"/>, ou null.</summary>
    public string? GetValue(string key) => this[key]?.Value;

    internal void Add(VdfNode child) => _children.Add(child);

    public override string ToString() => IsBlock ? $"\"{Key}\" {{{_children.Count}}}" : $"\"{Key}\" \"{Value}\"";
}

/// <summary>
/// Analyseur KeyValues/VDF tolérant (libraryfolders.vdf, appmanifest_*.acf) : clés et valeurs entre guillemets ou nues,
/// blocs imbriqués, commentaires « // », conditions « [$WIN32] » ignorées, échappements « \\ », « \" », « \n », « \t ».
/// Ne lève jamais d'exception : un fichier tronqué produit ce qui a pu être lu.
/// </summary>
public static class VdfParser
{
    private const int MaxDepth = 64;

    /// <summary>Analyse le texte et renvoie un nœud racine (clé vide) contenant les paires de premier niveau.</summary>
    public static VdfNode Parse(string? text)
    {
        var root = new VdfNode(string.Empty, null);
        if (string.IsNullOrEmpty(text)) return root;
        var reader = new Tokenizer(text);
        ParseBlock(reader, root, 0);
        return root;
    }

    private static void ParseBlock(Tokenizer reader, VdfNode parent, int depth)
    {
        while (true)
        {
            var token = reader.Next();
            switch (token.Kind)
            {
                case TokenKind.End:
                    return;
                case TokenKind.Close:
                    if (depth > 0) return;
                    continue; // « } » orphelin au premier niveau : ignoré.
                case TokenKind.Open:
                    // Bloc sans clé : lu puis ignoré pour rester synchronisé.
                    ParseBlock(reader, new VdfNode(string.Empty, null), depth + 1);
                    continue;
                case TokenKind.Conditional:
                    continue;
                case TokenKind.Text:
                    break;
            }

            var key = token.Text;
            var next = reader.Next();
            while (next.Kind == TokenKind.Conditional) next = reader.Next();

            switch (next.Kind)
            {
                case TokenKind.Text:
                    parent.Add(new VdfNode(key, next.Text));
                    reader.SkipConditional();
                    break;
                case TokenKind.Open:
                    var block = new VdfNode(key, null);
                    parent.Add(block);
                    if (depth + 1 >= MaxDepth) { reader.SkipBlock(); break; }
                    ParseBlock(reader, block, depth + 1);
                    break;
                case TokenKind.Close:
                    // Clé sans valeur juste avant la fin du bloc.
                    if (depth > 0) return;
                    break;
                case TokenKind.End:
                    return;
            }
        }
    }

    private enum TokenKind { End, Text, Open, Close, Conditional }

    private readonly record struct Token(TokenKind Kind, string Text = "");

    private sealed class Tokenizer(string text)
    {
        private int _pos = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;

        public Token Next()
        {
            SkipTrivia();
            if (_pos >= text.Length) return new Token(TokenKind.End);
            var c = text[_pos];
            switch (c)
            {
                case '{': _pos++; return new Token(TokenKind.Open);
                case '}': _pos++; return new Token(TokenKind.Close);
                case '"': return new Token(TokenKind.Text, ReadQuoted());
                case '[':
                    var end = text.IndexOf(']', _pos);
                    _pos = end < 0 ? text.Length : end + 1;
                    return new Token(TokenKind.Conditional);
                default: return new Token(TokenKind.Text, ReadBare());
            }
        }

        /// <summary>Ignore une condition « [$X] » placée après une valeur, sur la même ligne.</summary>
        public void SkipConditional()
        {
            var save = _pos;
            while (_pos < text.Length && (text[_pos] == ' ' || text[_pos] == '\t')) _pos++;
            if (_pos < text.Length && text[_pos] == '[')
            {
                var end = text.IndexOf(']', _pos);
                _pos = end < 0 ? text.Length : end + 1;
            }
            else
            {
                _pos = save;
            }
        }

        public void SkipBlock()
        {
            var depth = 1;
            while (depth > 0)
            {
                var t = Next();
                if (t.Kind == TokenKind.End) return;
                if (t.Kind == TokenKind.Open) depth++;
                else if (t.Kind == TokenKind.Close) depth--;
            }
        }

        private void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c)) { _pos++; continue; }
                if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    var eol = text.IndexOf('\n', _pos);
                    _pos = eol < 0 ? text.Length : eol + 1;
                    continue;
                }
                break;
            }
        }

        private string ReadQuoted()
        {
            _pos++; // guillemet ouvrant
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && _pos < text.Length)
                {
                    var e = text[_pos];
                    switch (e)
                    {
                        case '\\': sb.Append('\\'); _pos++; continue;
                        case '"': sb.Append('"'); _pos++; continue;
                        case 'n': sb.Append('\n'); _pos++; continue;
                        case 't': sb.Append('\t'); _pos++; continue;
                        default:
                            // Échappement inconnu (ex. ancien chemin « D:\Games ») : conservé tel quel.
                            sb.Append('\\');
                            continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString(); // Chaîne non terminée : fin de fichier.
        }

        private string ReadBare()
        {
            var start = _pos;
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"') break;
                _pos++;
            }
            if (_pos == start) _pos++; // caractère isolé non reconnu
            return text[start.._pos];
        }
    }
}
