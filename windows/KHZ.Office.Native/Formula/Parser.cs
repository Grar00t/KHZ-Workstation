using System;
using System.Collections.Generic;

namespace KHZ.Office.Native.Formula
{
	public abstract class FormulaNode
	{
	}

	public sealed class LiteralNode : FormulaNode
	{
		public LiteralNode(FormulaValue value)
		{
			Value = value ?? FormulaValue.BlankValue;
		}

		public FormulaValue Value { get; }
	}

	public sealed class ReferenceNode : FormulaNode
	{
		public string Sheet { get; set; }

		public CellRef Cell { get; set; }
	}

	public sealed class RangeNode : FormulaNode
	{
		public string Sheet { get; set; }

		public CellRef From { get; set; }

		public CellRef To { get; set; }
	}

	public sealed class UnaryNode : FormulaNode
	{
		public string Operator { get; set; }

		public FormulaNode Operand { get; set; }
	}

	public sealed class BinaryNode : FormulaNode
	{
		public string Operator { get; set; }

		public FormulaNode Left { get; set; }

		public FormulaNode Right { get; set; }
	}

	public sealed class FunctionNode : FormulaNode
	{
		public string Name { get; set; }

		public List<FormulaNode> Arguments { get; } = new List<FormulaNode>();
	}

	public sealed class NameNode : FormulaNode
	{
		public string Name { get; set; }
	}

	/// <summary>
	/// Precedence-climbing parser for the Excel expression grammar.
	/// Precedence, loosest to tightest: comparison, concatenation, additive,
	/// multiplicative, exponentiation, unary sign, postfix percent, primary.
	/// </summary>
	public sealed class Parser
	{
		private readonly List<Token> _tokens;
		private int _index;

		private Parser(List<Token> tokens)
		{
			_tokens = tokens;
		}

		public static FormulaNode Parse(string formula)
		{
			string text = formula ?? string.Empty;
			if (text.StartsWith("=", StringComparison.Ordinal))
			{
				text = text.Substring(1);
			}

			if (text.Trim().Length == 0)
			{
				throw new FormulaParseException("Empty formula");
			}

			Parser parser = new Parser(new Tokenizer(text).Tokenize());
			FormulaNode node = parser.ParseComparison();
			if (parser.Current.Kind != TokenKind.End)
			{
				throw new FormulaParseException("Unexpected token '" + parser.Current.Text + "'");
			}

			return node;
		}

		private Token Current { get { return _tokens[_index]; } }

		private Token Take()
		{
			Token token = _tokens[_index];
			if (token.Kind != TokenKind.End)
			{
				_index++;
			}

			return token;
		}

		private void Expect(TokenKind kind)
		{
			if (Current.Kind != kind)
			{
				throw new FormulaParseException("Expected " + kind + " but found '" + Current.Text + "'");
			}

			Take();
		}

		private static bool IsComparison(string op)
		{
			return op == "=" || op == "<>" || op == "<" || op == ">" || op == "<=" || op == ">=";
		}

		private FormulaNode ParseComparison()
		{
			FormulaNode left = ParseConcat();
			while (Current.Kind == TokenKind.Operator && IsComparison(Current.Text))
			{
				string op = Take().Text;
				FormulaNode right = ParseConcat();
				left = new BinaryNode { Operator = op, Left = left, Right = right };
			}

			return left;
		}

		private FormulaNode ParseConcat()
		{
			FormulaNode left = ParseAdditive();
			while (Current.Kind == TokenKind.Operator && Current.Text == "&")
			{
				Take();
				FormulaNode right = ParseAdditive();
				left = new BinaryNode { Operator = "&", Left = left, Right = right };
			}

			return left;
		}

		private FormulaNode ParseAdditive()
		{
			FormulaNode left = ParseMultiplicative();
			while (Current.Kind == TokenKind.Operator && (Current.Text == "+" || Current.Text == "-"))
			{
				string op = Take().Text;
				FormulaNode right = ParseMultiplicative();
				left = new BinaryNode { Operator = op, Left = left, Right = right };
			}

			return left;
		}

		private FormulaNode ParseMultiplicative()
		{
			FormulaNode left = ParsePower();
			while (Current.Kind == TokenKind.Operator && (Current.Text == "*" || Current.Text == "/"))
			{
				string op = Take().Text;
				FormulaNode right = ParsePower();
				left = new BinaryNode { Operator = op, Left = left, Right = right };
			}

			return left;
		}

		private FormulaNode ParsePower()
		{
			FormulaNode left = ParseUnary();
			if (Current.Kind == TokenKind.Operator && Current.Text == "^")
			{
				Take();
				FormulaNode right = ParsePower();
				return new BinaryNode { Operator = "^", Left = left, Right = right };
			}

			return left;
		}

		private FormulaNode ParseUnary()
		{
			if (Current.Kind == TokenKind.Operator && (Current.Text == "-" || Current.Text == "+"))
			{
				string op = Take().Text;
				FormulaNode operand = ParseUnary();
				return new UnaryNode { Operator = op, Operand = operand };
			}

			return ParsePostfix();
		}

		private FormulaNode ParsePostfix()
		{
			FormulaNode node = ParsePrimary();
			while (Current.Kind == TokenKind.Operator && Current.Text == "%")
			{
				Take();
				node = new UnaryNode { Operator = "%", Operand = node };
			}

			return node;
		}

		private FormulaNode ParsePrimary()
		{
			Token token = Current;
			switch (token.Kind)
			{
				case TokenKind.Number:
					Take();
					return new LiteralNode(FormulaValue.FromNumber(token.Number));
				case TokenKind.Text:
					Take();
					return new LiteralNode(FormulaValue.FromText(token.Text));
				case TokenKind.Logical:
					Take();
					return new LiteralNode(FormulaValue.FromLogical(token.Logical));
				case TokenKind.ErrorValue:
					Take();
					return new LiteralNode(FormulaValue.FromError(token.Text));
				case TokenKind.OpenParen:
				{
					Take();
					FormulaNode inner = ParseComparison();
					Expect(TokenKind.CloseParen);
					return inner;
				}

				case TokenKind.Function:
					return ParseFunction();
				case TokenKind.Reference:
					return ParseReferenceOrRange();
				case TokenKind.Name:
					Take();
					return new NameNode { Name = token.Text };
				default:
					throw new FormulaParseException("Unexpected token '" + token.Text + "'");
			}
		}

		private FormulaNode ParseFunction()
		{
			Token nameToken = Take();
			Expect(TokenKind.OpenParen);
			FunctionNode node = new FunctionNode { Name = nameToken.Text };

			if (Current.Kind == TokenKind.CloseParen)
			{
				Take();
				return node;
			}

			while (true)
			{
				// An omitted argument, as in IF(A1,,0), is a blank literal.
				if (Current.Kind == TokenKind.Separator || Current.Kind == TokenKind.CloseParen)
				{
					node.Arguments.Add(new LiteralNode(FormulaValue.BlankValue));
				}
				else
				{
					node.Arguments.Add(ParseComparison());
				}

				if (Current.Kind == TokenKind.Separator)
				{
					Take();
					continue;
				}

				break;
			}

			Expect(TokenKind.CloseParen);
			return node;
		}

		private FormulaNode ParseReferenceOrRange()
		{
			Token first = Take();
			CellRef from;
			CellRef.TryParseA1(first.Text, out from);

			if (Current.Kind == TokenKind.Colon && _tokens[_index + 1].Kind == TokenKind.Reference)
			{
				Take();
				Token second = Take();
				CellRef to;
				CellRef.TryParseA1(second.Text, out to);

				return new RangeNode
				{
					Sheet = first.Sheet ?? second.Sheet,
					From = new CellRef(Math.Min(from.Row, to.Row), Math.Min(from.Column, to.Column)),
					To = new CellRef(Math.Max(from.Row, to.Row), Math.Max(from.Column, to.Column))
				};
			}

			return new ReferenceNode { Sheet = first.Sheet, Cell = from };
		}
	}

	/// <summary>Read-only walks over a parsed tree.</summary>
	public static class FormulaInspector
	{
		/// <summary>Collects every function name referenced by a formula tree.</summary>
		public static void CollectFunctionNames(FormulaNode node, HashSet<string> output)
		{
			if (node == null || output == null)
			{
				return;
			}

			FunctionNode function = node as FunctionNode;
			if (function != null)
			{
				output.Add(function.Name);
				for (int index = 0; index < function.Arguments.Count; index++)
				{
					CollectFunctionNames(function.Arguments[index], output);
				}

				return;
			}

			UnaryNode unary = node as UnaryNode;
			if (unary != null)
			{
				CollectFunctionNames(unary.Operand, output);
				return;
			}

			BinaryNode binary = node as BinaryNode;
			if (binary != null)
			{
				CollectFunctionNames(binary.Left, output);
				CollectFunctionNames(binary.Right, output);
			}
		}
	}
}
