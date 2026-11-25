using System.Globalization;

namespace ExtendExplorer.Client.Services;

// Shared scalar parsing for the write dialogs (Insert, Update, Bulk insert,
// Bulk edit).
public static class Scalars
{
    // Parse a typed/pasted number the same way in every browser language.
    //
    // A Blazor WebAssembly app takes its culture from the browser, so a bare
    // double.TryParse reads "1.5" as 15 on a German-locale browser (where "."
    // groups thousands). This always uses the invariant culture and does NOT
    // accept thousands separators: digits, an optional "." decimal point, an
    // optional sign and exponent. Anything else ("1,5", "1,000") is left for
    // the caller to send as text, so Workday rejects it visibly instead of a
    // different number being stored silently. NaN / Infinity are refused too —
    // they can't be written as JSON numbers.
    public static bool TryParseNumber(string? text, out double value) =>
        double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
