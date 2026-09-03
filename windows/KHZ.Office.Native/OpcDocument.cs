// OpcDocument - an OPC package held as an ordered list of parts and their
// original bytes.
//
// This layer understands STORAGE, not content. That is deliberate, and it is
// the whole differentiator from LibreOffice (analysis section 5): a part whose
// XML we do not model is carried through untouched, so nothing is lost by
// being unrepresented. Measured failure being avoided: LibreOffice 25.2.3.2
// lost a TOC field in a single DOCX round trip and rewrote header/footer
// structure.
//
// Rule: what we do not understand, we do not touch.

using System;
using System.Collections.Generic;
using System.IO;
using KHZ.Office.Abstractions;

namespace KHZ.Office.Native
{
	/// <summary>
	/// An opened OPC package. Owns no process and no file handle after read;
	/// every part lives in memory as the exact bytes that were on disk.
	/// </summary>
	public sealed class OpcDocument : IOfficeDocument
	{
		private readonly List<string> _order = new List<string>();

		private readonly Dictionary<string, byte[]> _parts =
			new Dictionary<string, byte[]>(StringComparer.Ordinal);

		private readonly List<OfficeWarning> _warnings = new List<OfficeWarning>();

		private bool _disposed;

		internal OpcDocument(OfficeFormat format)
		{
			Format = format;
		}

		/// <inheritdoc />
		public OfficeFormat Format { get; internal set; }

		/// <summary>Part names in the order they appeared in the package.</summary>
		public IReadOnlyCollection<string> PartNames
		{
			get { return _order; }
		}

		/// <summary>
		/// Every part. The package layer models no XML semantics at all, so by
		/// its own honest accounting nothing here is understood - and therefore
		/// nothing here is altered.
		/// </summary>
		public IReadOnlyCollection<string> UnmodelledPartNames
		{
			get { return _order; }
		}

		/// <inheritdoc />
		public IReadOnlyList<OfficeWarning> Warnings
		{
			get { return _warnings; }
		}

		/// <inheritdoc />
		public Stream OpenPart(string partName)
		{
			ThrowIfDisposed();

			if (partName is null)
			{
				throw new ArgumentNullException(nameof(partName));
			}

			byte[]? bytes;
			if (!_parts.TryGetValue(partName, out bytes))
			{
				throw new KeyNotFoundException("No part named '" + partName + "'.");
			}

			return new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: false);
		}

		/// <summary>
		/// Replaces the bytes of one part, keeping its position in the package.
		/// This is the only sanctioned mutation: a caller that understands a
		/// specific part rewrites that part and nothing else.
		/// </summary>
		public void ReplacePart(string partName, byte[] content)
		{
			ThrowIfDisposed();

			if (partName is null)
			{
				throw new ArgumentNullException(nameof(partName));
			}

			if (content is null)
			{
				throw new ArgumentNullException(nameof(content));
			}

			if (!_parts.ContainsKey(partName))
			{
				throw new KeyNotFoundException(
					"No part named '" + partName + "'. Adding parts is not supported by the package layer.");
			}

			_parts[partName] = content;
		}

		internal IReadOnlyList<string> Order
		{
			get { return _order; }
		}

		internal bool TryAddPart(string partName, byte[] content)
		{
			if (_parts.ContainsKey(partName))
			{
				return false;
			}

			_parts.Add(partName, content);
			_order.Add(partName);
			return true;
		}

		internal byte[]? TryGetBytes(string partName)
		{
			byte[]? bytes;
			return _parts.TryGetValue(partName, out bytes) ? bytes : null;
		}

		internal void AddWarning(OfficeWarning warning)
		{
			_warnings.Add(warning);
		}

		private void ThrowIfDisposed()
		{
			if (_disposed)
			{
				throw new ObjectDisposedException(nameof(OpcDocument));
			}
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (_disposed)
			{
				return;
			}

			_parts.Clear();
			_order.Clear();
			_disposed = true;
		}
	}
}
