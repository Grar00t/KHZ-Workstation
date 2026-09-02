using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using KHZ.Office.Native;
using KHZ.Office.Native.Formula;
using KHZ.Office.Native.Xlsx;

namespace KHZ.Office.Spike
{
	public static class Program
	{
		/// <summary>
		/// Fixed clock. TODAY and NOW resolve against this, so two runs of the same
		/// corpus produce identical reports and the JSON can be diffed across builds.
		/// </summary>
		private static readonly DateTime DeterministicClock =
			new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

		private const string DefaultWorkbook = "acceptance/corpus/FormulaCompatibility.xlsx";
		private const string DefaultReport = "acceptance/reports/native-formula-spike.json";
		private const int MaxSamples = 40;

		public static int Main(string[] args)
		{
			string workbookPath = args.Length > 0 ? args[0] : DefaultWorkbook;
			string reportPath = args.Length > 1 ? args[1] : DefaultReport;

			if (!File.Exists(workbookPath))
			{
				Console.Error.WriteLine("Workbook not found: " + Path.GetFullPath(workbookPath));
				Console.Error.WriteLine("Usage: KHZ.Office.Spike <workbook.xlsx> [report.json]");
				return 2;
			}

			string scratch = Path.Combine(
				Path.GetTempPath(),
				"khz-office-spike-" + Guid.NewGuid().ToString("N"));

			Directory.CreateDirectory(scratch);

			try
			{
				return Run(workbookPath, reportPath, scratch);
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine("Spike failed: " + exception.Message);
				Console.Error.WriteLine(exception.StackTrace);
				return 1;
			}
			finally
			{
				try
				{
					Directory.Delete(scratch, true);
				}
				catch (IOException)
				{
					// Scratch cleanup is best effort.
				}
			}
		}

		private static int Run(string workbookPath, string reportPath, string scratch)
		{
			OpenXmlSpreadsheetEngine engine = new OpenXmlSpreadsheetEngine();
			OfficeEngineDescriptor descriptor = engine.Describe();

			Console.WriteLine("engine   : " + descriptor.DisplayName + " " + descriptor.Version);
			Console.WriteLine("in-proc  : " + descriptor.InProcess.ToString());
			Console.WriteLine("socket   : " + descriptor.Capabilities.RequiresNetworkSocket.ToString());
			Console.WriteLine("workbook : " + Path.GetFullPath(workbookPath));

			// ---- 1. Recalculate and compare against the file's own cached values ----
			NativeSpreadsheetDocument document = engine.OpenSpreadsheet(workbookPath);
			RecalcResult recalc = document.Recalculate(DeterministicClock);

			int compared = 0;
			int matched = 0;
			int noBaseline = 0;
			List<object> mismatches = new List<object>();
			HashSet<string> functionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (int index = 0; index < document.Workbook.Sheets.Count; index++)
			{
				WorkbookSheet sheet = document.Workbook.Sheets[index];
				foreach (KeyValuePair<CellRef, WorkbookCell> pair in sheet.Cells)
				{
					WorkbookCell cell = pair.Value;
					if (!cell.HasFormula)
					{
						continue;
					}

					FormulaInspector.CollectFunctionNames(cell.Ast, functionNames);

					// A blank cached value is no baseline at all. Counting it as agreement
					// would inflate the result, so it is excluded and reported separately.
					if (cell.CachedValue == null || cell.CachedValue.IsBlank)
					{
						noBaseline++;
						continue;
					}

					string expected = cell.CachedValue.ToDisplayText();
					string actual = cell.ComputedValue != null
						? cell.ComputedValue.ToDisplayText()
						: "<not evaluated>";

					compared++;
					if (string.Equals(expected, actual, StringComparison.Ordinal))
					{
						matched++;
						continue;
					}

					if (mismatches.Count < MaxSamples)
					{
						mismatches.Add(new
						{
							cell = sheet.Name + "!" + cell.Reference.ToA1(),
							formula = cell.FormulaText,
							expected = expected,
							actual = actual
						});
					}
				}
			}

			List<string> unsupported = new List<string>();
			foreach (string name in functionNames)
			{
				if (!FunctionLibrary.IsSupported(name))
				{
					unsupported.Add(name);
				}
			}

			unsupported.Sort(StringComparer.OrdinalIgnoreCase);

			double matchRate = compared > 0 ? (double)matched / compared : 0d;

			Console.WriteLine();
			Console.WriteLine("formulas : " + recalc.FormulaCellCount.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("evaluated: " + recalc.EvaluatedCount.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("parse err: " + recalc.ParseFailureCount.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("cycles   : " + recalc.CycleCount.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine("shared   : " + recalc.SharedFormulaFollowerCount.ToString(CultureInfo.InvariantCulture));
			Console.WriteLine(
				"agreement: " + matched.ToString(CultureInfo.InvariantCulture) +
				"/" + compared.ToString(CultureInfo.InvariantCulture) +
				" (" + (matchRate * 100d).ToString("0.00", CultureInfo.InvariantCulture) + "%)");

			// ---- 2. No-op round trip: every part must survive byte-identical ----
			string roundTripPath = Path.Combine(scratch, "roundtrip.xlsx");
			document.SaveAs(roundTripPath);

			List<string> changedParts = new List<string>();
			int identicalParts = ComparePackages(document.Package, roundTripPath, null, changedParts);
			bool roundTripPassed = changedParts.Count == 0;

			Console.WriteLine();
			Console.WriteLine(
				"roundtrip: " + (roundTripPassed ? "PASS" : "FAIL") +
				" (" + identicalParts.ToString(CultureInfo.InvariantCulture) +
				"/" + document.Package.PartCount.ToString(CultureInfo.InvariantCulture) +
				" parts identical)");

			// ---- 3. Single-cell mutation: unrelated parts must not move ----
			NativeSpreadsheetDocument mutable = engine.OpenSpreadsheet(workbookPath);
			string mutationCell = null;
			string mutationSheet = null;
			string mutationPart = null;
			bool unrelatedIdentical = false;
			List<string> mutationChanged = new List<string>();

			if (mutable.Workbook.Sheets.Count > 0)
			{
				WorkbookSheet target = mutable.Workbook.Sheets[0];
				mutationSheet = target.Name;
				mutationPart = mutable.SheetPartName(target.Name);

				// Write below the used range so no existing formula input is disturbed.
				int lastRow = 1;
				foreach (KeyValuePair<CellRef, WorkbookCell> pair in target.Cells)
				{
					if (pair.Key.Row > lastRow)
					{
						lastRow = pair.Key.Row;
					}
				}

				CellRef probe = new CellRef(lastRow + 5, 1);
				mutationCell = target.Name + "!" + probe.ToA1();
				mutable.SetNumericCell(target.Name, probe, 424242d);

				string mutatedPath = Path.Combine(scratch, "mutated.xlsx");
				mutable.SaveAs(mutatedPath);

				ComparePackages(mutable.Package, mutatedPath, mutationPart, mutationChanged);
				unrelatedIdentical = mutationChanged.Count == 0;

				Console.WriteLine(
					"mutation : " + (unrelatedIdentical ? "PASS" : "FAIL") +
					" (wrote " + mutationCell + ", " +
					mutable.Package.ModifiedParts.Count.ToString(CultureInfo.InvariantCulture) +
					" part(s) modified)");
			}

			// ---- Report ----
			List<string> modifiedParts = new List<string>(mutable.Package.ModifiedParts);
			modifiedParts.Sort(StringComparer.Ordinal);

			List<string> unknownParts = new List<string>(document.UnknownPartNames);
			unknownParts.Sort(StringComparer.Ordinal);

			object report = new
			{
				spike = "native-formula-engine",
				generatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
				deterministicClockUtc = DeterministicClock.ToString("o", CultureInfo.InvariantCulture),
				workbook = new
				{
					path = workbookPath.Replace('\\\\', '/'),
					sha256 = PreservingXlsxPackage.Sha256File(workbookPath),
					partCount = document.Package.PartCount,
					sheetCount = document.Workbook.Sheets.Count,
					cellCount = document.Workbook.TotalCellCount,
					unknownPartCount = unknownParts.Count,
					unknownParts = unknownParts
				},
				engine = new
				{
					id = descriptor.Id,
					displayName = descriptor.DisplayName,
					version = descriptor.Version,
					inProcess = descriptor.InProcess,
					requiresExternalProcess = descriptor.Capabilities.RequiresExternalProcess,
					requiresNetworkSocket = descriptor.Capabilities.RequiresNetworkSocket,
					preservesUnknownParts = descriptor.Capabilities.PreservesUnknownParts,
					canRender = descriptor.Capabilities.CanRender,
					canExportPdf = descriptor.Capabilities.CanExportPdf,
					supportedFunctionCount = FunctionLibrary.SupportedFunctionCount
				},
				formulas = new
				{
					total = recalc.FormulaCellCount,
					evaluated = recalc.EvaluatedCount,
					parseFailures = recalc.ParseFailureCount,
					cycles = recalc.CycleCount,
					sharedFormulaFollowers = recalc.SharedFormulaFollowerCount,
					compared = compared,
					matched = matched,
					mismatched = compared - matched,
					excludedNoBaseline = noBaseline,
					distinctFunctions = functionNames.Count,
					unsupportedFunctions = unsupported
				},
				matchRate = Math.Round(matchRate, 6),
				roundTrip = new
				{
					identicalParts = identicalParts,
					totalParts = document.Package.PartCount,
					changedParts = changedParts,
					verdict = roundTripPassed ? "PASS" : "FAIL"
				},
				mutation = new
				{
					sheet = mutationSheet,
					target = mutationCell,
					targetPart = mutationPart,
					modifiedParts = modifiedParts,
					unexpectedlyChangedParts = mutationChanged,
					unrelatedPartsIdentical = unrelatedIdentical,
					verdict = unrelatedIdentical ? "PASS" : "FAIL"
				},
				mismatchSamples = mismatches,
				parseFailureSamples = recalc.ParseFailures,
				cycleSamples = recalc.Cycles,
				readerWarnings = document.ReadResult.Warnings
			};

			string json = JsonSerializer.Serialize(
				report,
				new JsonSerializerOptions { WriteIndented = true });

			string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath));
			if (!string.IsNullOrEmpty(reportDirectory) && !Directory.Exists(reportDirectory))
			{
				Directory.CreateDirectory(reportDirectory);
			}

			File.WriteAllText(reportPath, json);

			Console.WriteLine();
			Console.WriteLine("report   : " + Path.GetFullPath(reportPath));

			if (unsupported.Count > 0)
			{
				Console.WriteLine("unsupported functions in corpus: " + string.Join(", ", unsupported));
			}

			// Round-trip fidelity is the load-bearing claim, so it alone gates the exit
			// code. The match rate is a measurement to be read, not a pass/fail gate.
			return roundTripPassed && unrelatedIdentical ? 0 : 3;
		}

		/// <summary>
		/// Compares a saved package against the in-memory original, part by part.
		/// Returns the identical-part count and fills <paramref name="changed"/> with
		/// every part that differs, ignoring one expected part when given.
		/// </summary>
		private static int ComparePackages(
			PreservingXlsxPackage original,
			string savedPath,
			string ignoredPart,
			List<string> changed)
		{
			PreservingXlsxPackage saved = PreservingXlsxPackage.Open(savedPath);
			int identical = 0;

			for (int index = 0; index < original.PartNames.Count; index++)
			{
				string name = original.PartNames[index];
				if (ignoredPart != null && string.Equals(name, ignoredPart, StringComparison.Ordinal))
				{
					continue;
				}

				string before;
				if (!original.OriginalHashes.TryGetValue(name, out before))
				{
					changed.Add(name + " (no original hash)");
					continue;
				}

				string after;
				if (!saved.OriginalHashes.TryGetValue(name, out after))
				{
					changed.Add(name + " (missing after save)");
					continue;
				}

				if (string.Equals(before, after, StringComparison.Ordinal))
				{
					identical++;
					continue;
				}

				changed.Add(name);
			}

			if (saved.PartCount != original.PartCount)
			{
				changed.Add(
					"<part count> " + original.PartCount.ToString(CultureInfo.InvariantCulture) +
					" -> " + saved.PartCount.ToString(CultureInfo.InvariantCulture));
			}

			return identical;
		}
	}
}
