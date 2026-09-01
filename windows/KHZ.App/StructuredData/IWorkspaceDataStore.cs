using System.Collections.Generic;

namespace KHZ.App.StructuredData;

internal interface IWorkspaceDataStore
{
    string CreateTable(
        string name,
        IReadOnlyList<DataColumnDefinition> columns);

    IReadOnlyList<DataTableInfo> ListTables();

    string AddRow(
        string tableId,
        IReadOnlyDictionary<string, object?> values);

    DataQueryResult Query(
        string tableId,
        int limit = 500,
        IReadOnlyDictionary<string, object?>? filters = null,
        string? sortBy = null,
        bool descending = false);
}
