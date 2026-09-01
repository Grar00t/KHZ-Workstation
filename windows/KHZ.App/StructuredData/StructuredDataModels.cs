using System.Collections.Generic;

namespace KHZ.App.StructuredData;

internal enum StructuredDataType
{
    Text,
    Integer,
    Real,
    Blob
}

internal sealed record DataColumnDefinition(
    string Name,
    StructuredDataType Type);

internal sealed record DataTableInfo(
    string TableId,
    string WorkspaceId,
    string Name,
    string SqlName,
    IReadOnlyList<DataColumnDefinition> Columns,
    string CreatedUtc);

internal sealed record DataQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
