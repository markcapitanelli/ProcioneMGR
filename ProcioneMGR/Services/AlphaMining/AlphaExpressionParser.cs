using System.Globalization;

namespace ProcioneMGR.Services.AlphaMining;

/// <summary>
/// Parser dell'espressione alpha serializzata (S-expression prodotta da <see cref="AlphaNode.ToExpression"/>),
/// per ricostruire l'albero da un <c>SavedFactor</c> o da un nome di feature "expr:...". Ricorsivo
/// discendente; per gli operatori temporali l'ultimo argomento è la finestra (intero).
/// </summary>
public static class AlphaExpressionParser
{
    public static AlphaNode Parse(string expression)
    {
        var p = new Cursor(expression);
        var node = ParseExpr(p);
        p.SkipWhitespace();
        if (!p.AtEnd) throw new FormatException($"Testo residuo nell'espressione a {p.Position}: '{expression}'.");
        return node;
    }

    private static AlphaNode ParseExpr(Cursor p)
    {
        p.SkipWhitespace();
        var ch = p.Peek();

        if (ch == '$')
        {
            p.Next();
            var field = p.ReadWhile(char.IsLetter);
            return AlphaNode.Variable(field);
        }

        if (ch == '-' || ch == '.' || char.IsDigit(ch))
        {
            var num = p.ReadWhile(c => char.IsDigit(c) || c == '.' || c == '-' || c == 'e' || c == 'E' || c == '+');
            return AlphaNode.Constant(decimal.Parse(num, CultureInfo.InvariantCulture));
        }

        // Operatore: nome + '(' args ')'
        var name = p.ReadWhile(char.IsLetter);
        if (!Enum.TryParse<AlphaOp>(name, out var op) || op is AlphaOp.Var or AlphaOp.Const)
            throw new FormatException($"Operatore sconosciuto '{name}' a {p.Position}.");

        p.Expect('(');
        var args = new List<AlphaNode>();
        while (true)
        {
            args.Add(ParseExpr(p));
            p.SkipWhitespace();
            if (p.Peek() == ',') { p.Next(); continue; }
            break;
        }
        p.Expect(')');

        if (AlphaNode.IsTimeOp(op))
        {
            if (args.Count < 2) throw new FormatException($"L'operatore temporale {op} richiede una finestra.");
            var window = (int)args[^1].Const; // l'ultimo argomento è la finestra
            var children = args.Take(args.Count - 1).ToArray();
            return new AlphaNode { Op = op, Window = window, Children = children };
        }

        return new AlphaNode { Op = op, Children = args.ToArray() };
    }

    private sealed class Cursor(string text)
    {
        public int Position { get; private set; }
        public bool AtEnd => Position >= text.Length;

        public char Peek() => AtEnd ? '\0' : text[Position];
        public void Next() => Position++;

        public void SkipWhitespace() { while (!AtEnd && char.IsWhiteSpace(text[Position])) Position++; }

        public string ReadWhile(Func<char, bool> pred)
        {
            var start = Position;
            while (!AtEnd && pred(text[Position])) Position++;
            if (Position == start) throw new FormatException($"Token vuoto a {Position} in '{text}'.");
            return text[start..Position];
        }

        public void Expect(char c)
        {
            SkipWhitespace();
            if (AtEnd || text[Position] != c) throw new FormatException($"Atteso '{c}' a {Position} in '{text}'.");
            Position++;
        }
    }
}
