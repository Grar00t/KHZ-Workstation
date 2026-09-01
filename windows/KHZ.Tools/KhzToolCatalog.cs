using System.Collections.Generic;
using KHZ.Tools.Office;
using KHZ.Tools.Tools;

namespace KHZ.Tools;

/// <summary>
/// The authoritative list of local agent capabilities.
/// </summary>
/// <remarks>
/// Both hosts build from this single list: the WPF chat surface and the
/// standalone MCP server. That is what keeps the in-app agent and any external
/// MCP host on identical semantics, limits, and safety behaviour instead of two
/// drifting implementations.
/// </remarks>
public static class KhzToolCatalog
{
    /// <summary>All tools, read tools first.</summary>
    public static IReadOnlyList<IKhzTool> CreateAll() =>
    [
        new ListDirectoryTool(),
        new ReadFileTool(),
        new SearchTextTool(),
        new ReadWordDocumentTool(),
        new ReadSheetTool(),
        new ReadSlidesTool(),
        new ReplaceTextTool(),
        new EditWordDocumentTool(),
        new WriteCellsTool(),
        new WriteSlideTextTool(),
        new ConvertToPdfTool(),
        new RunPowerShellTool()
    ];

    /// <summary>Read-only subset, for observation-only sessions.</summary>
    public static IReadOnlyList<IKhzTool> CreateReadOnly() =>
    [
        new ListDirectoryTool(),
        new ReadFileTool(),
        new SearchTextTool(),
        new ReadWordDocumentTool(),
        new ReadSheetTool(),
        new ReadSlidesTool()
    ];

    /// <summary>Router over the full catalog.</summary>
    public static ToolRouter CreateRouter() => new(CreateAll());

    /// <summary>Router over the read-only catalog.</summary>
    public static ToolRouter CreateReadOnlyRouter() => new(CreateReadOnly());
}
