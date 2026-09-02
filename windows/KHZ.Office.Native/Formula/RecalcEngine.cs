using System;
using System.Collections.Generic;

namespace KHZ.Office.Native.Formula
{
	internal readonly struct CellKey : IEquatable<CellKey>
	{
		public CellKey(string sheet, CellRef cell)
		{
			Sheet = sheet ?? string.Empty;
			Cell = cell;
		}

		public string Sheet { get; }

		public CellRef Cell { get; }

		public bool Equals(CellKey other)
		{
			return string.Equals(Sheet, other.Sheet, StringComparison.OrdinalIgnoreCase) &&
				Cell.Equals(other.Cell);
		}

		public override bool Equals(object obj)
		{
			return obj is CellKey && Equals((CellKey)obj);
		}

		public override int GetHashCode()
		{
			int sheetHash = StringComparer.OrdinalIgnoreCase.GetHashCode(Sheet);
			return (sheetHash * 397) ^ Cell.GetHashCode();
		}

		public override string ToString()
		{
			return Sheet + "!" + Cell.ToA1();
		}
	}

	public sealed class RecalcResult
	{
		public int FormulaCellCount { get; set; }

		public int EvaluatedCount { get; set; }

		public int CycleCount { get; set; }

		public int ParseFailureCount { get; set; }

		public int SharedFormulaFollowerCount { get; set; }

		public List<string> ParseFailures { get; } = new List<string>();

		public List<string> Cycles { get; } = new List<string>();
	}

	/// <summary>
	/// Orders and evaluates every formula in a workbook.
	/// <para>
	/// Evaluation order comes from a real dependency graph resolved with Kahn's
	/// algorithm, not from sheet or cell order. Any cell that cannot be ordered is
	/// part of a cycle and is reported as such instead of being assigned a value.
	/// </para>
	/// </summary>
	public sealed class RecalcEngine : IEvaluationContext
	{
		private readonly WorkbookModel _workbook;
		private readonly DateTime _clock;
		private readonly Dictionary<string, WorkbookSheet> _sheetsByName;
		private string _currentSheet;

		public RecalcEngine(WorkbookModel workbook, DateTime clock)
		{
			if (workbook == null)
			{
				throw new ArgumentNullException(nameof(workbook));
			}

			_workbook = workbook;
			_clock = clock;
			_sheetsByName = new Dictionary<string, WorkbookSheet>(StringComparer.OrdinalIgnoreCase);

			for (int index = 0; index < workbook.Sheets.Count; index++)
			{
				_sheetsByName[workbook.Sheets[index].Name] = workbook.Sheets[index];
			}

			_currentSheet = workbook.Sheets.Count > 0 ? workbook.Sheets[0].Name : string.Empty;
		}

		public string CurrentSheet
		{
			get { return _currentSheet; }
		}

		public DateTime Now
		{
			get { return _clock; }
		}

		public FormulaValue GetCellValue(string sheet, CellRef cell)
		{
			WorkbookSheet target;
			if (!_sheetsByName.TryGetValue(sheet ?? string.Empty, out target))
			{
				return FormulaValue.FromError(FormulaErrors.Reference);
			}

			WorkbookCell resolved;
			if (!target.Cells.TryGetValue(cell, out resolved))
			{
				return FormulaValue.BlankValue;
			}

			return resolved.ComputedValue ?? resolved.CachedValue ?? FormulaValue.BlankValue;
		}

		public bool TryGetDefinedName(string name, out FormulaValue value)
		{
			return _workbook.DefinedNames.TryGetValue(name ?? string.Empty, out value);
		}

		public RecalcResult RecalculateAll()
		{
			RecalcResult result = new RecalcResult();

			// 1. Constants seed the graph with the value the file already holds.
			for (int index = 0; index < _workbook.Sheets.Count; index++)
			{
				foreach (KeyValuePair<CellRef, WorkbookCell> pair in _workbook.Sheets[index].Cells)
				{
					WorkbookCell cell = pair.Value;
					cell.ComputedValue = cell.HasFormula ? null : cell.CachedValue;
				}
			}

			// 2. Parse every formula.
			List<CellKey> nodes = new List<CellKey>();
			Dictionary<CellKey, WorkbookCell> cellsByKey = new Dictionary<CellKey, WorkbookCell>();

			for (int index = 0; index < _workbook.Sheets.Count; index++)
			{
				WorkbookSheet sheet = _workbook.Sheets[index];
				foreach (KeyValuePair<CellRef, WorkbookCell> pair in sheet.Cells)
				{
					WorkbookCell cell = pair.Value;

					if (cell.IsSharedFormulaFollower && !cell.HasFormula)
					{
						result.SharedFormulaFollowerCount++;
						continue;
					}

					if (!cell.HasFormula)
					{
						continue;
					}

					result.FormulaCellCount++;
					CellKey key = new CellKey(sheet.Name, cell.Reference);
					cellsByKey[key] = cell;
					nodes.Add(key);

					try
					{
						cell.Ast = Parser.Parse(cell.FormulaText);
						cell.ParseError = null;
					}
					catch (FormulaParseException exception)
					{
						cell.Ast = null;
						cell.ParseError = exception.Message;
						cell.ComputedValue = FormulaValue.FromError(FormulaErrors.Name);
						result.ParseFailureCount++;

						if (result.ParseFailures.Count < 40)
						{
							result.ParseFailures.Add(
								sheet.Name + "!" + cell.Reference.ToA1() +
								" [" + cell.FormulaText + "] " + exception.Message);
						}
					}
				}
			}

			// 3. Build edges. Only formula cells are graph nodes; a dependency on a
			//    constant or an empty cell needs no ordering.
			Dictionary<CellKey, List<CellKey>> dependents = new Dictionary<CellKey, List<CellKey>>();
			Dictionary<CellKey, int> indegree = new Dictionary<CellKey, int>();

			for (int index = 0; index < nodes.Count; index++)
			{
				indegree[nodes[index]] = 0;
	