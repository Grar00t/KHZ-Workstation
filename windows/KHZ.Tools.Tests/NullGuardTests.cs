using System;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using KHZ.Tools.Office;
using KHZ.Tools.Tools;
using Xunit;

namespace KHZ.Tools.Tests;

/// <summary>
/// Null-input coverage for the ten CS8602 dereference sites in
/// ExcelTools.cs and PowerPointTools.cs. Each guarded root must fail
/// closed with a ToolFailureException, never a NullReferenceException.
/// </summary>
public sealed class NullGuardTests
{
    [Theory]
    [InlineData(null)]
    public void ExcelPackage_RequireWorkbook_throws_for_null(WorkbookPart? part)
    {
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorkbook(part!));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    public void ExcelPackage_RequireWorksheet_throws_for_null(WorksheetPart? part)
    {
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorksheet(part!));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(null)]
    public void SlidePackage_RequireSlide_throws_for_null(SlidePart? part)
    {
        var ex = Assert.Throws<ToolFailureException>(
            () => SlidePackage.RequireSlide(part!));
        Assert.Equal("invalid_package", ex.Code);
    }
}
