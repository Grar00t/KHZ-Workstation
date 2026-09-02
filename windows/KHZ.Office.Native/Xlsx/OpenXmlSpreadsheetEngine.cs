using System;
using System.Collections.Generic;
using KHZ.Office.Native.Formula;

namespace KHZ.Office.Native.Xlsx
{
	/// <summary>
	/// An in-process SpreadsheetML engine.
	/// <para>
	/// It claims exactly what it can do. Rendering and PDF export are false, because
	/// this engine has no typesetting or paint layer -- declaring them true and
	/// throwing later is the failure mode this contract was designed to remove.
	/// </para>
	/// </summary>
	public sealed class OpenXmlSpreadsheetEngine : IOfficeEngine
	{
		public const string EngineId = "khz.native.openxml.spreadsheet";

		public OfficeEngineDescriptor Describe()
		{
			Version assemblyVersion = typeof(OpenXmlSpreadsheetEngine).Assembly.GetName().Version;

			return new OfficeEngineDescriptor
			{
				Id = EngineId,
				DisplayName = "KHZ Native SpreadsheetML",

				// An in-process engine always knows its own version.
				Version = assemblyVersion != null ? assemblyVersion.ToString() : "0.0.0.0",
				InProcess = true,
				Capabilities = new OfficeEngineCapabilities
				{
					CanRead = true,
					CanWrite = true,
					CanRender = false,
					CanRecalculate = true,
					CanExportPdf = false,
					PreservesUnknownParts = true,
					RequiresExternalProcess = false,
					RequiresNetworkSocket = false,
					SupportedKinds = new OfficeDocumentKind[]
					{
						OfficeDocumentKind.Spreadsheet
					}
				}
			};
		}

		public bool CanHandle(OfficeDocumentKind kind)
		{
			return kind == OfficeDocumentKind.Spreadsheet;
		}

		public IOfficeDocument OpenRead(string path)
		{
			return new NativeSpreadsheetDocument(path);
		}

		/// <summary>Opens a workbook with the concrete type, for callers that need recalculation.</summary>
		public NativeSpreadsheetDocument OpenSpreadsheet(string path)
		{
			return new NativeSpreadsheetDocument(path);
		}
	}

	public sealed class NativeSpreadsheetDocument : IOfficeDocument
	{
		private readonly List<string> _unknownParts = new List<string>();

		internal NativeSpreadsheetDocument(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				throw new ArgumentNullException(nameof(path));
			}

			SourcePath = path;
			Package = PreservingXlsxPackage.Open(path);
			ReadResult = XlsxWorkbookReader.Read(Package);

			// Derived, not declared: anything the reader did not interpret is unknown.
			for (int index = 0; index < Package.PartNames.Count; index++)
			{
				string name = Package.PartNames[index];
				if (!ReadResult.InterpretedParts.Contains(name))
				{
					_unknownParts.Add(name);
				}
			}
		}

		public PreservingXlsxPackage Package { get; }

		public XlsxReadResult ReadResult { get; }

		public WorkbookModel Workbook
		{
			get { return ReadResult.Model; }
		}

		public OfficeDocumentKind Kind
		{
			get { return OfficeDocumentKind.Spreadsheet; }
		}

		public string SourcePath { get; }

		public IReadOnlyList<string> PartNames
		{
			get { return Package.PartNames; }
		}

		public IReadOnlyCollection<string> UnknownPartNames
		{
			get { return _unknownParts; }
		}

		/// <summary>Recalculates every formula against an injected clock.</summary>
		public RecalcResult Recalculate(DateTime clock)
		{
			return new RecalcEngine(Workbook, clock).RecalculateAll();
		}

		/// <summary>Package part backing a sheet, or null when the sheet is unresolved.</summary>
		public string SheetPartName(string sheetName)
		{
			for (int index = 0; index < ReadResult.SheetParts.Count; index++)
			{
				XlsxSheetPart part = ReadResult.SheetParts[index];
				if (string.Equals(part.SheetName, sheetName, StringComparison.OrdinalIgnoreCase))
				{
					return part.PartName;
				}
			}

			return null;
		}

		/// <summary>
		/// Writes a literal number into one cell. Only that sheet's part is touched.
		/// </summary>
		public void SetNumericCell(string sheetName, CellRef cell, double value)
		{
			string partName = SheetPartName(sheetName);
			if (partName == null)
			{
				throw new InvalidOperationException("Unknown sheet: " + sheetName);
			}

			byte[] updated = XlsxWorkbookWriter.SetNumericCell(
				Package.GetPart(partName),
				cell,
				value);

			Package.ReplacePart(partName, updated);
		}

		public void SaveAs(string path)
		{
			Package.Save(path);
		}

		public void Dispose()
		{
			// Parts are held in memory; the source file handle is released by Open.
		}
	}
}
