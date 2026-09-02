using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KHZ.Office.Native.Formula
{
	public sealed class FormulaParseException : Exception
	{
		public FormulaParseException(string message)
			: base(message)
		{
		}
	}

	public enum TokenKind
	{
		Number,
		Text,
		Logical,
		ErrorValue,
		Reference,
		Function,
		Name,
		Operator,
		OpenParen,
		CloseParen,
		Separator,
		Colon,
		End
	}

	public sealed class Token
	{
		public TokenKind Kind { get; set; }

		public string Text { get; set; }

		public double Number { get; set; }

		public bool Logical { get; set; }

		/// <summary>Sheet qualifier for a reference token, or null when unqualified.</summary>
		public string Sheet { get; set; }

		public override string ToString()
		{
			return Kind + ":" + Text;
		}
	}

	public sealed class Tokenizer
	{
		private readonly string _source;
		private int _position;

		public Tokenizer(string source)
		{
			_source = source ?? string.Empty;
		}

		public List<Token> Tokenize()
		{
			List<Token> tokens = new List<Token>();
			while (true)
			{
				Token token = Next();
				tokens.Add(token);
				if (token.Kind == TokenKind.End)
				{
					break;
				}
			}

			return tokens;
		}

		/// <summary>Strips the future-function prefixes Excel writes into the file.</summary>
		public static string NormalizeFunctionName(string name)
		{
			string normalized = (name ?? string.Empty).ToUpperInvariant();
			if (normalized.StartsWith("_XLFN.", StringComparison.Ordinal))
			{
				normalized = normalized.Substring(6);
			}

			if (normalized.StartsWith("_XLWS.", StringComparison.Ordinal))
			{
				normalized = normalized.Substring(6);
			}

			return normalized;
		}

		private Token Next()
		{
			while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
			{
				_position++;
			}

			if (_position >= _source.Length)
			{
				return new Token { Kind = TokenKind.End, Text = string.Empty };
			}

			char current = _source[_position];

			if (char.IsDigit(current) ||
				(current == '.' && _position + 1 < _source.Length && char.IsDigit(_source[_position + 1])))
			{
				return ReadNumber();
			}

			if (current == '"')
			{
				return ReadText();
			}

			if (current == '#')
			{
				return ReadErrorLiteral();
			}

			if (current == '\'')
			{
				return ReadQuotedSheetReference();
			}

			if (char.IsLetter(current) || current == '_' || current == '$')
			{
				return ReadIdentifierLike();
			}

			if (current == '(')
			{
				_position++;
				return new Token { Kind = TokenKind.OpenParen, Text = "(" };
			}

			if (current == ')')
			{
				_position++;
				return new Token { Kind = TokenKind.CloseParen, Text = ")" };
			}

			if (current == ',' || current == ';')
			{
				_position++;
				return new Token { Kind = TokenKind.Separator, Text = current.ToString() };
			}

			if (current == ':')
			{
				_position++;
				return new Token { Kind = TokenKind.Colon, Text = ":" };
			}

			if (_position + 1 < _source.Length)
			{
				string pair = _source.Substring(_position, 2);
				if (pair == "<=" || pair == ">=" || pair == "<>")
				{
					_position += 2;
					return new Token { Kind = TokenKind.Operator, Text = pair };
				}
			}

			if ("+-*/^&=<>%".IndexOf(current) >= 0)
			{
				_position++;
				return new Token { Kind = TokenKind.Operator, Text = current.ToString() };
			}

			throw new FormulaParseException(
				"Unexpected character '" + current + "' at position " + _position.ToString(CultureInfo.InvariantCulture));
		}

		private Token ReadNumber()
		{
			int start = _position;
			while (_position < _source.Length && (char.IsDigit(_source[_position]) || _source[_position] == '.'))
			{
				_position++;
			}

			if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
			{
				int saved = _position;
				_position++;
				if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-'))
				{
					_position++;
				}

				if (_position < _source.Length && char.IsDigit(_source[_position]))
				{
					while (_position < _source.Length && char.IsDigit(_source[_position]))
					{
						_position++;
					}
				}
				else
				{
					_position = saved;
				}
			}

			string text = _source.Substring(start, _position - start);
			double value;
			if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
			{
				throw new FormulaParseException("Invalid number literal '" + text + "'");
			}

			return new Token { Kind = TokenKind.Number, Text = text, Number = value };
		}

		private Token ReadText()
		{
			_position++;
			StringBuilder builder = new StringBuilder();
			while (_position < _source.Length)
			{
				char current = _source[_position];
				if (current == '"')
				{
					if (_position + 1 < _source.Length && _source[_position + 1] == '"')
					{
						builder.Append('"');
						_position += 2;
						continue;
					}

					_position++;
					return new Token { Kind = TokenKind.Text, Text = builder.ToString() };
				}

				builder.Append(current);
				_position++;
			}

			throw new FormulaParseException("Unterminated text literal");
		}

		private Token ReadErrorLiteral()
		{
			int start = _position;
			_position++;
			while (_position < _source.Length)
			{
				char current = _source[_position];
				if (char.IsLetterOrDigit(current) || current == '/')
				{
					_position++;
					continue;
				}

				if (current == '!' || current == '?')
				{
					_position++;
				}

				break;
			}

			string text = _source.Substring(start, _position - start).ToUpperInvariant();
			if (!FormulaErrors.IsErrorLiteral(text))
			{
				throw new FormulaParseException("Unknown error literal '" + text + "'");
			}

			return new Token { Kind = TokenKind.ErrorValue, Text = text };
		}

		private Token ReadQuotedSheetReference()
		{
			_position++;
			StringBuilder builder = new StringBuilder();
			bool closed = false;
			while (_position < _source.Length)
			{
				char current = _source[_position];
				if (current == '\'')
				{
					if (_position + 1 < _source.Length && _source[_position + 1] == '\'')
					{
						builder.Append('\'');
						_position += 2;
						continue;
					}

					_position++;
					closed = true;
					break;
				}

				builder.Append(current);
				_position++;
			}

			if (!closed || _position >= _source.Length || _source[_position] != '!')
			{
				throw new FormulaParseException("Expected '!' after quoted sheet name");
			}

			_position++;
			string reference = ReadReferenceRun();
			CellRef parsed;
			if (!CellRef.TryParseA1(reference, out parsed))
			{
				throw new FormulaParseException("Unsupported reference '" + reference + "'");
			}

			return new Token
			{
				Kind = TokenKind.Reference,
				Text = reference,
				Sheet = builder.ToString()
			};
		}

		private string ReadReferenceRun()
		{
			int start = _position;
			while (_position < _source.Length &&
				(char.IsLetterOrDigit(_source[_position]) || _source[_position] == '$'))
			{
				_position++;
			}

			return _source.Substring(start, _position - start);
		}

		private Token ReadIdentifierLike()
		{
			int start = _position;
			while (_position < _source.Length)
			{
				char current = _source[_position];
				if (char.IsLetterOrDigit(current) || current == '_' || current == '.' || current == '$')
				{
					_position++;
					continue;
				}

				break;
			}

			string text = _source.Substring(start, _position - start);

			if (_position < _source.Length && _source[_position] == '!')
			{
				_position++;
				string reference = ReadReferenceRun();
				CellRef qualified;
				if (!CellRef.TryParseA1(reference, out qualified))
				{
					throw new FormulaParseException("Unsupported reference '" + reference + "'");
				}

				return new Token { Kind = TokenKind.Reference, Text = reference, Sheet = text };
			}

			if (_position < _source.Length && _source[_position] == '(')
			{
				return new Token { Kind = TokenKind.Function, Text = NormalizeFunctionName(text) };
			}

			CellRef cell;
			if (CellRef.TryParseA1(text, out cell))
			{
				return new Token { Kind = TokenKind.Reference, Text = text, Sheet = null };
			}

			string upper = text.ToUpperInvariant();
			if (upper == "TRUE")
			{
				return new Token { Kind = TokenKind.Logical, Text = text, Logical = true };
			}

			if (upper == "FALSE")
			{
				return new Token { Kind = TokenKind.Logical, Text = text, Logical = false };
			}

			return new Token { Kind = TokenKind.Name, Text = text };
		}
	}
}
