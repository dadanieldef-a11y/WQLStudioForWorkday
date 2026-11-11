using System.Text;
using System.Text.RegularExpressions;

namespace ExtendExplorer.Client.Services;

// Small, best-effort WQL clause editing for the results-grid column menu — the
// C# port of lib/wql-edit.ts. Regex-based rather than a real WQL parser; WQL is
// regular enough at this level to be honest, and the result is always shown in
// the editor for the user to adjust. Assumes the conventional clause order
// (… WHERE ON … WHERE … GROUP BY … ORDER BY … LIMIT).

public enum SortDir
{
    Asc,
    Desc,
}

public static class WqlEdit
{
    public static string DirText(SortDir d) => d == SortDir.Asc ? "ASC" : "DESC";

    // Wrap a string constant for a WQL text predicate. WQL text literals use
    // single quotes, and an embedded quote is escaped by doubling it (SQL-style,
    // NOT a backslash) — so O'Brien becomes 'O''Brien'.
    private static string Quote(string v) =>
        "'" + (v ?? "").Replace("'", "''") + "'";

    public static string ContainsCondition(string field, string term) =>
        $"{field} contains {Quote(term)}";

    // `is empty` / `is not empty` — WQL's null test (NOT SQL `IS NULL`).
    public static string EmptyCondition(string field, bool negate) =>
        $"{field} is {(negate ? "not " : "")}empty";

    // Booleans compare with `=` in WQL (contains / is empty don't apply).
    public static string BoolCondition(string field, bool value) =>
        $"{field} = {(value ? "true" : "false")}";

    // Numeric / date comparison: `field >= 5000`, `field < '2024-01-01'`.
    // Numbers go unquoted; anything else (dates) is quoted as a WQL literal.
    public static string CompareCondition(string field, string op, string value)
    {
        var v = (value ?? "").Trim();
        var isNumber = double.TryParse(v, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _);
        return $"{field} {op} {(isNumber ? v : Quote(v))}";
    }

    // Everything from the first GROUP BY / ORDER BY / LIMIT onwards: the tail we
    // must not disturb when rewriting the WHERE clause.
    private const string TailPattern = @"\s+(group\s+by|order\s+by|limit\b)";

    // The first *plain* WHERE — deliberately not `WHERE ON`, which filters a
    // related business object, belongs before the plain WHERE, and must be left
    // in the head untouched.
    private const string WherePattern = @"\bwhere\b(?!\s+on\b)";

    // Replace any existing ORDER BY with a fresh single-field sort, preserving a
    // trailing LIMIT (which would otherwise be swallowed by the strip).
    public static string SetOrderBy(string wql, string field, SortDir dir)
    {
        var body = wql;
        var limit = "";
        var limitM = Regex.Match(body, @"\s+limit\s+\d+\s*$", RegexOptions.IgnoreCase);
        if (limitM.Success)
        {
            limit = limitM.Value.Trim();
            body = body[..limitM.Index];
        }

        var stripped = Regex.Replace(body, @"\s+order\s+by\s+[\s\S]*$", "",
            RegexOptions.IgnoreCase).TrimEnd();
        var result = $"{stripped}\nORDER BY {field} {DirText(dir)}";
        return limit.Length > 0 ? $"{result}\n{limit}" : result;
    }

    // Split a WHERE body into its top-level AND-joined predicates, respecting
    // parentheses and quoted strings. Top-level OR groups stay intact as one part.
    private static List<string> SplitTopLevelAnd(string where)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quoteCh = null;
        var cur = new StringBuilder();
        for (var i = 0; i < where.Length; i++)
        {
            var ch = where[i];
            if (quoteCh is not null)
            {
                cur.Append(ch);
                if (ch == quoteCh)
                {
                    // A doubled quote is an escaped quote — stay inside the literal.
                    if (i + 1 < where.Length && where[i + 1] == quoteCh)
                    {
                        cur.Append(where[i + 1]);
                        i++;
                    }
                    else
                    {
                        quoteCh = null;
                    }
                }
                continue;
            }
            if (ch is '\'' or '"')
            {
                quoteCh = ch;
                cur.Append(ch);
                continue;
            }
            if (ch == '(')
            {
                depth++;
                cur.Append(ch);
                continue;
            }
            if (ch == ')')
            {
                depth = Math.Max(0, depth - 1);
                cur.Append(ch);
                continue;
            }
            if (depth == 0 && char.IsWhiteSpace(ch))
            {
                var m = Regex.Match(where[i..], @"^\s+and\s+", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    parts.Add(cur.ToString());
                    cur.Clear();
                    i += m.Value.Length - 1;
                    continue;
                }
            }
            cur.Append(ch);
        }
        if (cur.ToString().Trim().Length > 0) parts.Add(cur.ToString());
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    // A query broken into: everything before the plain WHERE (including any
    // WHERE ON blocks), that WHERE's top-level predicates, and the untouched
    // GROUP BY / ORDER BY / LIMIT tail.
    private readonly record struct QueryParts(string PreWhere, List<string> Predicates, string Tail);

    private static QueryParts SplitQuery(string wql)
    {
        var tailM = Regex.Match(wql, TailPattern, RegexOptions.IgnoreCase);
        var splitAt = tailM.Success ? tailM.Index : wql.Length;
        var head = wql[..splitAt];
        var tail = wql[splitAt..];

        var whereM = Regex.Match(head, WherePattern, RegexOptions.IgnoreCase);
        if (!whereM.Success) return new QueryParts(head.TrimEnd(), new List<string>(), tail);

        var pre = head[..whereM.Index].TrimEnd();
        var body = head[(whereM.Index + whereM.Length)..].Trim();
        return new QueryParts(pre, SplitTopLevelAnd(body), tail);
    }

    private static string Rebuild(QueryParts q, List<string> predicates) =>
        predicates.Count == 0
            ? q.PreWhere + q.Tail
            : $"{q.PreWhere}\nWHERE {string.Join(" AND ", predicates)}{q.Tail}";

    // Does this predicate look like one the column menu generated for `field`
    // (contains / is empty / is not empty / a comparison such as `= true` or
    // `>= 5000`)? Used to replace rather than stack. Deliberately does NOT match
    // `field in (…)`, which drill-down and chip-append own.
    private static bool IsMenuPredicate(string chunk, string field)
    {
        var c = chunk.Trim();
        if (c.StartsWith("(") && c.EndsWith(")")) c = c[1..^1].Trim();
        var f = Regex.Escape(field);
        return Regex.IsMatch(c, $@"^{f}\s+(contains\s+|is\s+(not\s+)?empty\b)",
                   RegexOptions.IgnoreCase)
            || Regex.IsMatch(c, $@"^{f}\s*(=|>=|<=|>|<)\s*\S", RegexOptions.IgnoreCase);
    }

    // Set the column-menu filter for `field`: regenerate the WHERE so any prior
    // menu-predicate on the same field is replaced (one filter per column),
    // leaving drill-down `in (…)` predicates and hand-written conditions in
    // place. The new predicate lands before any GROUP BY / ORDER BY / LIMIT tail.
    public static string SetColumnFilter(string wql, string field, string condition)
    {
        var q = SplitQuery(wql);
        var kept = q.Predicates.Where(c => !IsMenuPredicate(c, field)).ToList();
        kept.Add(condition);
        return Rebuild(q, kept);
    }

    // Matches a top-level `field in (a, b, …)` predicate, with or without
    // wrapping parentheses, capturing the id list.
    private static Regex InPredicate(string field) =>
        new($@"^\(?\s*{Regex.Escape(field)}\s+in\s*\((?<ids>[^)]*)\)\s*\)?$",
            RegexOptions.IgnoreCase);

    // Add an instance/reference filter for `field`. If the query already has a
    // top-level `field in (…)` predicate, the id is MERGED into that list rather
    // than added as a second predicate — two `in` predicates on one field are
    // ANDed together and match nothing. An id that's already present is a no-op.
    public static string AddInFilter(string wql, string field, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return wql;

        var q = SplitQuery(wql);
        var predicates = new List<string>(q.Predicates);
        var rx = InPredicate(field);

        for (var i = 0; i < predicates.Count; i++)
        {
            var m = rx.Match(predicates[i].Trim());
            if (!m.Success) continue;

            var ids = m.Groups["ids"].Value
                .Split(',')
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            if (ids.Contains(id, StringComparer.OrdinalIgnoreCase)) return wql; // already filtered
            ids.Add(id);
            predicates[i] = $"{field} in ({string.Join(", ", ids)})";
            return Rebuild(q, predicates);
        }

        predicates.Add($"{field} in ({id})");
        return Rebuild(q, predicates);
    }

    public sealed record OrderBy(string Field, SortDir Dir);

    // Read back a single-field ORDER BY (optionally followed by LIMIT) so the
    // grid can show a ▲/▼ indicator on the sorted column.
    public static OrderBy? ParseOrderBy(string wql)
    {
        var m = Regex.Match(wql,
            @"\border\s+by\s+([a-zA-Z_][\w.]*)\s*(asc|desc)?\s*(limit\s+\d+\s*)?$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var dir = m.Groups[2].Success && m.Groups[2].Value.ToUpperInvariant() == "DESC"
            ? SortDir.Desc
            : SortDir.Asc;
        return new OrderBy(m.Groups[1].Value, dir);
    }
}
