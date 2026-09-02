using System;
using System.Collections.Generic;

namespace KHZ.Office.Native
{
	/// <summary>Document families an engine may claim support for.</summary>
	public enum OfficeDocumentKind
	{
		Unknown = 0,
		Spreadsheet = 1,
		WordProcessing = 2,
		Presentation = 3,
		Portable = 4
	}

	/// <summary>
	/// Declared, inspectable capabilities of an engine.
	/// <para>
	/// This type exists because the previous contract
	/// (<c>src/khz_workstation/office/base.py</c>) had no capability negotiation:
	/// <c>OnlyOfficeDesktopEngine.convert_to_pdf</c> raises
	/// <c>NotImplementedError</c>, but the registry could still select it. The
	/// failure surfaced at export time instead of selection time.
	/// </para>
	/// </summary>
	public sealed class OfficeEngineCapabilities
	{
		public bool CanRead { get; set; }

		public bool CanWrite { get; set; }

		public bool CanRender { get; set; }

		public bool CanRecalculate { get; set; }

		public bool CanExportPdf { get; set; }

		/// <summary>
		/// True when parts the engine does not understand are carried through a
		/// read/write cycle byte-for-byte rather than regenerated from an internal
		/// model. Regenerating is how a round-trip silently drops structures the
		/// engine has no representation for.
		/// </summary>
		public bool PreservesUnknownParts { get; set; }

		/// <summary>True when the engine spawns a process outside this one.</summary>
		public bool RequiresExternalProcess { get; set; }

		/// <summary>
		/// True when the engine needs a listening or outbound socket. A policy layer
		/// must be able to read this and refuse the engine before it is used.
		/// </summary>
		public bool RequiresNetworkSocket { get; set; }

		public IReadOnlyCollection<OfficeDocumentKind> SupportedKinds { get; set; }

		public static OfficeEngineCapabilities None()
		{
			return new OfficeEngineCapabilities
			{
				SupportedKinds = new OfficeDocumentKind[0]
			};
		}
	}

	/// <summary>Identity and capabilities of a concrete engine.</summary>
	public sealed class OfficeEngineDescriptor
	{
		public string Id { get; set; }

		public string DisplayName { get; set; }

		/// <summary>
		/// Resolved engine version, or null when it genuinely cannot be determined.
		/// An in-process engine reports its own assembly version, so unlike the
		/// external adapters this is never null in practice.
		/// </summary>
		public string Version { get; set; }

		public bool InProcess { get; set; }

		public OfficeEngineCapabilities Capabilities { get; set; }
	}

	/// <summary>
	/// The engine boundary.
	/// <para>
	/// Compared with the Python <c>IOfficeEngine</c>, <c>open_for_edit(path) -&gt; pid</c>
	/// is intentionally absent. A process id is only meaningful for an out-of-process
	/// editor, so that signature made "launch an external application" part of the
	/// interface rather than one possible implementation of it.
	/// </para>
	/// </summary>
	public interface IOfficeEngine
	{
		OfficeEngineDescriptor Describe();

		bool CanHandle(OfficeDocumentKind kind);

		IOfficeDocument OpenRead(string path);
	}

	/// <summary>An opened document, independent of how it is stored.</summary>
	public interface IOfficeDocument : IDisposable
	{
		OfficeDocumentKind Kind { get; }

		string SourcePath { get; }

		/// <summary>Every part present in the source container.</summary>
		IReadOnlyList<string> PartNames { get; }

		/// <summary>
		/// Parts this engine does not model. They must survive <see cref="SaveAs"/>
		/// unchanged.
		/// </summary>
		IReadOnlyCollection<string> UnknownPartNames { get; }

		void SaveAs(string path);
	}
}
