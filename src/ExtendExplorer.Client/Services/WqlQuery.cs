using System.Text;
using System.Text.RegularExpressions;

namespace ExtendExplorer.Client.Services;

// Pure, side-effect-free helpers for inspecting and preparing a WQL query
// before it runs. Moved out of Explorer.razor so the workbench component only
// orchestrates; these are regex-level, not a real WQL parser.
public static class WqlQuery
{
    // The FROM source alias and the raw SELECT list of a query.
    public static (string? Source, string? SelectList) Parse(string wql)
    {
        var fromM = Regex.Match(wql, @"\bfrom\s+([a-zA-Z_]\w*)", RegexOptions.IgnoreCase);
        var selM = Regex.Match(wql, @"\bselect\b([\s\S]*?)\bfrom\b", RegexOptions.IgnoreCase);
        return (
            fromM.Success ? fromM.Groups[1].Value : null,
            selM.Success ? selM.Groups[1].Value.Trim() : null);
    }

    // Guarantee workdayID is returned (Update/Delete key off it). Prepends it to
    // the SELECT list when absent; leaves `SELECT *` and already-present queries
    // untouched.
    public static string EnsureWorkdayId(string wql)
    {
        var m = Regex.Match(wql, @"(\bselect\b)([\s\S]*?)(\bfrom\b)", RegexOptions.IgnoreCase);
        if (!m.Success) return wql;
        var list = m.Groups[2].Value;
        if (Regex.IsMatch(list, @"\bworkdayID\b", RegexOptions.IgnoreCase) || list.Trim() == "*") return wql;
        var trimmed = list.Trim();
        var newList = trimmed.Length > 0 ? $" workdayID, {trimmed}\n" : " workdayID\n";
        var selEnd = m.Groups[1].Index + m.Groups[1].Length;
        var fromStart = m.Groups[3].Index;
        return wql[..selEnd] + newList + wql[fromStart..];
    }

    // Swap the SELECT list for `selectList`, keeping FROM / WHERE / ORDER BY /
    // etc. untouched. Returns the query unchanged if it has no SELECT … FROM.
    public static string ReplaceSelectList(string wql, string selectList)
    {
        var m = Regex.Match(wql, @"(\bselect\b)([\s\S]*?)(\bfrom\b)", RegexOptions.IgnoreCase);
        if (!m.Success) return wql;
        var selEnd = m.Groups[1].Index + m.Groups[1].Length;
        var fromStart = m.Groups[3].Index;
        return wql[..selEnd] + $" {selectList}\n" + wql[fromStart..];
    }

    // WQL has no comments, so strip our editor-only ones before executing: `--`
    // to end of line and `/* ... */` blocks, ignoring occurrences inside single-
    // quoted string literals.
    public static string StripComments(string wql)
    {
        var sb = new StringBuilder();
        var i = 0;
        var inStr = false;
        var n = wql.Length;
        while (i < n)
        {
            var c = wql[i];
            var next = i + 1 < n ? wql[i + 1] : '\0';
            if (inStr)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    if (next == '\'')
                    {
                        sb.Append('\'');
                        i += 2;
                        continue;
                    }
                    inStr = false;
                }
                i++;
                continue;
            }
            if (c == '\'')
            {
                inStr = true;
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '-' && next == '-')
            {
                i += 2;
                while (i < n && wql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                i += 2;
                while (i < n && !(wql[i] == '*' && i + 1 < n && wql[i + 1] == '/')) i++;
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
