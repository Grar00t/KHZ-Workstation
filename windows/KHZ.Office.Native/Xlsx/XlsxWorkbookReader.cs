using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using KHZ.Office.Native.Formula;

namespace KHZ.Office.Native.Xlsx
{
	public sealed class XlsxSheetPart
	{
		public string SheetName { get; set; }

		public string PartName { get; set; }
	}

	public sealed class XlsxReadResult
	{
		public WorkbookModel Model { get; set; }

		public List<XlsxSheetPart> SheetParts { get; } = new List<XlsxSheetPart>();

		/// <summary>
		/// Parts this reader actually opened and interpreted. Everything else in the
		/// package is, by definition, an unknown part that must survive a save.
		/// </summary>
		public HashSet<string> InterpretedParts { get; } =
			new HashSet<string>(StringComparer.Ordinal);

		public int DefinedNameCount { get; set; }

		public List<string> Warnings { get; } = new List<string>();
	}

	/// <summary>Reads cells and formulas out of a SpreadsheetML package.</summary>
	public static class XlsxWorkbookReader
	{
		private static readonly XNamespace Main =
			"http://schemas.openxmlformats.org/spreadsheetml/2006/main";

		private static readonly XNamespace DocRel =
			"http://schemas.openxmlformats.org/officeDocument/2006/relationships";

		private static readonly XNamespace PackageRel =
			"http://schemas.openxmlformats.org/package/2006/relationships";

		private const string WorkbookPart = "xl/workbook.xml";
		private const string WorkbookRelsPart = "xl/_rels/workbook.xml.rels";
		private const string SharedStringsPart = "xl/sharedStrings.xml";

		public static XlsxReadResult Read(PreservingXlsxPackage package)
		{
			if (package == null)
			{
				throw new ArgumentNullException(nameof(package));
			}

			XlsxReadResult result = new XlsxReadResult();
			WorkbookModel model = new WorkbookModel();
			result.Model = model;

			if (!package.HasPart(WorkbookPart))
			{
				result.Warnings.Add("Missing " + WorkbookPart + "; not a SpreadsheetML package.");
				return result;
			}

			List<string> sharedStrings = ReadSharedStrings(package, result);
			ReadSheetParts(package, result);

			for (int index = 0; index < result.SheetParts.Count; index++)
			{
				XlsxSheetPart sheetPart = result.SheetParts[index];
				WorkbookSheet sheet = new WorkbookSheet(sheetPart.SheetName);
				model.Sheets.Add(sheet);

				byte[] content;
				if (!package.TryGetPart(sheetPart.PartName, out content))
				{
					result.Warnings.Add(
						"Sheet '" + sheetPart.SheetName + "' targets missing part " + sheetPart.PartName);
					continue;
				}

				result.InterpretedParts.Add(sheetPart.PartName);
				ReadSheet(content, sheet, sharedStrings, result);
			}

			return result;
		}

		private static void ReadSheetParts(PreservingXlsxPackage package, XlsxReadResult result)
		{
			result.InterpretedParts.Add(WorkbookPart);
			XDocument workbook = LoadXml(package.GetPart(WorkbookPart));

			// Relationship id -> part name.
			Dictionary<string, string> targets =
				new Dictionary<string, string>(StringComparer.Ordinal);

			byte[] relsContent;
			if (package.TryGetPart(WorkbookRelsPart, out relsContent))
			{
				result.InterpretedParts.Add(WorkbookRelsPart);
				XDocument rels = LoadXml(relsContent);
				foreach (XElement relationship in rels.Descendants(PackageRel + "Relationship"))
				{
					XAttribute id = relationship.Attribute("Id");
					XAttribute target = relationship.Attribute("Target");
					if (id == null || target == null)
					{
						continue;
					}

					targets[id.Value] = NormalizeTarget(target.Value);
				}
			}
			else
			{
				result.Warnings.Add("Missing " + WorkbookRelsPart + "; sheet parts cannot be resolved.");
			}

			XElement definedNames = null;
			foreach (XElement candidate in workbook.Descendants(Main + "definedNames"))
			{
				definedNames = candidate;
				break;
			}

			if (definedNames != null)
			{
				foreach (XElement name in definedNames.Elements(Main + "definedName"))
				{
					result.DefinedNameCount++;
				}

				if (result.DefinedNameCount > 0)
				{
					// Not resolved in this spike. A formula referencing one evaluates to
					// #NAME? and is counted as a mismatch, which is the accurate outcome.
					result.Warnings.Add(
						result.DefinedNameCount.ToString(CultureInfo.InvariantCulture) +
						" defined name(s) present but not resolved by this spike.");
				}
			}

			int ordinal = 0;
			foreach (XElement sheet in workbook.Descendants(Main + "sheet"))
			{
				ordinal++;
				XAttribute nameAttribute = sheet.Attribute("name");
				XAttribute idAttribute = sheet.Attribute(DocRel + "id");

				string sheetName = nameAttribute != null
					? nameAttribute.Value
					: "Sheet" + ordinal.ToString(CultureInfo.InvariantCulture);

				string partName = null;
				if (idAttribute != null)
				{
					targets.TryGetValue(idAttribute.Value, out partName);
				}

				if (string.IsNullOrEmpty(partName))
				{
					result.Warnings.Add("Sheet '" + sheetName + "' has no resolvable relationship target.");
					continue;
				}

				result.SheetParts.Add(new XlsxSheetPart
				{
					SheetName = sheetName,
					PartName = partName
				});
			}
		}

		/// <summary>Resolves a relationship target to a package part name.</summary>
		private static string NormalizeTarget(string target)
		{
			string value = (target ?? string.Empty).Replace('\\', '/');
			if (value.Length == 0)
			{
				return value;
			}

			if (value[0] == '/')
			{
				return value.Substring(1);
			}

			if (value.StartsWith("../", StringComparison.Ordinal))
			{
				return value.Substring(3);
			}

			// Targets in xl/_rels/workbook.xml.rels are relative to xl/.
			return "xl/" + value;
		}

		private static List<string> ReadSharedStrings(
			PreservingXlsxPackage package,
			XlsxReadResult result)
		{
			List<string> strings = new List<string>();

			byte[] content;
			if (!package.TryGetPart(SharedStringsPart, out content))
			{
				return strings;
			}

			result.InterpretedParts.Add(SharedStringsPart);
			XDocument document = LoadXml(content);
			foreach (XElement item in document.Descendants(Main + "si"))
			{
				strings.Add(ReadTextRuns(item));
			}

			return strings;
		}

		private static void ReadSheet(
			byte[] content,
			WorkbookSheet sheet,
			List<string> sharedStrings,
			XlsxReadResult result)
		{
			XDocument document = LoadXml(content);

			foreach (XElement cellElement in document.Descendants(Main + "c"))
			{
				XAttribute referenceAttribute = cellElement.Attribute("r");
				if (referenceAttribute == null)
				{
					continue;
				}

				CellRef reference;
				if (!CellRef.TryParseA1(referenceAttribute.Value, out reference))
				{
					continue;
				}

				WorkbookCell cell = sheet.GetOrCreate(reference);
				cell.CachedValue = ReadCachedValue(cellElement, sharedStrings);

				XElement formula = cellElement.Element(Main + "f");
				if (formula == null)
				{
					continue;
				}

				string formulaText = formula.Value;
				XAttribute formulaType = formula.Attribute("t");
				bool shared = formulaType != null &&
					string.Equals(formulaType.Value, "shared", StringComparison.Ordinal);

				if (string.IsNullOrEmpty(formulaText))
				{
					// A shared-formula follower stores no text of its own. Translating the
					// master formula to this cell's offsets is not implemented, so it is
					// recorded as unresolved.
					cell.IsSharedFormulaFollower = shared;
					continue;
				}

				cell.FormulaText = formulaText;
			}
		}

		private static FormulaValue ReadCachedValue(XElement cellElement, List<string> sharedStrings)
		{
			XAttribute typeAttribute = cellElement.Attribute("t");
			string type = typeAttribute != null ? typeAttribute.Value : "n";

			if (string.Equals(type, "inlineStr", StringComparison.Ordinal))
			{
				XElement inline = cellElement.Element(Main + "is");
				return FormulaValue.FromText(inline != null ? ReadTextRuns(inline) : string.Empty);
			}

			XElement valueElement = cellElement.Element(Main + "v");
			if (valueElement == null)
			{
				return FormulaValue.BlankValue;
			}

			string raw = valueElement.Value;

			switch (type)
			{
				case "s":
				{
					int sharedIndex;
					if (int.TryParse(
							raw,
							NumberStyles.Integer,
							CultureInfo.InvariantCulture,
							out sharedIndex) &&
						sharedIndex >= 0 &&
						sharedIndex < sharedStrings.Count)
					{
						return FormulaValue.FromText(sharedStrings[sharedIndex]);
					}

					return FormulaValue.FromError(FormulaErrors.Value);
				}

				case "str":
					return FormulaValue.FromText(raw);
				case "b":
					return FormulaValue.FromLogical(string.Equals(raw, "1", StringComparison.Ordinal));
				case "e":
					return FormulaValue.FromError(raw);
				default:
				{
					double numeric;
					if (double.TryParse(
						raw,
						NumberStyles.Float,
						CultureInfo.InvariantCulture,
						out numeric))
					{
						return FormulaValue.FromNumber(numeric);
					}

					return raw.Length == 0
						? FormulaValue.BlankValue
						: FormulaValue.FromText(raw);
				}
			}
		}

		/// <summary>
		/// Concatenates text runs, skipping phonetic runs. Text inside rPh is a reading
		/// hint, not part of the string value.
		/// </summary>
		private static string ReadTextRuns(XElement parent)
		{
			StringBuilder builder = new StringBuilder();
			foreach (XElement text in parent.Descendants(Main + "t"))
			{
				bool phonetic = false;
				XElement ancestor = text.Parent;
				while (ancestor != null)
				{
					if (ancestor.Name == Main + "rPh")
					{
						phonetic = true;
						break;
					}

					ancestor = ancestor.Parent;
				}

				if (phonetic)
				{
					continue;
				}

				builder.Append(text.Value);
			}

			return builder.ToString();
		}

		private static XDocument LoadXml(byte[] content)
		{
			using (MemoryStream stream = new MemoryStream(content))
			{
				return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
			}
		}
	}
}
