using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KHZ.Office.Native.Formula;

namespace KHZ.Office.Native.Xlsx
{
	/// <summary>
	/// Makes a minimal edit to a sheet part.
	/// <para>
	/// Everything outside the single targeted cell keeps its original XML, including
	/// whitespace, attribute order and elements this code has no model for.
	/// </para>
	/// </summary>
	public static class XlsxWorkbookWriter
	{
		private static readonly XNamespace Main =
			"http://schemas.openxmlformats.org/spreadsheetml/2006/main";

		/// <summary>
		/// Writes a literal number into one cell, clearing any formula and type marker
		/// that cell previously carried.
		/// </summary>
		public static byte[] SetNumericCell(byte[] sheetXml, CellRef target, double value)
		{
			if (sheetXml == null)
			{
				throw new ArgumentNullException(nameof(sheetXml));
			}

			if (!target.IsValid)
			{
				throw new ArgumentOutOfRangeException(nameof(target));
			}

			XDocument document = LoadXml(sheetXml);
			XElement worksheet = document.Root;
			if (worksheet == null)
			{
				throw new InvalidDataException("Sheet part has no root element.");
			}

			XElement sheetData = worksheet.Element(Main + "sheetData");
			if (sheetData == null)
			{
				sheetData = new XElement(Main + "sheetData");
				worksheet.Add(sheetData);
			}

			XElement row = FindOrCreateRow(sheetData, target.Row);
			XElement cell = FindOrCreateCell(row, target);

			// A numeric literal carries no t attribute, and must not keep a stale formula.
			XAttribute type = cell.Attribute("t");
			if (type != null)
			{
				type.Remove();
			}

			XElement formula = cell.Element(Main + "f");
			if (formula != null)
			{
				formula.Remove();
			}

			XElement valueElement = cell.Element(Main + "v");
			if (valueElement == null)
			{
				valueElement = new XElement(Main + "v");
				cell.Add(valueElement);
			}

			// "R" round-trips the double exactly.
			valueElement.Value = value.ToString("R", CultureInfo.InvariantCulture);

			return Serialize(document);
		}

		private static XElement FindOrCreateRow(XElement sheetData, int rowNumber)
		{
			XElement insertBefore = null;
			foreach (XElement candidate in sheetData.Elements(Main + "row"))
			{
				XAttribute attribute = candidate.Attribute("r");
				if (attribute == null)
				{
					continue;
				}

				int number;
				if (!int.TryParse(
					attribute.Value,
					NumberStyles.Integer,
					CultureInfo.InvariantCulture,
					out number))
				{
					continue;
				}

				if (number == rowNumber)
				{
					return candidate;
				}

				if (number > rowNumber)
				{
					insertBefore = candidate;
					break;
				}
			}

			XElement row = new XElement(
				Main + "row",
				new XAttribute("r", rowNumber.ToString(CultureInfo.InvariantCulture)));

			if (insertBefore != null)
			{
				insertBefore.AddBeforeSelf(row);
			}
			else
			{
				sheetData.Add(row);
			}

			return row;
		}

		private static XElement FindOrCreateCell(XElement row, CellRef target)
		{
			string reference = target.ToA1();
			XElement insertBefore = null;

			foreach (XElement candidate in row.Elements(Main + "c"))
			{
				XAttribute attribute = candidate.Attribute("r");
				if (attribute == null)
				{
					continue;
				}

				if (string.Equals(attribute.Value, reference, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}

				CellRef parsed;
				if (!CellRef.TryParseA1(attribute.Value, out parsed))
				{
					continue;
				}

				if (parsed.Column > target.Column)
				{
					insertBefore = candidate;
					break;
				}
			}

			XElement cell = new XElement(Main + "c", new XAttribute("r", reference));
			if (insertBefore != null)
			{
				insertBefore.AddBeforeSelf(cell);
			}
			else
			{
				row.Add(cell);
			}

			return cell;
		}

		private static XDocument LoadXml(byte[] content)
		{
			using (MemoryStream stream = new MemoryStream(content))
			{
				return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
			}
		}

		private static byte[] Serialize(XDocument document)
		{
			XmlWriterSettings settings = new XmlWriterSettings
			{
				// No BOM: Excel writes the declaration without one.
				Encoding = new UTF8Encoding(false),
				Indent = false,
				OmitXmlDeclaration = false
			};

			using (MemoryStream stream = new MemoryStream())
			{
				using (XmlWriter writer = XmlWriter.Create(stream, settings))
				{
					document.Save(writer);
				}

				return stream.ToArray();
			}
		}
	}
}
