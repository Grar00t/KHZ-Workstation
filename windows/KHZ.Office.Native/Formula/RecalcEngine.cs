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
	/// Evaluation order comes from a dependency graph resolved with Kahn's algorithm,
	/// not from sheet or cell order. Any cell that cannot be ordered is part of a
	/// cycle and is reported as such instead of being assigned a value.
	/// </para>
	/// </summary>
	public sealed class RecalcEngine : IEvaluationContext
	{
		private const int MaxSamples = 40;

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

						if (result.ParseFailures.Count < MaxSamples)
						{
							result.ParseFailures.Add(
								sheet.Name + "!" + cell.Reference.ToA1() +
								" [" + cell.FormulaText + "] " + exception.Message);
						}
					}
				}
			}

			// 3. Build edges. Only formula cells are graph nodes: a dependency on a
			//    constant or an empty cell needs no ordering.
			Dictionary<CellKey, List<CellKey>> dependents = new Dictionary<CellKey, List<CellKey>>();
			Dictionary<CellKey, int> indegree = new Dictionary<CellKey, int>();

			for (int index = 0; index < nodes.Count; index++)
			{
				indegree[nodes[index]] = 0;
			}

			List<CellKey> buffer = new List<CellKey>();
			for (int index = 0; index < nodes.Count; index++)
			{
				CellKey key = nodes[index];
				WorkbookCell cell = cellsByKey[key];
				if (cell.Ast == null)
				{
					continue;
				}

				buffer.Clear();
				CollectDependencies(cell.Ast, key.Sheet, buffer);

				HashSet<CellKey> seen = new HashSet<CellKey>();
				for (int position = 0; position < buffer.Count; position++)
				{
					CellKey dependency = buffer[position];
					if (!cellsByKey.ContainsKey(dependency))
					{
						continue;
					}

					if (dependency.Equals(key) || !seen.Add(dependency))
					{
						continue;
					}

					List<CellKey> list;
					if (!dependents.TryGetValue(dependency, out list))
					{
						list = new List<CellKey>();
						dependents[dependency] = list;
					}

					list.Add(key);
					indegree[key] = indegree[key] + 1;
				}
			}

			// 4. Topological order.
			Queue<CellKey> ready = new Queue<CellKey>();
			for (int index = 0; index < nodes.Count; index++)
			{
				if (indegree[nodes[index]] == 0)
				{
					ready.Enqueue(nodes[index]);
				}
			}

			List<CellKey> order = new List<CellKey>(nodes.Count);
			while (ready.Count > 0)
			{
				CellKey key = ready.Dequeue();
				order.Add(key);

				List<CellKey> list;
				if (!dependents.TryGetValue(key, out list))
				{
					continue;
				}

				for (int position = 0; position < list.Count; position++)
				{
					CellKey dependent = list[position];
					indegree[dependent] = indegree[dependent] - 1;
					if (indegree[dependent] == 0)
					{
						ready.Enqueue(dependent);
					}
				}
			}

			// 5. Evaluate in dependency order.
			Evaluator evaluator = new Evaluator(this);
			for (int index = 0; index < order.Count; index++)
			{
				CellKey key = order[index];
				WorkbookCell cell = cellsByKey[key];
				if (cell.Ast == null)
				{
					continue;
				}

				_currentSheet = key.Sheet;
				try
				{
					cell.ComputedValue = evaluator.Evaluate(cell.Ast).Scalar();
				}
				catch (Exception)
				{
					cell.ComputedValue = FormulaValue.FromError(FormulaErrors.Value);
				}

				result.EvaluatedCount++;
			}

			// 6. Anything left unordered is in a cycle. Report it; never invent a value.
			if (order.Count < nodes.Count)
			{
				HashSet<CellKey> ordered = new HashSet<CellKey>(order);
				for (int index = 0; index < nodes.Count; index++)
				{
					CellKey key = nodes[index];
					if (ordered.Contains(key))
					{
						continue;
					}

					WorkbookCell cell = cellsByKey[key];
					if (cell.Ast == null)
					{
						continue;
					}

					cell.ComputedValue = FormulaValue.FromError(FormulaErrors.Cycle);
					result.CycleCount++;

					if (result.Cycles.Count < MaxSamples)
					{
						result.Cycles.Add(key.Sheet + "!" + key.Cell.ToA1());
					}
				}
			}

			return result;
		}

		/// <summary>
		/// Walks a formula tree and appends every cell it reads. Ranges are expanded,
		/// but a range larger than the evaluator's own limit is skipped rather than
		/// materialised, so a stray whole-column reference cannot exhaust memory here.
		/// </summary>
		private static void CollectDependencies(
			FormulaNode node,
			string currentSheet,
			List<CellKey> buffer)
		{
			if (node == null)
			{
				return;
			}

			ReferenceNode reference = node as ReferenceNode;
			if (reference != null)
			{
				if (reference.Cell.IsValid)
				{
					buffer.Add(new CellKey(reference.Sheet ?? currentSheet, reference.Cell));
				}

				return;
			}

			RangeNode range = node as RangeNode;
			if (range != null)
			{
				if (!range.From.IsValid || !range.To.IsValid)
				{
					return;
				}

				int rows = range.To.Row - range.From.Row + 1;
				int columns = range.To.Column - range.From.Column + 1;
				if (rows <= 0 || columns <= 0)
				{
					return;
				}

				if ((long)rows * columns > Evaluator.MaxRangeCells)
				{
					return;
				}

				string sheet = range.Sheet ?? currentSheet;
				for (int row = 0; row < rows; row++)
				{
					for (int column = 0; column < columns; column++)
					{
						buffer.Add(new CellKey(
							sheet,
							new CellRef(range.From.Row + row, range.From.Column + column)));
					}
				}

				return;
			}

			UnaryNode unary = node as UnaryNode;
			if (unary != null)
			{
				CollectDependencies(unary.Operand, currentSheet, buffer);
				return;
			}

			BinaryNode binary = node as BinaryNode;
			if (binary != null)
			{
				CollectDependencies(binary.Left, currentSheet, buffer);
				CollectDependencies(binary.Right, currentSheet, buffer);
				return;
			}

			FunctionNode function = node as FunctionNode;
			if (function != null)
			{
				for (int index = 0; index < function.Arguments.Count; index++)
				{
					CollectDependencies(function.Arguments[index], currentSheet, buffer);
				}
			}
		}
	}
}
