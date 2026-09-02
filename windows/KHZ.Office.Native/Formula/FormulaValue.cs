using System;
using System.Collections.Generic;
using System.Globalization;

namespace KHZ.Office.Native.Formula
{
	public enum FormulaValueKind
	{
		Blank = 0,
		Number = 1,
		Text = 2,
		Logical = 3,
		Error = 4,
		Array = 5
	}

	public static class FormulaErrors
	{
		public const string Div0 = "#DIV/0!";
		public const string Value = "#VALUE!";
		public const string Reference = "#REF!";
		public const string Name = "#NAME?";
		public const string NotAvailable = "#N/A";
		public const string Number = "#NUM!";
		public const string Null = "#NULL!";

		/// <summary>
		/// Not an Excel error code. Emitted when a circular reference is detected so
		/// a cycle is never silently reported as a computed value.
		/// </summary>
		public const string Cycle = "#CYCLE!";

		public static bool IsErrorLiteral(string text)
		{
			switch (text)
			{
				case Div0:
				case Value:
				case Reference:
				case Name:
				case NotAvailable:
				case Number:
				case Null:
				case Cycle:
					return true;
				default:
					return false;
			}
		}
	}

	/// <summary>An immutable spreadsheet value.</summary>
	public sealed class FormulaValue
	{
		private readonly double _number;
		private readonly string _text;
		private readonly bool _logical;
		private readonly string _error;
		private readonly FormulaValue[,] _array;

		private FormulaValue(
			FormulaValueKind kind,
			double number,
			string text,
			bool logical,
			string error,
			FormulaValue[,] array)
		{
			Kind = kind;
			_number = number;
			_text = text;
			_logical = logical;
			_error = error;
			_array = array;
		}

		public FormulaValueKind Kind { get; }

		public static readonly FormulaValue BlankValue =
			new FormulaValue(FormulaValueKind.Blank, 0d, null, false, null, null);

		public static readonly FormulaValue TrueValue =
			new FormulaValue(FormulaValueKind.Logical, 0d, null, true, null, null);

		public static readonly FormulaValue FalseValue =
			new FormulaValue(FormulaValueKind.Logical, 0d, null, false, null, null);

		public static FormulaValue FromNumber(double value)
		{
			return new FormulaValue(FormulaValueKind.Number, value, null, false, null, null);
		}

		public static FormulaValue FromText(string value)
		{
			return new FormulaValue(FormulaValueKind.Text, 0d, value ?? string.Empty, false, null, null);
		}

		public static FormulaValue FromLogical(bool value)
		{
			return value ? TrueValue : FalseValue;
		}

		public static FormulaValue FromError(string code)
		{
			return new FormulaValue(FormulaValueKind.Error, 0d, null, false, code ?? FormulaErrors.Value, null);
		}

		public static FormulaValue FromArray(FormulaValue[,] cells)
		{
			if (cells == null)
			{
				return FromError(FormulaErrors.Reference);
			}

			return new FormulaValue(FormulaValueKind.Array, 0d, null, false, null, cells);
		}

		public double NumberValue { get { return _number; } }

		public string TextValue { get { return _text; } }

		public bool LogicalValue { get { return _logical; } }

		public string ErrorCode { get { return _error; } }

		public FormulaValue[,] ArrayValue { get { return _array; } }

		public bool IsError { get { return Kind == FormulaValueKind.Error; } }

		public bool IsBlank { get { return Kind == FormulaValueKind.Blank; } }

		public bool IsArray { get { return Kind == FormulaValueKind.Array; } }

		/// <summary>Collapses an array to its top-left value, as Excel does in scalar context.</summary>
		public FormulaValue Scalar()
		{
			if (Kind != FormulaValueKind.Array)
			{
				return this;
			}

			if (_array.GetLength(0) == 0 || _array.GetLength(1) == 0)
			{
				return FromError(FormulaErrors.Value);
			}

			return _array[0, 0] ?? BlankValue;
		}

		public bool TryGetNumber(out double result)
		{
			FormulaValue value = Scalar();
			switch (value.Kind)
			{
				case FormulaValueKind.Number:
					result = value._number;
					return true;
				case FormulaValueKind.Logical:
					result = value._logical ? 1d : 0d;
					return true;
				case FormulaValueKind.Blank:
					result = 0d;
					return true;
				case FormulaValueKind.Text:
					return double.TryParse(
						value._text,
						NumberStyles.Any,
						CultureInfo.InvariantCulture,
						out result);
				default:
					result = 0d;
					return false;
			}
		}

		public string ToDisplayText()
		{
			switch (Kind)
			{
				case FormulaValueKind.Blank:
					return string.Empty;
				case FormulaValueKind.Number:
					return FormatNumber(_number);
				case FormulaValueKind.Text:
					return _text;
				case FormulaValueKind.Logical:
					return _logical ? "TRUE" : "FALSE";
				case FormulaValueKind.Error:
					return _error;
				default:
					return Scalar().ToDisplayText();
			}
		}

		/// <summary>
		/// Canonical numeric text used when comparing this engine's result against the
		/// value cached in the file. Rounds to 10 decimal places so that IEEE-754
		/// noise is not reported as a compatibility failure.
		/// </summary>
		public static string FormatNumber(double value)
		{
			if (double.IsNaN(value) || double.IsInfinity(value))
			{
				return FormulaErrors.Number;
			}

			double rounded = Math.Round(value, 10, MidpointRounding.AwayFromZero);
			if (rounded == 0d)
			{
				rounded = 0d;
			}

			return rounded.ToString("0.##########", CultureInfo.InvariantCulture);
		}

		/// <summary>Yields this value, or every element when it is an array.</summary>
		public IEnumerable<FormulaValue> Enumerate()
		{
			if (Kind != FormulaValueKind.Array)
			{
				yield return this;
				yield break;
			}

			int rows = _array.GetLength(0);
			int columns = _array.GetLength(1);
			for (int row = 0; row < rows; row++)
			{
				for (int column = 0; column < columns; column++)
				{
					yield return _array[row, column] ?? BlankValue;
				}
			}
		}

		public override string ToString()
		{
			return Kind + ":" + ToDisplayText();
		}
	}
}
