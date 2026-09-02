using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KHZ.Office.Native.Formula
{
	/// <summary>
	/// Strict worksheet functions. Lazy functions live in <see cref="Evaluator"/>.
	/// </summary>
	public static class FunctionLibrary
	{
		public delegate FormulaValue FunctionHandler(
			IReadOnlyList<FormulaValue> arguments,
			IEvaluationContext context);

		private static readonly Dictionary<string, FunctionHandler> Handlers = CreateHandlers();

		/// <summary>Functions that must see error arguments instead of propagating them.</summary>
		private static readonly HashSet<string> ErrorTolerant =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				"COUNTA",
				"COUNTBLANK",
				"ISERROR",
				"ISERR",
				"ISNA",
				"ISBLANK",
				"ISNUMBER",
				"ISTEXT",
				"ISLOGICAL",
				"NA",
				"TRUE",
				"FALSE",
				"ERROR.TYPE"
			};

		public static IReadOnlyCollection<string> SupportedFunctions
		{
			get { return new List<string>(Handlers.Keys); }
		}

		public static int SupportedFunctionCount
		{
			get { return Handlers.Count; }
		}

		public static bool IsSupported(string name)
		{
			if (string.IsNullOrEmpty(name))
			{
				return false;
			}

			return Handlers.ContainsKey(name) || Evaluator.LazyFunctionNames.Contains(name);
		}

		public static FormulaValue Invoke(
			string name,
			IReadOnlyList<FormulaValue> arguments,
			IEvaluationContext context)
		{
			FunctionHandler handler;
			if (!Handlers.TryGetValue(name ?? string.Empty, out handler))
			{
				// Unimplemented function names surface as #NAME? -- exactly the symptom
				// docs/OFFICE-COMPATIBILITY.md records for SORTBY, TAKE, DROP, HSTACK
				// and VSTACK under the external engine.
				return FormulaValue.FromError(FormulaErrors.Name);
			}

			if (!ErrorTolerant.Contains(name))
			{
				for (int index = 0; index < arguments.Count; index++)
				{
					if (arguments[index].Kind == FormulaValueKind.Error)
					{
						return arguments[index];
					}
				}
			}

			try
			{
				return handler(arguments, context);
			}
			catch (Exception)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}
		}

		private static Dictionary<string, FunctionHandler> CreateHandlers()
		{
			Dictionary<string, FunctionHandler> map =
				new Dictionary<string, FunctionHandler>(StringComparer.OrdinalIgnoreCase);

			// ---- Aggregation ------------------------------------------------------
			map["SUM"] = (a, c) => Reduce(a, values => Total(values));
			map["PRODUCT"] = (a, c) => Reduce(a, values =>
			{
				if (values.Count == 0)
				{
					return 0d;
				}

				double product = 1d;
				for (int i = 0; i < values.Count; i++)
				{
					product *= values[i];
				}

				return product;
			});

			map["AVERAGE"] = (a, c) => ReduceOrError(a, values =>
				values.Count == 0
					? FormulaValue.FromError(FormulaErrors.Div0)
					: FormulaValue.FromNumber(Total(values) / values.Count));

			map["MIN"] = (a, c) => ReduceOrError(a, values =>
			{
				if (values.Count == 0)
				{
					return FormulaValue.FromNumber(0d);
				}

				double smallest = values[0];
				for (int i = 1; i < values.Count; i++)
				{
					if (values[i] < smallest)
					{
						smallest = values[i];
					}
				}

				return FormulaValue.FromNumber(smallest);
			});

			map["MAX"] = (a, c) => ReduceOrError(a, values =>
			{
				if (values.Count == 0)
				{
					return FormulaValue.FromNumber(0d);
				}

				double largest = values[0];
				for (int i = 1; i < values.Count; i++)
				{
					if (values[i] > largest)
					{
						largest = values[i];
					}
				}

				return FormulaValue.FromNumber(largest);
			});

			map["MEDIAN"] = (a, c) => ReduceOrError(a, values =>
			{
				if (values.Count == 0)
				{
					return FormulaValue.FromError(FormulaErrors.Number);
				}

				values.Sort();
				int middle = values.Count / 2;
				if (values.Count % 2 == 1)
				{
					return FormulaValue.FromNumber(values[middle]);
				}

				return FormulaValue.FromNumber((values[middle - 1] + values[middle]) / 2d);
			});

			map["STDEV"] = (a, c) => ReduceOrError(a, values =>
			{
				if (values.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Div0);
				}

				double mean = Total(values) / values.Count;
				double sumOfSquares = 0d;
				for (int i = 0; i < values.Count; i++)
				{
					double delta = values[i] - mean;
					sumOfSquares += delta * delta;
				}

				return FormulaValue.FromNumber(Math.Sqrt(sumOfSquares / (values.Count - 1)));
			});

			map["COUNT"] = (a, c) =>
			{
				int count = 0;
				for (int i = 0; i < a.Count; i++)
				{
					foreach (FormulaValue item in a[i].Enumerate())
					{
						if (item.Kind == FormulaValueKind.Number)
						{
							count++;
						}
					}
				}

				return FormulaValue.FromNumber(count);
			};

			map["COUNTA"] = (a, c) =>
			{
				int count = 0;
				for (int i = 0; i < a.Count; i++)
				{
					foreach (FormulaValue item in a[i].Enumerate())
					{
						if (item.Kind != FormulaValueKind.Blank)
						{
							count++;
						}
					}
				}

				return FormulaValue.FromNumber(count);
			};

			map["COUNTBLANK"] = (a, c) =>
			{
				int count = 0;
				for (int i = 0; i < a.Count; i++)
				{
					foreach (FormulaValue item in a[i].Enumerate())
					{
						if (item.Kind == FormulaValueKind.Blank)
						{
							count++;
							continue;
						}

						if (item.Kind == FormulaValueKind.Text && item.TextValue.Length == 0)
						{
							count++;
						}
					}
				}

				return FormulaValue.FromNumber(count);
			};

			map["SUMPRODUCT"] = (a, c) =>
			{
				if (a.Count == 0)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				List<List<FormulaValue>> vectors = new List<List<FormulaValue>>();
				for (int i = 0; i < a.Count; i++)
				{
					vectors.Add(Flatten(a[i]));
				}

				int length = vectors[0].Count;
				for (int i = 1; i < vectors.Count; i++)
				{
					if (vectors[i].Count != length)
					{
						return FormulaValue.FromError(FormulaErrors.Value);
					}
				}

				double total = 0d;
				for (int position = 0; position < length; position++)
				{
					double product = 1d;
					for (int i = 0; i < vectors.Count; i++)
					{
						FormulaValue item = vectors[i][position];
						if (item.Kind == FormulaValueKind.Error)
						{
							return item;
						}

						double numeric;
						product *= item.TryGetNumber(out numeric) && item.Kind == FormulaValueKind.Number
							? numeric
							: 0d;
					}

					total += product;
				}

				return FormulaValue.FromNumber(total);
			};

			// ---- Conditional aggregation -----------------------------------------
			map["SUMIF"] = (a, c) => ConditionalSum(a, false);
			map["AVERAGEIF"] = (a, c) => ConditionalSum(a, true);
			map["COUNTIF"] = (a, c) =>
			{
				if (a.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				List<FormulaValue> range = Flatten(a[0]);
				FormulaValue criteria = a[1].Scalar();
				int count = 0;
				for (int i = 0; i < range.Count; i++)
				{
					if (MatchesCriteria(range[i], criteria))
					{
						count++;
					}
				}

				return FormulaValue.FromNumber(count);
			};

			map["SUMIFS"] = (a, c) =>
			{
				if (a.Count < 3)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				List<FormulaValue> target = Flatten(a[0]);
				double total = 0d;
				for (int position = 0; position < target.Count; position++)
				{
					if (!MatchesAllCriteria(a, 1, position))
					{
						continue;
					}

					FormulaValue item = target[position];
					if (item.Kind == FormulaValueKind.Error)
					{
						return item;
					}

					if (item.Kind == FormulaValueKind.Number)
					{
						total += item.NumberValue;
					}
				}

				return FormulaValue.FromNumber(total);
			};

			map["COUNTIFS"] = (a, c) =>
			{
				if (a.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				List<FormulaValue> first = Flatten(a[0]);
				int count = 0;
				for (int position = 0; position < first.Count; position++)
				{
					if (MatchesAllCriteria(a, 0, position))
					{
						count++;
					}
				}

				return FormulaValue.FromNumber(count);
			};

			// ---- Rounding and arithmetic -----------------------------------------
			map["ROUND"] = (a, c) => RoundWith(a, (value, digits) =>
			{
				double factor = Math.Pow(10d, digits);
				return Math.Round(value * factor, MidpointRounding.AwayFromZero) / factor;
			});

			map["ROUNDUP"] = (a, c) => RoundWith(a, (value, digits) =>
			{
				double factor = Math.Pow(10d, digits);
				double scaled = value * factor;
				return (value >= 0d ? Math.Ceiling(scaled) : Math.Floor(scaled)) / factor;
			});

			map["ROUNDDOWN"] = (a, c) => RoundWith(a, (value, digits) =>
			{
				double factor = Math.Pow(10d, digits);
				double scaled = value * factor;
				return (value >= 0d ? Math.Floor(scaled) : Math.Ceiling(scaled)) / factor;
			});

			map["TRUNC"] = (a, c) => RoundWith(a, (value, digits) =>
			{
				double factor = Math.Pow(10d, digits);
				return Math.Truncate(value * factor) / factor;
			});

			map["INT"] = (a, c) => Unary(a, value => Math.Floor(value));
			map["ABS"] = (a, c) => Unary(a, value => Math.Abs(value));
			map["SIGN"] = (a, c) => Unary(a, value => Math.Sign(value));
			map["SQRT"] = (a, c) => UnaryChecked(a, value =>
				value < 0d
					? FormulaValue.FromError(FormulaErrors.Number)
					: FormulaValue.FromNumber(Math.Sqrt(value)));

			map["MOD"] = (a, c) => Binary(a, (x, y) =>
				y == 0d
					? FormulaValue.FromError(FormulaErrors.Div0)
					: FormulaValue.FromNumber(x - (Math.Floor(x / y) * y)));

			map["POWER"] = (a, c) => Binary(a, (x, y) =>
			{
				double result = Math.Pow(x, y);
				if (double.IsNaN(result) || double.IsInfinity(result))
				{
					return FormulaValue.FromError(FormulaErrors.Number);
				}

				return FormulaValue.FromNumber(result);
			});

			map["CEILING"] = (a, c) => Binary(a, (x, y) =>
				y == 0d
					? FormulaValue.FromNumber(0d)
					: FormulaValue.FromNumber(Math.Ceiling(x / y) * y));

			map["FLOOR"] = (a, c) => Binary(a, (x, y) =>
				y == 0d
					? FormulaValue.FromError(FormulaErrors.Div0)
					: FormulaValue.FromNumber(Math.Floor(x / y) * y));

			// ---- Logical ----------------------------------------------------------
			map["AND"] = (a, c) => BooleanFold(a, true);
			map["OR"] = (a, c) => BooleanFold(a, false);
			map["XOR"] = (a, c) =>
			{
				int trueCount = 0;
				bool sawAny = false;
				for (int i = 0; i < a.Count; i++)
				{
					foreach (FormulaValue item in a[i].Enumerate())
					{
						if (item.Kind == FormulaValueKind.Blank || item.Kind == FormulaValueKind.Text)
						{
							continue;
						}

						double numeric;
						if (!item.TryGetNumber(out numeric))
						{
							continue;
						}

						sawAny = true;
						if (numeric != 0d)
						{
							trueCount++;
						}
					}
				}

				if (!sawAny)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				return FormulaValue.FromLogical(trueCount % 2 == 1);
			};

			map["NOT"] = (a, c) =>
			{
				if (a.Count < 1)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				double numeric;
				if (!a[0].TryGetNumber(out numeric))
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				return FormulaValue.FromLogical(numeric == 0d);
			};

			map["TRUE"] = (a, c) => FormulaValue.TrueValue;
			map["FALSE"] = (a, c) => FormulaValue.FalseValue;
			map["NA"] = (a, c) => FormulaValue.FromError(FormulaErrors.NotAvailable);

			map["ISERROR"] = (a, c) => FormulaValue.FromLogical(a.Count > 0 && a[0].Scalar().IsError);
			map["ISERR"] = (a, c) => FormulaValue.FromLogical(
				a.Count > 0 &&
				a[0].Scalar().IsError &&
				a[0].Scalar().ErrorCode != FormulaErrors.NotAvailable);

			map["ISNA"] = (a, c) => FormulaValue.FromLogical(
				a.Count > 0 &&
				a[0].Scalar().IsError &&
				a[0].Scalar().ErrorCode == FormulaErrors.NotAvailable);

			map["ISBLANK"] = (a, c) => FormulaValue.FromLogical(a.Count > 0 && a[0].Scalar().IsBlank);
			map["ISNUMBER"] = (a, c) => FormulaValue.FromLogical(
				a.Count > 0 && a[0].Scalar().Kind == FormulaValueKind.Number);

			map["ISTEXT"] = (a, c) => FormulaValue.FromLogical(
				a.Count > 0 && a[0].Scalar().Kind == FormulaValueKind.Text);

			map["ISLOGICAL"] = (a, c) => FormulaValue.FromLogical(
				a.Count > 0 && a[0].Scalar().Kind == FormulaValueKind.Logical);

			// ---- Lookup -----------------------------------------------------------
			map["VLOOKUP"] = (a, c) => Lookup(a, true);
			map["HLOOKUP"] = (a, c) => Lookup(a, false);

			map["MATCH"] = (a, c) =>
			{
				if (a.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				FormulaValue needle = a[0].Scalar();
				List<FormulaValue> haystack = Flatten(a[1]);
				int mode = 1;
				double rawMode;
				if (a.Count >= 3 && a[2].TryGetNumber(out rawMode))
				{
					mode = (int)Math.Truncate(rawMode);
				}

				int best = -1;
				for (int i = 0; i < haystack.Count; i++)
				{
					int comparison = Evaluator.CompareValues(haystack[i], needle);
					if (comparison == 0)
					{
						return FormulaValue.FromNumber(i + 1);
					}

					if (mode == 1 && comparison < 0)
					{
						best = i;
					}
					else if (mode == -1 && comparison > 0)
					{
						best = i;
					}
				}

				if (mode == 0 || best < 0)
				{
					return FormulaValue.FromError(FormulaErrors.NotAvailable);
				}

				return FormulaValue.FromNumber(best + 1);
			};

			map["INDEX"] = (a, c) =>
			{
				if (a.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				FormulaValue source = a[0];
				if (!source.IsArray)
				{
					return source.Scalar();
				}

				FormulaValue[,] cells = source.ArrayValue;
				int rows = cells.GetLength(0);
				int columns = cells.GetLength(1);

				double rawRow;
				if (!a[1].TryGetNumber(out rawRow))
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				int rowIndex = (int)Math.Truncate(rawRow);
				int columnIndex = 1;
				double rawColumn;
				if (a.Count >= 3 && a[2].TryGetNumber(out rawColumn))
				{
					columnIndex = (int)Math.Truncate(rawColumn);
				}

				// A single-row or single-column range is indexed along its own axis.
				if (a.Count < 3 && rows == 1 && columns > 1)
				{
					columnIndex = rowIndex;
					rowIndex = 1;
				}

				if (rowIndex < 1 || rowIndex > rows || columnIndex < 1 || columnIndex > columns)
				{
					return FormulaValue.FromError(FormulaErrors.Reference);
				}

				return cells[rowIndex - 1, columnIndex - 1] ?? FormulaValue.BlankValue;
			};

			map["ROWS"] = (a, c) => FormulaValue.FromNumber(
				a.Count > 0 && a[0].IsArray ? a[0].ArrayValue.GetLength(0) : 1);

			map["COLUMNS"] = (a, c) => FormulaValue.FromNumber(
				a.Count > 0 && a[0].IsArray ? a[0].ArrayValue.GetLength(1) : 1);

			// ---- Text -------------------------------------------------------------
			map["LEN"] = (a, c) => FormulaValue.FromNumber(
				a.Count > 0 ? a[0].Scalar().ToDisplayText().Length : 0);

			map["UPPER"] = (a, c) => FormulaValue.FromText(TextOf(a, 0).ToUpperInvariant());
			map["LOWER"] = (a, c) => FormulaValue.FromText(TextOf(a, 0).ToLowerInvariant());
			map["TRIM"] = (a, c) => FormulaValue.FromText(CollapseSpaces(TextOf(a, 0)));

			map["LEFT"] = (a, c) =>
			{
				string text = TextOf(a, 0);
				int take = CountArgument(a, 1, 1);
				if (take < 0)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				return FormulaValue.FromText(text.Substring(0, Math.Min(take, text.Length)));
			};

			map["RIGHT"] = (a, c) =>
			{
				string text = TextOf(a, 0);
				int take = CountArgument(a, 1, 1);
				if (take < 0)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				int start = Math.Max(0, text.Length - take);
				return FormulaValue.FromText(text.Substring(start));
			};

			map["MID"] = (a, c) =>
			{
				string text = TextOf(a, 0);
				int start = CountArgument(a, 1, 1);
				int take = CountArgument(a, 2, 0);
				if (start < 1 || take < 0)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				if (start > text.Length)
				{
					return FormulaValue.FromText(string.Empty);
				}

				int available = text.Length - (start - 1);
				return FormulaValue.FromText(text.Substring(start - 1, Math.Min(take, available)));
			};

			map["CONCAT"] = (a, c) => ConcatenateAll(a);
			map["CONCATENATE"] = (a, c) => ConcatenateAll(a);

			map["TEXTJOIN"] = (a, c) =>
			{
				if (a.Count < 3)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				string delimiter = a[0].Scalar().ToDisplayText();
				double ignoreFlag;
				bool ignoreEmpty = !a[1].TryGetNumber(out ignoreFlag) || ignoreFlag != 0d;

				List<string> pieces = new List<string>();
				for (int i = 2; i < a.Count; i++)
				{
					foreach (FormulaValue item in a[i].Enumerate())
					{
						if (ignoreEmpty && (item.IsBlank || item.ToDisplayText().Length == 0))
						{
							continue;
						}

						pieces.Add(item.ToDisplayText());
					}
				}

				return FormulaValue.FromText(string.Join(delimiter, pieces));
			};

			map["SUBSTITUTE"] = (a, c) =>
			{
				if (a.Count < 3)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				string text = TextOf(a, 0);
				string oldText = TextOf(a, 1);
				string newText = TextOf(a, 2);
				if (oldText.Length == 0)
				{
					return FormulaValue.FromText(text);
				}

				if (a.Count < 4)
				{
					return FormulaValue.FromText(text.Replace(oldText, newText));
				}

				int occurrence = CountArgument(a, 3, 1);
				if (occurrence < 1)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				int seen = 0;
				int cursor = 0;
				while (cursor <= text.Length - oldText.Length)
				{
					int found = text.IndexOf(oldText, cursor, StringComparison.Ordinal);
					if (found < 0)
					{
						break;
					}

					seen++;
					if (seen == occurrence)
					{
						return FormulaValue.FromText(
							text.Substring(0, found) + newText + text.Substring(found + oldText.Length));
					}

					cursor = found + oldText.Length;
				}

				return FormulaValue.FromText(text);
			};

			map["FIND"] = (a, c) => FindIn(a, StringComparison.Ordinal);
			map["SEARCH"] = (a, c) => FindIn(a, StringComparison.OrdinalIgnoreCase);

			map["VALUE"] = (a, c) =>
			{
				double numeric;
				if (a.Count > 0 && double.TryParse(
					TextOf(a, 0),
					NumberStyles.Any,
					CultureInfo.InvariantCulture,
					out numeric))
				{
					return FormulaValue.FromNumber(numeric);
				}

				return FormulaValue.FromError(FormulaErrors.Value);
			};

			// ---- Date -------------------------------------------------------------
			map["TODAY"] = (a, c) => FormulaValue.FromNumber(Math.Floor(c.Now.Date.ToOADate()));
			map["NOW"] = (a, c) => FormulaValue.FromNumber(c.Now.ToOADate());
			map["YEAR"] = (a, c) => DatePart(a, date => date.Year);
			map["MONTH"] = (a, c) => DatePart(a, date => date.Month);
			map["DAY"] = (a, c) => DatePart(a, date => date.Day);
			map["HOUR"] = (a, c) => DatePart(a, date => date.Hour);
			map["MINUTE"] = (a, c) => DatePart(a, date => date.Minute);
			map["WEEKDAY"] = (a, c) => DatePart(a, date => ((int)date.DayOfWeek) + 1);

			map["DATE"] = (a, c) =>
			{
				if (a.Count < 3)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				double year;
				double month;
				double day;
				if (!a[0].TryGetNumber(out year) ||
					!a[1].TryGetNumber(out month) ||
					!a[2].TryGetNumber(out day))
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				int yearPart = (int)Math.Truncate(year);
				if (yearPart < 1900 || yearPart > 9999)
				{
					return FormulaValue.FromError(FormulaErrors.Number);
				}

				DateTime resolved = new DateTime(yearPart, 1, 1)
					.AddMonths((int)Math.Truncate(month) - 1)
					.AddDays(Math.Truncate(day) - 1d);

				return FormulaValue.FromNumber(resolved.ToOADate());
			};

			map["EOMONTH"] = (a, c) =>
			{
				if (a.Count < 2)
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				DateTime start;
				double offset;
				if (!TryToDate(a[0], out start) || !a[1].TryGetNumber(out offset))
				{
					return FormulaValue.FromError(FormulaErrors.Value);
				}

				DateTime shifted = start.AddMonths((int)Math.Truncate(offset));
				DateTime endOfMonth = new DateTime(
					shifted.Year,
					shifted.Month,
					DateTime.DaysInMonth(shifted.Year, shifted.Month));

				return FormulaValue.FromNumber(endOfMonth.ToOADate());
			};

			return map;
		}

		// ---- Shared helpers ------------------------------------------------------

		private static double Total(List<double> values)
		{
			double total = 0d;
			for (int index = 0; index < values.Count; index++)
			{
				total += values[index];
			}

			return total;
		}

		/// <summary>
		/// Collects numeric cells. An error anywhere inside a range aborts collection so
		/// aggregation propagates it rather than quietly skipping the cell.
		/// </summary>
		private static bool TryCollectNumbers(
			IReadOnlyList<FormulaValue> arguments,
			List<double> output,
			out FormulaValue error)
		{
			error = null;
			for (int index = 0; index < arguments.Count; index++)
			{
				bool isRange = arguments[index].IsArray;
				foreach (FormulaValue item in arguments[index].Enumerate())
				{
					if (item.Kind == FormulaValueKind.Error)
					{
						error = item;
						return false;
					}

					if (item.Kind == FormulaValueKind.Number)
					{
						output.Add(item.NumberValue);
						continue;
					}

					// Inside a range, text and logicals are skipped. Passed directly as a
					// scalar argument, they coerce -- matching Excel.
					if (isRange)
					{
						continue;
					}

					if (item.Kind == FormulaValueKind.Logical)
					{
						output.Add(item.LogicalValue ? 1d : 0d);
						continue;
					}

					if (item.Kind == FormulaValueKind.Text)
					{
						double parsed;
						if (double.TryParse(
							item.TextValue,
							NumberStyles.Any,
							CultureInfo.InvariantCulture,
							out parsed))
						{
							output.Add(parsed);
						}
					}
				}
			}

			return true;
		}

		private static FormulaValue Reduce(
			IReadOnlyList<FormulaValue> arguments,
			Func<List<double>, double> reducer)
		{
			List<double> numbers = new List<double>();
			FormulaValue error;
			if (!TryCollectNumbers(arguments, numbers, out error))
			{
				return error;
			}

			return FormulaValue.FromNumber(reducer(numbers));
		}

		private static FormulaValue ReduceOrError(
			IReadOnlyList<FormulaValue> arguments,
			Func<List<double>, FormulaValue> reducer)
		{
			List<double> numbers = new List<double>();
			FormulaValue error;
			if (!TryCollectNumbers(arguments, numbers, out error))
			{
				return error;
			}

			return reducer(numbers);
		}

		private static FormulaValue Unary(
			IReadOnlyList<FormulaValue> arguments,
			Func<double, double> projection)
		{
			double value;
			if (arguments.Count < 1 || !arguments[0].TryGetNumber(out value))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return FormulaValue.FromNumber(projection(value));
		}

		private static FormulaValue UnaryChecked(
			IReadOnlyList<FormulaValue> arguments,
			Func<double, FormulaValue> projection)
		{
			double value;
			if (arguments.Count < 1 || !arguments[0].TryGetNumber(out value))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return projection(value);
		}

		private static FormulaValue Binary(
			IReadOnlyList<FormulaValue> arguments,
			Func<double, double, FormulaValue> projection)
		{
			double first;
			double second;
			if (arguments.Count < 2 ||
				!arguments[0].TryGetNumber(out first) ||
				!arguments[1].TryGetNumber(out second))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return projection(first, second);
		}

		private static FormulaValue RoundWith(
			IReadOnlyList<FormulaValue> arguments,
			Func<double, int, double> rounder)
		{
			double value;
			if (arguments.Count < 1 || !arguments[0].TryGetNumber(out value))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			int digits = CountArgument(arguments, 1, 0);
			if (digits > 15)
			{
				digits = 15;
			}

			if (digits < -15)
			{
				digits = -15;
			}

			return FormulaValue.FromNumber(rounder(value, digits));
		}

		private static FormulaValue BooleanFold(IReadOnlyList<FormulaValue> arguments, bool requireAll)
		{
			bool sawAny = false;
			bool accumulator = requireAll;

			for (int index = 0; index < arguments.Count; index++)
			{
				foreach (FormulaValue item in arguments[index].Enumerate())
				{
					if (item.Kind == FormulaValueKind.Error)
					{
						return item;
					}

					if (item.Kind == FormulaValueKind.Blank || item.Kind == FormulaValueKind.Text)
					{
						continue;
					}

					double numeric;
					if (!item.TryGetNumber(out numeric))
					{
						continue;
					}

					sawAny = true;
					bool flag = numeric != 0d;
					accumulator = requireAll ? (accumulator && flag) : (accumulator || flag);
				}
			}

			if (!sawAny)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return FormulaValue.FromLogical(accumulator);
		}

		private static List<FormulaValue> Flatten(FormulaValue value)
		{
			List<FormulaValue> flattened = new List<FormulaValue>();
			foreach (FormulaValue item in value.Enumerate())
			{
				flattened.Add(item);
			}

			return flattened;
		}

		private static FormulaValue ConditionalSum(IReadOnlyList<FormulaValue> arguments, bool average)
		{
			if (arguments.Count < 2)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			List<FormulaValue> range = Flatten(arguments[0]);
			List<FormulaValue> target = arguments.Count >= 3 ? Flatten(arguments[2]) : range;
			FormulaValue criteria = arguments[1].Scalar();

			double total = 0d;
			int matched = 0;
			for (int index = 0; index < range.Count; index++)
			{
				if (!MatchesCriteria(range[index], criteria))
				{
					continue;
				}

				if (index >= target.Count)
				{
					continue;
				}

				FormulaValue item = target[index];
				if (item.Kind == FormulaValueKind.Error)
				{
					return item;
				}

				if (item.Kind != FormulaValueKind.Number)
				{
					continue;
				}

				total += item.NumberValue;
				matched++;
			}

			if (!average)
			{
				return FormulaValue.FromNumber(total);
			}

			if (matched == 0)
			{
				return FormulaValue.FromError(FormulaErrors.Div0);
			}

			return FormulaValue.FromNumber(total / matched);
		}

		private static bool MatchesAllCriteria(
			IReadOnlyList<FormulaValue> arguments,
			int firstPairIndex,
			int position)
		{
			for (int index = firstPairIndex; index + 1 < arguments.Count; index += 2)
			{
				List<FormulaValue> range = Flatten(arguments[index]);
				if (position >= range.Count)
				{
					return false;
				}

				if (!MatchesCriteria(range[position], arguments[index + 1].Scalar()))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Applies an Excel criteria expression such as "&gt;=10", "&lt;&gt;x" or "A*".</summary>
		public static bool MatchesCriteria(FormulaValue value, FormulaValue criteria)
		{
			string expression = criteria.Kind == FormulaValueKind.Text
				? criteria.TextValue
				: criteria.ToDisplayText();

			expression = expression ?? string.Empty;
			string op = "=";
			string operand = expression;

			string[] candidates = new string[] { ">=", "<=", "<>", ">", "<", "=" };
			for (int index = 0; index < candidates.Length; index++)
			{
				if (operand.StartsWith(candidates[index], StringComparison.Ordinal))
				{
					op = candidates[index];
					operand = operand.Substring(candidates[index].Length);
					break;
				}
			}

			FormulaValue expected;
			double numeric;
			if (double.TryParse(operand, NumberStyles.Any, CultureInfo.InvariantCulture, out numeric))
			{
				expected = FormulaValue.FromNumber(numeric);
			}
			else if (string.Equals(operand, "TRUE", StringComparison.OrdinalIgnoreCase))
			{
				expected = FormulaValue.TrueValue;
			}
			else if (string.Equals(operand, "FALSE", StringComparison.OrdinalIgnoreCase))
			{
				expected = FormulaValue.FalseValue;
			}
			else
			{
				expected = FormulaValue.FromText(operand);
			}

			bool hasWildcard = operand.IndexOf('*') >= 0 || operand.IndexOf('?') >= 0;
			if ((op == "=" || op == "<>") &&
				expected.Kind == FormulaValueKind.Text &&
				hasWildcard)
			{
				bool wildcardMatch = WildcardMatch(value.ToDisplayText(), operand);
				return op == "=" ? wildcardMatch : !wildcardMatch;
			}

			if (value.IsBlank && operand.Length == 0 && op == "=")
			{
				return true;
			}

			int comparison = Evaluator.CompareValues(value, expected);
			switch (op)
			{
				case "=":
					return comparison == 0;
				case "<>":
					return comparison != 0;
				case ">":
					return comparison > 0;
				case "<":
					return comparison < 0;
				case ">=":
					return comparison >= 0;
				case "<=":
					return comparison <= 0;
				default:
					return false;
			}
		}

		private static bool WildcardMatch(string input, string pattern)
		{
			StringBuilder builder = new StringBuilder("^");
			for (int index = 0; index < pattern.Length; index++)
			{
				char current = pattern[index];
				if (current == '*')
				{
					builder.Append(".*");
					continue;
				}

				if (current == '?')
				{
					builder.Append('.');
					continue;
				}

				builder.Append(Regex.Escape(current.ToString()));
			}

			builder.Append('$');
			return Regex.IsMatch(input ?? string.Empty, builder.ToString(), RegexOptions.IgnoreCase);
		}

		private static FormulaValue Lookup(IReadOnlyList<FormulaValue> arguments, bool vertical)
		{
			if (arguments.Count < 3)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			FormulaValue needle = arguments[0].Scalar();
			if (!arguments[1].IsArray)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			FormulaValue[,] table = arguments[1].ArrayValue;
			int rows = table.GetLength(0);
			int columns = table.GetLength(1);

			double rawIndex;
			if (!arguments[2].TryGetNumber(out rawIndex))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			int offset = (int)Math.Truncate(rawIndex);
			int limit = vertical ? columns : rows;
			if (offset < 1 || offset > limit)
			{
				return FormulaValue.FromError(FormulaErrors.Reference);
			}

			bool approximate = true;
			double flag;
			if (arguments.Count >= 4 && arguments[3].TryGetNumber(out flag))
			{
				approximate = flag != 0d;
			}

			int scanLength = vertical ? rows : columns;
			int best = -1;
			for (int position = 0; position < scanLength; position++)
			{
				FormulaValue key = vertical ? table[position, 0] : table[0, position];
				key = key ?? FormulaValue.BlankValue;

				int comparison = Evaluator.CompareValues(key, needle);
				if (comparison == 0)
				{
					best = position;
					break;
				}

				if (approximate && comparison < 0)
				{
					best = position;
				}
			}

			if (best < 0)
			{
				return FormulaValue.FromError(FormulaErrors.NotAvailable);
			}

			FormulaValue result = vertical ? table[best, offset - 1] : table[offset - 1, best];
			return result ?? FormulaValue.BlankValue;
		}

		private static FormulaValue ConcatenateAll(IReadOnlyList<FormulaValue> arguments)
		{
			StringBuilder builder = new StringBuilder();
			for (int index = 0; index < arguments.Count; index++)
			{
				foreach (FormulaValue item in arguments[index].Enumerate())
				{
					builder.Append(item.ToDisplayText());
				}
			}

			return FormulaValue.FromText(builder.ToString());
		}

		private static FormulaValue FindIn(
			IReadOnlyList<FormulaValue> arguments,
			StringComparison comparison)
		{
			if (arguments.Count < 2)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			string needle = TextOf(arguments, 0);
			string haystack = TextOf(arguments, 1);
			int start = CountArgument(arguments, 2, 1);
			if (start < 1 || start > haystack.Length + 1)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			int found = haystack.IndexOf(needle, start - 1, comparison);
			if (found < 0)
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return FormulaValue.FromNumber(found + 1);
		}

		private static FormulaValue DatePart(
			IReadOnlyList<FormulaValue> arguments,
			Func<DateTime, int> projection)
		{
			DateTime date;
			if (arguments.Count < 1 || !TryToDate(arguments[0], out date))
			{
				return FormulaValue.FromError(FormulaErrors.Value);
			}

			return FormulaValue.FromNumber(projection(date));
		}

		/// <summary>
		/// Converts a serial to a date via OLE automation dates. This agrees with Excel
		/// for every date from 1900-03-01 onward. Serials 1 and 60, which encode the
		/// historical 1900 leap-year defect, are outside the agreed range and are
		/// rejected rather than silently mapped.
		/// </summary>
		private static bool TryToDate(FormulaValue value, out DateTime date)
		{
			date = default(DateTime);
			double serial;
			if (!value.TryGetNumber(out serial))
			{
				return false;
			}

			if (serial < 61d || serial > 2958465d)
			{
				return false;
			}

			date = DateTime.FromOADate(serial);
			return true;
		}

		private static string TextOf(IReadOnlyList<FormulaValue> arguments, int index)
		{
			if (index >= arguments.Count)
			{
				return string.Empty;
			}

			return arguments[index].Scalar().ToDisplayText() ?? string.Empty;
		}

		private static int CountArgument(
			IReadOnlyList<FormulaValue> arguments,
			int index,
			int fallback)
		{
			double raw;
			if (index >= arguments.Count || !arguments[index].TryGetNumber(out raw))
			{
				return fallback;
			}

			return (int)Math.Truncate(raw);
		}

		private static string CollapseSpaces(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return string.Empty;
			}

			string[] parts = text.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			return string.Join(" ", parts);
		}
	}
}
