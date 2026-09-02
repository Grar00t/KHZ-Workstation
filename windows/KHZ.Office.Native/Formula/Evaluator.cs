using System;
using System.Collections.Generic;

namespace KHZ.Office.Native.Formula
{
	/// <summary>Everything the evaluator needs from the surrounding workbook.</summary>
	public interface IEvaluationContext
	{
		/// <summary>Sheet used to resolve unqualified references.</summary>
		string CurrentSheet { get; }

		/// <summary>
		/// Injected clock. TODAY and NOW read this instead of the system clock so a
		/// recalculation run is reproducible and can be diffed between builds.
		/// </summary>
		DateTime Now { get; }

		FormulaValue GetCellValue(string sheet, CellRef cell);

		bool TryGetDefinedName(string name, out FormulaValue value);
	}

	public sealed class Evaluator
	{
		/// <summary>Largest range the evaluator will materialise, guarding against whole-sheet references.</summary>
		public const long MaxRangeCells = 262144L;

		/// <summary>
		/// Functions evaluated lazily by the evaluator itself. They must not have their
		/// arguments evaluated up front, because an untaken branch is allowed to be an
		/// error.
		/// </summary>
		public static readonly HashSet<string> LazyFunctionNames =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"IF",
				"IFERROR",
				"IFNA",
				"IFS",
				"CHOOSE"
			};

		private readonly IEvaluationContext _context;

		public Evaluator(IEvaluationContext context)
		{
			if (context == null)
			{
				throw new ArgumentNullException(nameof(context));
			}

			_context = context;
		}

		public FormulaValue Evaluate(FormulaNode node)
		{
			if (node == null)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			LiteralNode literal = node as LiteralNode;
			if (literal != null)
			{
				return literal.Value;
			}

			ReferenceNode reference = node as ReferenceNode;
			if (reference != null)
			{
				if (!reference.Cell.IsValid)
				{
					return FormulaValue.FromError(FormulaErrors.Reference);
				}

				return _context.GetCellValue(reference.Sheet ?? _context.CurrentSheet, reference.Cell);
			}

			RangeNode range = node as RangeNode;
			if (range != null)
			{
				return EvaluateRange(range);
			}

			UnaryNode unary = node as UnaryNode;
			if (unary != null)
			{
				return EvaluateUnary(unary);
			}

			BinaryNode binary = node as BinaryNode;
			if (binary != null)
			{
				return EvaluateBinary(binary);
			}

			FunctionNode function = node as FunctionNode;
			if (function != null)
			{
				return EvaluateFunction(function);
			}

			NameNode name = node as NameNode;
			if (name != null)
			{
				FormulaValue resolved;
				if (_context.TryGetDefinedName(name.Name, out resolved))
				{
					return resolved;
				}

				return FormulaValue.FromError(FormulaErrors.Name);
			}

			return FormulaValue.FromError(FormulaErrors.Value);
		}

		private FormulaValue EvaluateRange(RangeNode range)
		{
			if (!range.From.IsValid || !range.To.IsValid)
			{
				return FormulaValue.FromError(FormulaErrors.Reference);
			}

			int rows = range.To.Row - range.From.Row + 1;
			int columns = range.To.Column - range.From.Column + 1;
			if (rows <= 0 || columns <= 0)
			{
				return FormulaValue.FromError(FormulaErrors.Reference);
			}

			if ((long)rows * columns > MaxRangeCells)
			{
				return FormulaValue.FromError(FormulaErrors.Number);
			}

			string sheet = range.Sheet ?? _context.CurrentSheet;
			FormulaValue[,] cells = new FormulaValue[rows, columns];
			for (int row = 0; row < rows; row++)
			{
				for (int column = 0; column < columns; column++)
				{
					cells[row, column] = _context.GetCellValue(
						sheet,
						new CellRef(range.From.Row + row, range.From.Column + column));
				}
			}

			return FormulaValue.FromArray(cells);
		}

		private FormulaValue EvaluateUnary(UnaryNode node)
		{
			FormulaValue operand = Evaluate(node.Operand).Scalar();
			if (operand.IsError)
			{
				return operand;
			}

			double value;
			if (!operand.TryGetNumber(out value))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			switch (node.Operator)
			{
				case "-":
					return FormulaValue.FromNumber(-value);
				case "+":
					return FormulaValue.FromNumber(value);
				case "%":
					return FormulaValue.FromNumber(value / 100d);
				default:
					return FormulaValue.FromError(FormulaErrors.Value);
			}
		}

		private FormulaValue EvaluateBinary(BinaryNode node)
		{
			FormulaValue left = Evaluate(node.Left).Scalar();
			FormulaValue right = Evaluate(node.Right).Scalar();

			if (left.IsError)
			{
				return left;
			}

			if (right.IsError)
			{
				return right;
			}

			if (node.Operator == "&")
			{
				return FormulaValue.FromText(left.ToDisplayText() + right.ToDisplayText());
			}

			switch (node.Operator)
			{
				case "=":
					return FormulaValue.FromLogical(CompareValues(left, right) == 0);
				case "<>":
					return FormulaValue.FromLogical(CompareValues(left, right) != 0);
				case "<":
					return FormulaValue.FromLogical(CompareValues(left, right) < 0);
				case ">":
					return FormulaValue.FromLogical(CompareValues(left, right) > 0);
				case "<=":
					return FormulaValue.FromLogical(CompareValues(left, right) <= 0);
				case ">=":
					return FormulaValue.FromLogical(CompareValues(left, right) >= 0);
			}

			double a;
			double b;
			if (!left.TryGetNumber(out a) || !right.TryGetNumber(out b))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			switch (node.Operator)
			{
				case "+":
					return FormulaValue.FromNumber(a + b);
				case "-":
					return FormulaValue.FromNumber(a - b);
				case "*":
					return FormulaValue.FromNumber(a * b);
				case "/":
					if (b == 0d)
					{
						return FormulaValue.FromError(FormulaErrors.Div0);
					}

					return FormulaValue.FromNumber(a / b);
				case "^":
				{
					double result = Math.Pow(a, b);
					if (double.IsNaN(result) || double.IsInfinity(result))
					{
						return FormulaValue.FromError(FormulaErrors.Number);
					}

					return FormulaValue.FromNumber(result);
				}

				default:
					return FormulaValue.FromError(FormulaErrors.Value);
			}
		}

		/// <summary>
		/// Excel comparison ordering: numbers sort before text, text before logicals.
		/// Text comparison is case-insensitive. A blank is normalised to the zero value
		/// of whatever type it is compared against.
		/// </summary>
		public static int CompareValues(FormulaValue left, FormulaValue right)
		{
			FormulaValue a = NormalizeBlank(left.Scalar(), right.Scalar());
			FormulaValue b = NormalizeBlank(right.Scalar(), left.Scalar());

			int rankA = TypeRank(a);
			int rankB = TypeRank(b);
			if (rankA != rankB)
			{
				return rankA < rankB ? -1 : 1;
			}

			switch (rankA)
			{
				case 0:
					return a.NumberValue.CompareTo(b.NumberValue);
				case 1:
					return string.Compare(
						a.TextValue ?? string.Empty,
						b.TextValue ?? string.Empty,
						StringComparison.OrdinalIgnoreCase);
				default:
				{
					int left1 = a.LogicalValue ? 1 : 0;
					int right1 = b.LogicalValue ? 1 : 0;
					return left1.CompareTo(right1);
				}
			}
		}

		private static FormulaValue NormalizeBlank(FormulaValue value, FormulaValue other)
		{
			if (!value.IsBlank)
			{
				return value;
			}

			switch (other.Kind)
			{
				case FormulaValueKind.Text:
					return FormulaValue.FromText(string.Empty);
				case FormulaValueKind.Logical:
					return FormulaValue.FromLogical(false);
				default:
					return FormulaValue.FromNumber(0d);
			}
		}

		private static int TypeRank(FormulaValue value)
		{
			if (value.Kind == FormulaValueKind.Text)
			{
				return 1;
			}

			if (value.Kind == FormulaValueKind.Logical)
			{
				return 2;
			}

			return 0;
		}

		private FormulaValue EvaluateFunction(FunctionNode node)
		{
			switch (node.Name)
			{
				case "IF":
				{
					if (node.Arguments.Count < 2)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					FormulaValue condition = Evaluate(node.Arguments[0]).Scalar();
					if (condition.IsError)
					{
						return condition;
					}

					double flag;
					if (!condition.TryGetNumber(out flag))
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					if (flag != 0d)
					{
						return Evaluate(node.Arguments[1]).Scalar();
					}

					if (node.Arguments.Count >= 3)
					{
						return Evaluate(node.Arguments[2]).Scalar();
					}

					return FormulaValue.FromLogical(false);
				}

				case "IFERROR":
				{
					if (node.Arguments.Count < 2)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					FormulaValue candidate = Evaluate(node.Arguments[0]).Scalar();
					if (candidate.IsError)
					{
						return Evaluate(node.Arguments[1]).Scalar();
					}

					return candidate;
				}

				case "IFNA":
				{
					if (node.Arguments.Count < 2)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					FormulaValue candidate = Evaluate(node.Arguments[0]).Scalar();
					if (candidate.IsError && candidate.ErrorCode == FormulaErrors.NotAvailable)
					{
						return Evaluate(node.Arguments[1]).Scalar();
					}

					return candidate;
				}

				case "IFS":
				{
					for (int index = 0; index + 1 < node.Arguments.Count; index += 2)
					{
						FormulaValue condition = Evaluate(node.Arguments[index]).Scalar();
						if (condition.IsError)
						{
							return condition;
						}

						double flag;
						if (condition.TryGetNumber(out flag) && flag != 0d)
						{
							return Evaluate(node.Arguments[index + 1]).Scalar();
						}
					}

					return FormulaValue.FromError(FormulaErrors.NotAvailable);
				}

				case "CHOOSE":
				{
					if (node.Arguments.Count < 2)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					FormulaValue selector = Evaluate(node.Arguments[0]).Scalar();
					if (selector.IsError)
					{
						return selector;
					}

					double raw;
					if (!selector.TryGetNumber(out raw))
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					int chosen = (int)Math.Truncate(raw);
					if (chosen < 1 || chosen >= node.Arguments.Count)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}

					return Evaluate(node.Arguments[chosen]).Scalar();
				}
			}

			List<FormulaValue> arguments = new List<FormulaValue>(node.Arguments.Count);
			for (int index = 0; index < node.Arguments.Count; index++)
			{
				arguments.Add(Evaluate(node.Arguments[index]));
			}

			return FunctionLibrary.Invoke(node.Name, arguments, _context);
		}
	}
}
