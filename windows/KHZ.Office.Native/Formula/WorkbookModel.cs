using System;
using System.Collections.Generic;

namespace KHZ.Office.Native.Formula
{
	/// <summary>A single cell: what the file stored, and what this engine computed.</summary>
	public sealed class WorkbookCell
	{
		public CellRef Reference { get; set; }

		/// <summary>Formula source without the leading '='. Null for a constant cell.</summary>
		public string FormulaText { get; set; }

		/// <summary>
		/// True when the cell carries a shared-formula reference whose master text is
		/// held by another cell. These are reported as unresolved rather than guessed.
		/// </summary>
		public bool IsSharedFormulaFollower { get; set; }

		/// <summary>The value already present in the file, used as the comparison baseline.</summary>
		public FormulaValue CachedValue { get; set; } = FormulaValue.BlankValue;

		/// <summary>The value this engine produced.</summary>
		public FormulaValue ComputedValue { get; set; }

		public FormulaNode Ast { get; set; }

		public string ParseError { get; set; }

		public bool HasFormula
		{
			get { return !string.IsNullOrEmpty(FormulaText); }
		}
	}

	public sealed class WorkbookSheet
	{
		public WorkbookSheet(string name)
		{
			Name = name ?? string.Empty;
		}

		public string Name { get; }

		public Dictionary<CellRef, WorkbookCell> Cells { get; } =
			new Dictionary<CellRef, WorkbookCell>();

		public WorkbookCell GetOrCreate(CellRef reference)
		{
			WorkbookCell cell;
			if (!Cells.TryGetValue(reference, out cell))
			{
				cell = new WorkbookCell { Reference = reference };
				Cells[reference] = cell;
			}

			return cell;
		}
	}

	public sealed class WorkbookModel
	{
		public List<WorkbookSheet> Sheets { get; } = new List<WorkbookSheet>();

		public Dictionary<string, FormulaValue> DefinedNames { get; } =
			new Dictionary<string, FormulaValue>(StringComparer.OrdinalIgnoreCase);

		public WorkbookSheet FindSheet(string name)
		{
			for (int index = 0; index < Sheets.Count; index++)
			{
				if (string.Equals(Sheets[index].Name, name, StringComparison.OrdinalIgnoreCase))
				{
					return Sheets[index];
				}
			}

			return null;
		}

		public int TotalCellCount
		{
			get
			{
				int total = 0;
				for (int index = 0; index < Sheets.Count; index++)
				{
					total += Sheets[index].Cells.Count;
				}

				return total;
			}
		}
	}
}
