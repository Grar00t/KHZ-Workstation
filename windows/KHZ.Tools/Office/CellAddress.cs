using System;
using System.Text;
using KHZ.Tools.Tools;

namespace KHZ.Tools.Office;

/// <summary>An A1-style spreadsheet cell reference.</summary>
/// <param name="Column">1-based column index (A is 1).</param>
/// <param name="Row">1-based row index.</param>
public readonly record struct CellAddress(int Column, int Row)
{
    /// <summary>Maximum column index supported by the XLSX format.</summary>
    public const int MaxColumn = 16_384;

    /// <summary>Maximum row index supported by the XLSX format.</summary>
    public const int MaxRow = 1_048_576;

    /// <summary>Parses a reference such as "B7", ignoring absolute markers.</summary>
    public static CellAddress Parse(string reference)
    {
        if (!TryParse(reference, out var address))
        {
            throw new ToolFailureException(
                "invalid_cell_reference",
                "Not a valid A1 cell reference: " + reference);
        }

        return address;
    }

    public static bool TryParse(string? reference, out CellAddress address)
    {
        address = default;
        var text = (reference ?? string.Empty).Replace("$", string.Empty).Trim();

        if (text.Length is < 2 or > 12)
            return false;

        var column = 0;
        var index = 0;

        while (index < text.Length && char.IsAsciiLetter(text[index]))
        {
            column = (column * 26) + (char.ToUpperInvariant(text[index]) - 'A' + 1);
            index++;
        }

        if (column is < 1 or > MaxColumn || index == 0 || index == text.Length)
            return false;

        if (!int.TryParse(text[index..], out var row) || row is < 1 or > MaxRow)
            return false;

        address = new CellAddress(column, row);
        return true;
    }

    /// <summary>Renders the reference in A1 form.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        var remaining = Column;

        while (remaining > 0)
        {
            var modulo = (remaining - 1) % 26;
            builder.Insert(0, (char)('A' + modulo));
            remaining = (remaining - modulo - 1) / 26;
        }

        return builder.Append(Row).ToString();
    }
}
