using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace KHZ.Office.Native.Xlsx
{
	/// <summary>
	/// An OOXML package held as raw parts.
	/// <para>
	/// The point of this type is what it refuses to do: it never parses a part it was
	/// not asked about, and it never regenerates one. A part is either replaced
	/// explicitly or written back exactly as it was read, in its original position.
	/// That is what makes an unknown part survive a save.
	/// </para>
	/// </summary>
	public sealed class PreservingXlsxPackage
	{
		/// <summary>
		/// The zip format stores MS-DOS timestamps and cannot represent a year before
		/// 1980. Assigning an earlier value to LastWriteTime throws, so this is the
		/// fallback -- not the Unix epoch, which would.
		/// </summary>
		private static readonly DateTimeOffset DosEpoch =
			new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

		private readonly List<string> _order = new List<string>();

		private readonly Dictionary<string, byte[]> _parts =
			new Dictionary<string, byte[]>(StringComparer.Ordinal);

		private readonly Dictionary<string, DateTimeOffset> _timestamps =
			new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

		private readonly Dictionary<string, string> _originalHashes =
			new Dictionary<string, string>(StringComparer.Ordinal);

		private readonly HashSet<string> _modified = new HashSet<string>(StringComparer.Ordinal);

		private PreservingXlsxPackage(string sourcePath)
		{
			SourcePath = sourcePath;
		}

		public string SourcePath { get; }

		/// <summary>Part names in the order the container stored them.</summary>
		public IReadOnlyList<string> PartNames
		{
			get { return _order; }
		}

		/// <summary>SHA-256 of each part as it was read from disk.</summary>
		public IReadOnlyDictionary<string, string> OriginalHashes
		{
			get { return _originalHashes; }
		}

		/// <summary>Parts explicitly replaced since the package was opened.</summary>
		public IReadOnlyCollection<string> ModifiedParts
		{
			get { return _modified; }
		}

		public int PartCount
		{
			get { return _order.Count; }
		}

		public static PreservingXlsxPackage Open(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				throw new ArgumentNullException(nameof(path));
			}

			PreservingXlsxPackage package = new PreservingXlsxPackage(path);

			using (FileStream file = File.OpenRead(path))
			using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read))
			{
				foreach (ZipArchiveEntry entry in archive.Entries)
				{
					// A directory entry has an empty name and no content.
					if (string.IsNullOrEmpty(entry.Name))
					{
						continue;
					}

					byte[] content;
					using (Stream stream = entry.Open())
					using (MemoryStream buffer = new MemoryStream())
					{
						stream.CopyTo(buffer);
						content = buffer.ToArray();
					}

					string name = entry.FullName;
					if (!package._parts.ContainsKey(name))
					{
						package._order.Add(name);
					}

					package._parts[name] = content;
					package._originalHashes[name] = Sha256(content);

					DateTimeOffset stamp = entry.LastWriteTime;
					package._timestamps[name] = stamp < DosEpoch ? DosEpoch : stamp;
				}
			}

			return package;
		}

		public bool TryGetPart(string name, out byte[] content)
		{
			return _parts.TryGetValue(name ?? string.Empty, out content);
		}

		public byte[] GetPart(string name)
		{
			byte[] content;
			if (!_parts.TryGetValue(name ?? string.Empty, out content))
			{
				throw new FileNotFoundException("Package part not found: " + name);
			}

			return content;
		}

		public bool HasPart(string name)
		{
			return _parts.ContainsKey(name ?? string.Empty);
		}

		/// <summary>
		/// Replaces one part's bytes and records it as modified. Parts not passed here
		/// are guaranteed to be written back byte-for-byte.
		/// </summary>
		public void ReplacePart(string name, byte[] content)
		{
			if (string.IsNullOrEmpty(name))
			{
				throw new ArgumentNullException(nameof(name));
			}

			if (content == null)
			{
				throw new ArgumentNullException(nameof(content));
			}

			if (!_parts.ContainsKey(name))
			{
				_order.Add(name);
				_timestamps[name] = DosEpoch;
				_originalHashes[name] = string.Empty;
			}

			_parts[name] = content;
			_modified.Add(name);
		}

		/// <summary>SHA-256 of a part's current bytes.</summary>
		public string CurrentHash(string name)
		{
			return Sha256(GetPart(name));
		}

		/// <summary>True when a part's bytes are unchanged since the package was opened.</summary>
		public bool IsUnchanged(string name)
		{
			string original;
			if (!_originalHashes.TryGetValue(name ?? string.Empty, out original))
			{
				return false;
			}

			return string.Equals(original, CurrentHash(name), StringComparison.Ordinal);
		}

		/// <summary>
		/// Writes the package out, entries in their original order.
		/// </summary>
		public void Save(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				throw new ArgumentNullException(nameof(path));
			}

			string directory = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
			}

			using (FileStream file = File.Create(path))
			using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Create))
			{
				for (int index = 0; index < _order.Count; index++)
				{
					string name = _order[index];
					byte[] content = _parts[name];

					ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

					DateTimeOffset stamp;
					if (!_timestamps.TryGetValue(name, out stamp) || stamp < DosEpoch)
					{
						stamp = DosEpoch;
					}

					entry.LastWriteTime = stamp;

					using (Stream stream = entry.Open())
					{
						stream.Write(content, 0, content.Length);
					}
				}
			}
		}

		public static string Sha256(byte[] content)
		{
			using (SHA256 algorithm = SHA256.Create())
			{
				return Convert.ToHexString(algorithm.ComputeHash(content ?? new byte[0]))
					.ToLowerInvariant();
			}
		}

		public static string Sha256File(string path)
		{
			return Sha256(File.ReadAllBytes(path));
		}
	}
}
