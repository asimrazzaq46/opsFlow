using System.Globalization;
using System.Text;

namespace OpsFlow.Application.Analytics;

/// <summary>
/// RFC 4180 CSV with CRLF rows. A text cell whose first meaningful character (after leading whitespace/control characters) is a
/// formula trigger (= + - @, TAB/CR, or their full-width forms) is prefixed with an apostrophe (spreadsheet formula injection).
/// </summary>
public static class CsvWriter
{
    public static string Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', headers)).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(Row(row));
        }

        return sb.ToString();
    }

    /// <summary>One CSV record including its line terminator.</summary>
    public static string Row(IReadOnlyList<object?> cells) => string.Join(',', cells.Select(Cell)) + "\r\n";

    /// <summary>UTF-8 with BOM (Excel) written to the stream in one go; used for the small summary exports.</summary>
    public static async Task WriteUtf8Async(Stream stream, string content, CancellationToken ct)
    {
        await stream.WriteAsync(Encoding.UTF8.GetPreamble(), ct);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
    }

    public static string Cell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            DateTime d => d.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            double n => n.ToString("0.##", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (value is string && StartsWithFormulaTrigger(text))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    private static bool StartsWithFormulaTrigger(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) && ch is not ('\t' or '\r') || char.IsControl(ch) && ch is not ('\t' or '\r'))
            {
                continue; // spreadsheets may ignore leading blanks/control characters before the formula
            }

            return ch is '=' or '+' or '-' or '@' or '\t' or '\r' or '＝' or '＋' or '－' or '＠';
        }

        return false;
    }
}
