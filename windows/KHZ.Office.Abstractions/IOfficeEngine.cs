// KHZ.Office.Abstractions - the in-process office engine boundary.
//
// Ordering rule (KHZ-Workstation analysis, section 6): this interface is
// redesigned BEFORE any adapter. Reversing the order means writing twice.
//
// The Python IOfficeEngine exposed three methods and open_for_edit returned a
// PID. That signature structurally assumes an external editor, which makes an
// in-process engine impossible behind it. Nothing in this file may return a
// process handle, a PID, or a path to an executable.
//
// Preservation rule (section 5): what we do not understand, we do not touch.
// Parts of the package that an engine does not model are carried through byte
// for byte. LibreOffice does the opposite and loses what it cannot represent
// (measured: a TOC field lost in a single DOCX round trip).
//
// Capability rule (section 6): onlyoffice.py threw NotImplementedError from
// convert_to_pdf and declared no capabilities, so it could be selected and
// then explode on the first PDF call. An engine here MUST declare what it can
// do before it is selected.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace KHZ.Office.Abstractions
{
	/// <summary>
	/// What an engine declares it can do, before it is selected. Callers must
	/// check this instead of discovering a gap by exception at run time.
	/// </summary>
	[Flags]
	public enum OfficeCapability
	{
		None = 0,

		/// <summary>Can open a package and expose its parts.</summary>
		Read = 1 << 0,

		/// <summary>Can serialise a document back to a stream.</summary>
		Write = 1 << 1,

		/// <summary>Can produce a visual surface for a page or sheet.</summary>
		Render = 1 << 2,

		/// <summary>Can recalculate formulas.</summary>
		Recalculate = 1 << 3,

		/// <summary>
		/// Unmodelled parts survive a read/write cycle byte for byte. An engine
		/// that rebuilds the package on save MUST NOT declare this.
		/// </summary>
		PreserveUnknownParts = 1 << 4,
	}

	/// <summary>Container format, decided by inspection, never by file extension alone.</summary>
	public enum OfficeFormat
	{
		Unknown = 0,
		SpreadsheetOpenXml = 1,
		WordprocessingOpenXml = 2,
		PresentationOpenXml = 3,
	}

	/// <summary>
	/// A concern raised while reading. Warnings are collected and returned, never
	/// swallowed and never promoted to silent defaults (section 9: checking cost
	/// zero and was skipped anyway).
	/// </summary>
	public sealed class OfficeWarning
	{
		public OfficeWarning(string code, string message, string? partName = null)
		{
			Code = code ?? throw new ArgumentNullException(nameof(code));
			Message = message ?? throw new ArgumentNullException(nameof(message));
			PartName = partName;
		}

		/// <summary>Stable machine-readable code. Prose is not a gate.</summary>
		public string Code { get; }

		public string Message { get; }

		/// <summary>Package-relative part name, when the concern is local to a part.</summary>
		public string? PartName { get; }
	}

	/// <summary>
	/// An opened document. Owns no process. Disposing releases only memory and
	/// file handles.
	/// </summary>
	public interface IOfficeDocument : IDisposable
	{
		OfficeFormat Format { get; }

		/// <summary>Every part name found in the package, modelled or not.</summary>
		IReadOnlyCollection<string> PartNames { get; }

		/// <summary>
		/// Part names this engine does not model. These must be reproduced exactly
		/// on write when <see cref="OfficeCapability.PreserveUnknownParts"/> is
		/// declared.
		/// </summary>
		IReadOnlyCollection<string> UnmodelledPartNames { get; }

		/// <summary>
		/// Opens a part for reading. The returned stream is seekable and
		/// independent; the caller disposes it.
		/// </summary>
		/// <exception cref="KeyNotFoundException">No such part.</exception>
		Stream OpenPart(string partName);

		IReadOnlyList<OfficeWarning> Warnings { get; }
	}

	public sealed class OfficeReadOptions
	{
		/// <summary>
		/// Retain the original bytes of every part so a write can reproduce
		/// unmodelled parts exactly. Costs memory proportional to the file.
		/// </summary>
		public bool RetainOriginalBytes { get; set; } = true;

		/// <summary>
		/// Recalculate on read. Off by default: a stored value is evidence, a
		/// recalculated one is a claim.
		/// </summary>
		public bool RecalculateOnRead { get; set; }
	}

	public sealed class OfficeWriteOptions
	{
		/// <summary>
		/// Fail the write instead of dropping a part the engine cannot reproduce.
		/// A broken workbook must not be allowed to look intact (section 7).
		/// </summary>
		public bool FailOnPartLoss { get; set; } = true;
	}

	public sealed class OfficeReadResult
	{
		public OfficeReadResult(IOfficeDocument document)
		{
			Document = document ?? throw new ArgumentNullException(nameof(document));
		}

		public IOfficeDocument Document { get; }
	}

	/// <summary>
	/// A rendered surface as raw pixels. No window, no browser control, no
	/// external process. Section 4: WebView2 is the only real telemetry and
	/// auto-update carrier in the tree, so the boundary must not require it.
	/// </summary>
	public sealed class OfficeRenderResult
	{
		public OfficeRenderResult(int widthPx, int heightPx, ReadOnlyMemory<byte> bgra32)
		{
			if (widthPx <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(widthPx));
			}

			if (heightPx <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(heightPx));
			}

			WidthPx = widthPx;
			HeightPx = heightPx;
			Bgra32 = bgra32;
		}

		public int WidthPx { get; }

		public int HeightPx { get; }

		/// <summary>Tightly packed BGRA, stride = WidthPx * 4.</summary>
		public ReadOnlyMemory<byte> Bgra32 { get; }
	}

	/// <summary>
	/// The office engine boundary. Four concerns: Read, Write, Render,
	/// Capabilities. No Open, no OpenForEdit, no PID, no executable path.
	/// </summary>
	public interface IOfficeEngine
	{
		/// <summary>Stable identifier for logs and acceptance output.</summary>
		string EngineId { get; }

		/// <summary>
		/// Declared up front. An engine MUST throw <see cref="NotSupportedException"/>
		/// from any method whose capability it did not declare, and MUST NOT be
		/// selected for work it did not declare.
		/// </summary>
		OfficeCapability Capabilities { get; }

		/// <summary>Formats this engine accepts.</summary>
		IReadOnlyCollection<OfficeFormat> SupportedFormats { get; }

		Task<OfficeReadResult> ReadAsync(
			Stream source,
			OfficeReadOptions options,
			CancellationToken cancellationToken);

		Task WriteAsync(
			IOfficeDocument document,
			Stream destination,
			OfficeWriteOptions options,
			CancellationToken cancellationToken);

		Task<OfficeRenderResult> RenderAsync(
			IOfficeDocument document,
			int surfaceIndex,
			double scale,
			CancellationToken cancellationToken);
	}
}
