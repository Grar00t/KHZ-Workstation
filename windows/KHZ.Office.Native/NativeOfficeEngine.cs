// NativeOfficeEngine - the package layer. Original code, no third-party
// source, no MPL, no LGPL. An OPC container is a ZIP, and System.IO.Compression
// is in the base class library, so this needs no dependency at all.
//
// Declares Read, Write and PreserveUnknownParts. Does NOT declare Render or
// Recalculate, and refuses both. Analysis section 6: onlyoffice.py threw from
// convert_to_pdf while declaring no capabilities, so it could be selected and
// then explode on the first call. Capabilities exist to make that impossible.
//
// Sections 5 and 7: layout and Word conformance are never promised here.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using KHZ.Office.Abstractions;

namespace KHZ.Office.Native
{
	/// <summary>
	/// Reads and writes OPC packages with byte-for-byte preservation of every
	/// part it was not asked to change.
	/// </summary>
	public sealed class NativeOfficeEngine : IOfficeEngine
	{
		private const string ContentTypesPart = "[Content_Types].xml";

		private const int CopyBufferSize = 81920;

		private static readonly OfficeFormat[] Formats =
		{
			OfficeFormat.SpreadsheetOpenXml,
			OfficeFormat.WordprocessingOpenXml,
			OfficeFormat.PresentationOpenXml,
			OfficeFormat.Unknown,
		};

		/// <inheritdoc />
		public string EngineId
		{
			get { return "khz.native.opc"; }
		}

		/// <inheritdoc />
		public OfficeCapability Capabilities
		{
			get
			{
				return OfficeCapability.Read
					| OfficeCapability.Write
					| OfficeCapability.PreserveUnknownParts;
			}
		}

		/// <inheritdoc />
		public IReadOnlyCollection<OfficeFormat> SupportedFormats
		{
			get { return Formats; }
		}

		/// <inheritdoc />
		public async Task<OfficeReadResult> ReadAsync(
			Stream source,
			OfficeReadOptions options,
			CancellationToken cancellationToken)
		{
			if (source is null)
			{
				throw new ArgumentNullException(nameof(source));
			}

			if (options is null)
			{
				throw new ArgumentNullException(nameof(options));
			}

			OpcDocument document = new OpcDocument(OfficeFormat.Unknown);

			if (!options.RetainOriginalBytes)
			{
				document.AddWarning(new OfficeWarning(
					"KHZ-OPC-001",
					"RetainOriginalBytes was false and was ignored. The package layer always retains "
					+ "original bytes, because byte-for-byte preservation is impossible without them."));
			}

			if (options.RecalculateOnRead)
			{
				document.AddWarning(new OfficeWarning(
					"KHZ-OPC-004",
					"RecalculateOnRead was true and was ignored. This engine does not declare "
					+ "Recalculate. Stored values are returned untouched."));
			}

			using (MemoryStream buffer = new MemoryStream())
			{
				await source.CopyToAsync(buffer, CopyBufferSize, cancellationToken).ConfigureAwait(false);
				buffer.Position = 0;

				bool sawContentTypes = false;

				using (ZipArchive archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true))
				{
					foreach (ZipArchiveEntry entry in archive.Entries)
					{
						cancellationToken.ThrowIfCancellationRequested();

						string name = entry.FullName;

						if (name.Length == 0 || name.EndsWith("/", StringComparison.Ordinal))
						{
							document.AddWarning(new OfficeWarning(
								"KHZ-OPC-005",
								"Directory entry present in the container and not carried over. "
								+ "It is not an OPC part, but the container is therefore not identical.",
								name));
							continue;
						}

						byte[] bytes;
						using (Stream entryStream = entry.Open())
						using (MemoryStream partBuffer = new MemoryStream())
						{
							await entryStream.CopyToAsync(partBuffer, CopyBufferSize, cancellationToken)
								.ConfigureAwait(false);
							bytes = partBuffer.ToArray();
						}

						if (!document.TryAddPart(name, bytes))
						{
							document.AddWarning(new OfficeWarning(
								"KHZ-OPC-003",
								"Duplicate part name in the container. The first occurrence was kept.",
								name));
							continue;
						}

						if (string.Equals(name, ContentTypesPart, StringComparison.Ordinal))
						{
							sawContentTypes = true;
						}
					}
				}

				if (!sawContentTypes)
				{
					document.AddWarning(new OfficeWarning(
						"KHZ-OPC-002",
						"[Content_Types].xml is absent. This is not a valid OPC package."));
				}
			}

			document.Format = DetectFormat(document);
			return new OfficeReadResult(document);
		}

		/// <inheritdoc />
		public Task WriteAsync(
			IOfficeDocument document,
			Stream destination,
			OfficeWriteOptions options,
			CancellationToken cancellationToken)
		{
			if (document is null)
			{
				throw new ArgumentNullException(nameof(document));
			}

			if (destination is null)
			{
				throw new ArgumentNullException(nameof(destination));
			}

			if (options is null)
			{
				throw new ArgumentNullException(nameof(options));
			}

			OpcDocument? opc = document as OpcDocument;
			if (opc is null)
			{
				throw new NotSupportedException(
					"This engine writes only documents it produced. A foreign IOfficeDocument cannot "
					+ "guarantee byte-for-byte part preservation, so writing one would be a silent claim.");
			}

			using (ZipArchive archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
			{
				foreach (string name in opc.Order)
				{
					cancellationToken.ThrowIfCancellationRequested();

					byte[]? bytes = opc.TryGetBytes(name);
					if (bytes is null)
					{
						if (options.FailOnPartLoss)
						{
							throw new InvalidDataException(
								"Part '" + name + "' has no content. Refusing to write a package that "
								+ "would look intact while missing a part.");
						}

						continue;
					}

					ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
					using (Stream entryStream = entry.Open())
					{
						entryStream.Write(bytes, 0, bytes.Length);
					}
				}
			}

			return Task.CompletedTask;
		}

		/// <inheritdoc />
		public Task<OfficeRenderResult> RenderAsync(
			IOfficeDocument document,
			int surfaceIndex,
			double scale,
			CancellationToken cancellationToken)
		{
			throw new NotSupportedException(
				"Render is not declared in Capabilities and is not implemented. Layout and Word "
				+ "conformance are twenty years of reverse engineering and are never promised here.");
		}

		private static OfficeFormat DetectFormat(OpcDocument document)
		{
			foreach (string name in document.PartNames)
			{
				if (string.Equals(name, "xl/workbook.xml", StringComparison.Ordinal))
				{
					return OfficeFormat.SpreadsheetOpenXml;
				}

				if (string.Equals(name, "word/document.xml", StringComparison.Ordinal))
				{
					return OfficeFormat.WordprocessingOpenXml;
				}

				if (string.Equals(name, "ppt/presentation.xml", StringComparison.Ordinal))
				{
					return OfficeFormat.PresentationOpenXml;
				}
			}

			return OfficeFormat.Unknown;
		}
	}
}
