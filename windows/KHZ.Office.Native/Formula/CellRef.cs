using System;
using System.Globalization;
using System.Text;

namespace KHZ.Office.Native.Formula
{
	/// <summary>A 1-based worksheet cell address.</summary>
	public readonly struct CellRef : IEquatable<CellRef>
	{
		public CellRef(int row, int column)
		{
			Row = row;
			Column = column;
		}

		public int Row { get; }

		public int Column { get; }

		public bool IsValid { get { return Row >= 1 && Column >= 1; } }

		public static string ColumnToLetters(int column)
		{
			if (column < 1)
			{
				return string.Empty;
			}

			StringBuilder builder = new StringBuilder();
			int remaining = column;
			while (remaining > 0)
			{
				int remainder = (remaining - 1) % 26;
				builder.Insert(0, (char)('A' + remainder));
				remaining = (remaining - 1) / 26;
			}

			return builder.ToString();
		}

		public static int ColumnFromLetters(string letters)
		{
			if (string.IsNullOrEmpty(letters))
			{
				return -1;
			}

			int result = 0;
			for (int index = 0; index < letters.Length; index++)
			{
				char upper = char.ToUpperInvariant(letters[index]);
				if (upper < 'A' || upper > 'Z')
				{
					return -1;
				}

				result = (result * 26) + (upper - 'A' + 1);
				if (result > 16384)
				{
					return -1;
				}
			}

			return result == 0 ? -1 : result;
		}

		/// <summary>
		/// Parses an A1 address, tolerating absolute markers. Whole-column and
		/// whole-row forms such as <c>A:A</c> are deliberately rejected here; they are
		/// reported as unsupported rather than silently mis-parsed.
		/// </summary>
		public static bool TryParseA1(string text, out CellRef cell)
		{
			cell = default(CellRef);
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}

			int index = 0;
			if (text[index] == '$')
			{
				index++;
			}

			int letterStart = index;
			while (index < text.Length && char.IsLetter(text[index]))
			{
				index++;
			}

			if (index == letterStart)
			{
				return false;
			}

			string letters = text.Substring(letterStart, index - letterStart);
			if (index < text.Length && text[index] == '$')
			{
				index++;
			}

			int digitStart = index;
			while (index < text.Length && char.IsDigit(text[index]))
			{
				index++;
			}

			if (index == digitStart || index != text.Length)
			{
				return false;
			}

			int row;
			if (!int.TryParse(
				text.Substring(digitStart, index - digitStart),
				NumberStyles.Integer,
				CultureInfo.InvariantCulture,
				out row))
			{
				return false;
			}

			int column = ColumnFromLetters(letters);
			if (column < 1 || row < 1 || row > 1048576)
			{
				return false;
			}

			cell = new CellRef(row, column);
			return true;
		}

		public string ToA1()
		{
			return ColumnToLetters(Column) + Row.ToString(CultureInfo.InvariantCulture);
		}

		public bool Equals(CellRef other)
		{
			return Row == other.Row && Column == other.Column;
		}

		public override bool Equals(object obj)
		{
			return obj is CellRef && Equals((CellRef)obj);
		}

		public override int GetHashCode()
		{
			return (Row * 397) ^ Column;
		}

		public override string ToString()
		{
			return ToA1();
		}
	}
}
