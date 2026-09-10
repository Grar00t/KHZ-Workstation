using System;
using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using KHZ.Tools.Office;
using KHZ.Tools.Tools;
using Xunit;

namespace KHZ.Tools.Tests;

/// <summary>
/// Null, empty, and valid input coverage for the ten formerly-nullable
/// dereference sites, grouped by the guard that protects each site.
///
/// ExcelTools.cs sites -> ExcelPackage.RequireWorkbook (L24,L152,L264,L471)
///                      -> ExcelPackage.RequireWorksheet (L217,L415)
/// PowerPointTools.cs sites -> SlidePackage.RequireSlide (L127,L241,L303,L325)
///
/// Inputs:
///   null  = null part
///   empty = part whose root element is null (corrupt package)
///   valid = part with a non-null root element
/// </summary>
public sealed class SiteNullInputTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string TempPath(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name);
        if (File.Exists(path)) File.Delete(path);
        _tempFiles.Add(path);
        return path;
    }

    // --- workbook/worksheet parts ---

    private WorkbookPart WorkbookPartWithRoot()
    {
        var d = SpreadsheetDocument.Create(TempPath("wb_valid.xlsx"), SpreadsheetDocumentType.Workbook);
        var wb = d.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        ws.Worksheet = new Worksheet(new SheetData());
        return wb;
    }

    private WorkbookPart WorkbookPartWithoutRoot()
    {
        var d = SpreadsheetDocument.Create(TempPath("wb_empty.xlsx"), SpreadsheetDocumentType.Workbook);
        return d.AddWorkbookPart(); // Workbook intentionally left null
    }

    private WorksheetPart WorksheetPartWithRoot()
        => WorkbookPartWithRoot().WorksheetParts.First();

    private WorksheetPart WorksheetPartWithoutRoot()
        => WorkbookPartWithoutRoot().AddNewPart<WorksheetPart>(); // Worksheet left null

    // --- slide parts ---

    private SlidePart SlidePartWithRoot()
    {
        var d = PresentationDocument.Create(TempPath("sl_valid.pptx"), PresentationDocumentType.Presentation);
        var sp = d.AddPresentationPart().AddNewPart<SlidePart>();
        sp.Slide = new Slide(new CommonSlideData(new ShapeTree()));
        return sp;
    }

    private SlidePart SlidePartWithoutRoot()
    {
        var d = PresentationDocument.Create(TempPath("sl_empty.pptx"), PresentationDocumentType.Presentation);
        return d.AddPresentationPart().AddNewPart<SlidePart>(); // Slide left null
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static readonly int[] WorkbookSites = { 24, 152, 264, 471 };
    private static readonly int[] WorksheetSites = { 217, 415 };
    private static readonly int[] SlideSites = { 127, 241, 303, 325 };

    // ===== RequireWorkbook: sites L24, L152, L264, L471 =====

    [Theory]
    [InlineData(24)]
    [InlineData(152)]
    [InlineData(264)]
    [InlineData(471)]
    public void RequireWorkbook_null_input_throws_for_site(int site)
    {
        Assert.Contains(site, WorkbookSites);
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorkbook(null!));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(152)]
    [InlineData(264)]
    [InlineData(471)]
    public void RequireWorkbook_empty_input_throws_for_site(int site)
    {
        Assert.Contains(site, WorkbookSites);
        var part = WorkbookPartWithoutRoot();
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorkbook(part));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(152)]
    [InlineData(264)]
    [InlineData(471)]
    public void RequireWorkbook_valid_input_returns_root_for_site(int site)
    {
        Assert.Contains(site, WorkbookSites);
        var part = WorkbookPartWithRoot();
        var root = ExcelPackage.RequireWorkbook(part);
        Assert.NotNull(root);
        Assert.IsType<Workbook>(root);
    }

    // ===== RequireWorksheet: sites L217, L415 =====

    [Theory]
    [InlineData(217)]
    [InlineData(415)]
    public void RequireWorksheet_null_input_throws_for_site(int site)
    {
        Assert.Contains(site, WorksheetSites);
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorksheet(null!));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(217)]
    [InlineData(415)]
    public void RequireWorksheet_empty_input_throws_for_site(int site)
    {
        Assert.Contains(site, WorksheetSites);
        var part = WorksheetPartWithoutRoot();
        var ex = Assert.Throws<ToolFailureException>(
            () => ExcelPackage.RequireWorksheet(part));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(217)]
    [InlineData(415)]
    public void RequireWorksheet_valid_input_returns_root_for_site(int site)
    {
        Assert.Contains(site, WorksheetSites);
        var part = WorksheetPartWithRoot();
        var root = ExcelPackage.RequireWorksheet(part);
        Assert.NotNull(root);
        Assert.IsType<Worksheet>(root);
    }

    // ===== RequireSlide: sites L127, L241, L303, L325 =====

    [Theory]
    [InlineData(127)]
    [InlineData(241)]
    [InlineData(303)]
    [InlineData(325)]
    public void RequireSlide_null_input_throws_for_site(int site)
    {
        Assert.Contains(site, SlideSites);
        var ex = Assert.Throws<ToolFailureException>(
            () => SlidePackage.RequireSlide(null!));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(241)]
    [InlineData(303)]
    [InlineData(325)]
    public void RequireSlide_empty_input_throws_for_site(int site)
    {
        Assert.Contains(site, SlideSites);
        var part = SlidePartWithoutRoot();
        var ex = Assert.Throws<ToolFailureException>(
            () => SlidePackage.RequireSlide(part));
        Assert.Equal("invalid_package", ex.Code);
    }

    [Theory]
    [InlineData(127)]
    [InlineData(241)]
    [InlineData(303)]
    [InlineData(325)]
    public void RequireSlide_valid_input_returns_root_for_site(int site)
    {
        Assert.Contains(site, SlideSites);
        var part = SlidePartWithRoot();
        var root = SlidePackage.RequireSlide(part);
        Assert.NotNull(root);
        Assert.IsType<Slide>(root);
    }
}
