using System.Globalization;

namespace ReviewTool.Core.Reviews;

/// <summary>
/// A set of line numbers on the old ("O") and new ("N") side of a diff, serialized compactly as
/// ranges such as <c>N12-20</c> or <c>O5</c>.
/// </summary>
public sealed class LineRangeSet
{
    private readonly SortedSet<int> _new = [];
    private readonly SortedSet<int> _old = [];

    public int Count => _new.Count + _old.Count;

    public static LineRangeSet Parse(IEnumerable<string>? ranges)
    {
        var set = new LineRangeSet();
        if (ranges != null) set.Add(ranges);
        return set;
    }

    public void Add(IEnumerable<string> ranges) => Apply(ranges, add: true);

    public void Remove(IEnumerable<string> ranges) => Apply(ranges, add: false);

    public bool Contains(char side, int line) => (side == 'O' ? _old : _new).Contains(line);

    private void Apply(IEnumerable<string> ranges, bool add)
    {
        foreach (var raw in ranges)
        {
            if (!TryParse(raw, out var side, out var from, out var to)) throw new ArgumentException($"Invalid line range '{raw}'. Expected e.g. N12-20 or O5.");
            if (to - from > 1_000_000) throw new ArgumentException("Line range too large.");
            var target = side == 'O' ? _old : _new;
            for (var i = from; i <= to; i++)
            {
                if (add) target.Add(i); else target.Remove(i);
            }
        }
    }

    private static bool TryParse(string raw, out char side, out int from, out int to)
    {
        side = default;
        from = to = 0;
        if (string.IsNullOrEmpty(raw) || raw.Length < 2 || (raw[0] != 'N' && raw[0] != 'O')) return false;
        side = raw[0];
        var body = raw.AsSpan(1);
        var dash = body.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out from)) return false;
            to = from;
        }
        else if (!int.TryParse(body[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out from)
                 || !int.TryParse(body[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out to))
        {
            return false;
        }
        return from >= 1 && to >= from;
    }

    /// <summary>Compact, sorted representation (old side first).</summary>
    public List<string> ToRanges()
    {
        var result = new List<string>();
        Emit('O', _old, result);
        Emit('N', _new, result);
        return result;
    }

    private static void Emit(char side, SortedSet<int> lines, List<string> into)
    {
        int? start = null, prev = null;
        foreach (var n in lines)
        {
            if (start == null) start = n;
            else if (n != prev + 1)
            {
                into.Add(Format(side, start.Value, prev!.Value));
                start = n;
            }
            prev = n;
        }
        if (start != null) into.Add(Format(side, start.Value, prev!.Value));
    }

    private static string Format(char side, int from, int to)
        => from == to ? $"{side}{from}" : $"{side}{from}-{to}";
}
