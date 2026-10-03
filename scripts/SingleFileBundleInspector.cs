// Read-only .NET bundle v6 inspection. Compatible with Windows PowerShell Add-Type.
// Layout references: dotnet/runtime v10.0.12 HostModel Bundle/{Manifest,FileEntry}.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace SimpleScraper.Packaging
{
    public sealed class BundleEntry
    {
        public string Path { get; set; }
        public int Type { get; set; }
        public string TypeName { get; set; }
        public long Offset { get; set; }
        public long OriginalBytes { get; set; }
        public long CompressedBytes { get; set; }
        public long StoredBytes { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class BundleInfo
    {
        public string Executable { get; set; }
        public string ExecutableSha256 { get; set; }
        public long ExecutableBytes { get; set; }
        public string Version { get; set; }
        public string BundleId { get; set; }
        public long HeaderOffset { get; set; }
        public long ManifestBytes { get; set; }
        public ulong Flags { get; set; }
        public bool ExtractAllContent { get; set; }
        public long OriginalPayloadBytes { get; set; }
        public long StoredPayloadBytes { get; set; }
        public List<BundleEntry> Entries { get; set; }
    }

    public static class BundleInspector
    {
        private static readonly byte[] Signature = new byte[] {
            0x8b,0x12,0x02,0xb9,0x6a,0x61,0x20,0x38,0x72,0x7b,0x93,0x02,0x14,0xd7,0xa0,0x32,
            0x13,0xf5,0xb9,0xe6,0xef,0xae,0x33,0x18,0xee,0x3b,0x2d,0xce,0x24,0xb3,0x6a,0xae };
        private static readonly string[] TypeNames = new string[] { "Other", "Assembly", "NativeBinary", "DepsJson", "RuntimeConfigJson", "Symbols" };
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string HashFile(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return Hex(sha.ComputeHash(input));
        }

        public static string HashBytes(byte[] data)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(data));
        }

        private static string Hex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }

        private static byte[] FromHex(string value)
        {
            var result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; ++i) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }

        private static string ReadString(BinaryReader reader)
        {
            int count = 0;
            int shift = 0;
            for (int i = 0; i < 5; ++i)
            {
                byte value = reader.ReadByte();
                if (i == 4 && value > 7) throw new InvalidDataException("Invalid bundle string length.");
                count |= (value & 127) << shift;
                if (value < 128)
                {
                    if (count > 32768) throw new InvalidDataException("Bundle path is too long.");
                    byte[] text = reader.ReadBytes(count);
                    if (text.Length != count) throw new EndOfStreamException();
                    return StrictUtf8.GetString(text);
                }
                shift += 7;
            }
            throw new InvalidDataException("Invalid bundle string length.");
        }

        private static string ValidatePath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 1024 || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0 || path[0] == '/')
                throw new InvalidDataException("Unsafe bundle path: " + path);
            foreach (string part in path.Split('/'))
            {
                if (part == "" || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Unsafe bundle path: " + path);
            }
            return path;
        }

        private static string HashEntry(byte[] image, BundleEntry entry)
        {
            using (var input = new MemoryStream(image, checked((int)entry.Offset), checked((int)entry.StoredBytes), false))
            using (var sha = SHA256.Create())
            {
                Stream content = input;
                DeflateStream decompressor = null;
                if (entry.CompressedBytes != 0)
                {
                    decompressor = new DeflateStream(input, CompressionMode.Decompress, true);
                    content = decompressor;
                }
                try
                {
                    var buffer = new byte[81920];
                    long length = 0;
                    int read;
                    while ((read = content.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        length = checked(length + read);
                        if (length > entry.OriginalBytes) throw new InvalidDataException("Decompressed entry exceeds its declared length: " + entry.Path);
                        sha.TransformBlock(buffer, 0, read, buffer, 0);
                    }
                    if (length != entry.OriginalBytes) throw new InvalidDataException("Entry length mismatch: " + entry.Path);
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    return Hex(sha.Hash);
                }
                finally { if (decompressor != null) decompressor.Dispose(); }
            }
        }

        public static BundleInfo Inspect(string executable)
        {
            byte[] image = File.ReadAllBytes(executable);
            int marker = -1;
            for (int i = 8; i <= image.Length - Signature.Length; ++i)
            {
                if (image[i] != Signature[0]) continue;
                bool equal = true;
                for (int n = 1; n < Signature.Length; ++n) if (image[i + n] != Signature[n]) { equal = false; break; }
                if (equal)
                {
                    if (marker >= 0) throw new InvalidDataException("Multiple bundle signatures found.");
                    marker = i;
                }
            }
            if (marker < 8) throw new InvalidDataException("The executable has no .NET single-file bundle.");
            long header = BitConverter.ToInt64(image, marker - 8);
            if (header <= marker + Signature.Length || header > image.LongLength - 65) throw new InvalidDataException("Invalid bundle header offset.");
            var info = new BundleInfo { Executable = System.IO.Path.GetFullPath(executable), ExecutableBytes = image.LongLength,
                ExecutableSha256 = HashBytes(image), HeaderOffset = header, ManifestBytes = image.LongLength - header,
                Entries = new List<BundleEntry>() };
            using (var stream = new MemoryStream(image, false))
            using (var reader = new BinaryReader(stream, StrictUtf8))
            using (var combined = SHA256.Create())
            {
                stream.Position = header;
                uint major = reader.ReadUInt32();
                uint minor = reader.ReadUInt32();
                int count = reader.ReadInt32();
                if (major != 6 || minor != 0 || count < 1 || count > 100000) throw new InvalidDataException("Unsupported or invalid bundle header.");
                info.Version = major + "." + minor;
                info.BundleId = ReadString(reader);
                long depsOffset = reader.ReadInt64();
                long depsSize = reader.ReadInt64();
                long configOffset = reader.ReadInt64();
                long configSize = reader.ReadInt64();
                info.Flags = reader.ReadUInt64();
                if ((info.Flags & ~1UL) != 0) throw new InvalidDataException("Unknown bundle flags.");
                info.ExtractAllContent = (info.Flags & 1UL) != 0;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ranges = new List<BundleEntry>();
                int depsCount = 0, configCount = 0;
                for (int i = 0; i < count; ++i)
                {
                    var entry = new BundleEntry { Offset = reader.ReadInt64(), OriginalBytes = reader.ReadInt64(), CompressedBytes = reader.ReadInt64(), Type = reader.ReadByte() };
                    entry.Path = ValidatePath(ReadString(reader));
                    entry.StoredBytes = entry.CompressedBytes == 0 ? entry.OriginalBytes : entry.CompressedBytes;
                    if (!paths.Add(entry.Path)) throw new InvalidDataException("Case-insensitive duplicate bundle path: " + entry.Path);
                    if (entry.Type < 0 || entry.Type >= TypeNames.Length || entry.Offset < marker + Signature.Length || entry.OriginalBytes < 0 || entry.OriginalBytes > 536870912L ||
                        entry.CompressedBytes < 0 || entry.StoredBytes < 0 || entry.Offset > header || entry.StoredBytes > header - entry.Offset)
                        throw new InvalidDataException("Invalid bundle entry bounds: " + entry.Path);
                    entry.TypeName = TypeNames[entry.Type];
                    entry.Sha256 = HashEntry(image, entry);
                    byte[] hash = FromHex(entry.Sha256);
                    combined.TransformBlock(hash, 0, hash.Length, hash, 0);
                    if (entry.Type == 3) { ++depsCount; if (entry.Offset != depsOffset || entry.OriginalBytes != depsSize || entry.CompressedBytes != 0) throw new InvalidDataException("Invalid dependency manifest location."); }
                    if (entry.Type == 4) { ++configCount; if (entry.Offset != configOffset || entry.OriginalBytes != configSize || entry.CompressedBytes != 0) throw new InvalidDataException("Invalid runtime configuration location."); }
                    info.OriginalPayloadBytes = checked(info.OriginalPayloadBytes + entry.OriginalBytes);
                    info.StoredPayloadBytes = checked(info.StoredPayloadBytes + entry.StoredBytes);
                    info.Entries.Add(entry);
                    ranges.Add(entry);
                }
                if (stream.Position != image.LongLength || depsCount != 1 || configCount != 1) throw new InvalidDataException("Bundle manifest boundaries / required runtime records are invalid.");
                ranges.Sort(delegate(BundleEntry a, BundleEntry b) { return a.Offset.CompareTo(b.Offset); });
                long end = 0;
                foreach (var entry in ranges) { if (entry.Offset < end) throw new InvalidDataException("Overlapping bundle entries."); end = entry.Offset + entry.StoredBytes; }
                combined.TransformFinalBlock(new byte[0], 0, 0);
                string actualId = Convert.ToBase64String(combined.Hash).Replace('+', '-').Replace('/', '_').TrimEnd('=').Substring(0, 12);
                if (actualId != info.BundleId) throw new InvalidDataException("BundleID content hash mismatch.");
            }
            return info;
        }

        public static byte[] ReadEntryContent(string executable, BundleEntry entry)
        {
            using (var file = File.OpenRead(executable))
            {
                file.Position = entry.Offset;
                byte[] stored = new byte[checked((int)entry.StoredBytes)];
                int position = 0;
                while (position < stored.Length)
                {
                    int count = file.Read(stored, position, stored.Length - position);
                    if (count == 0) throw new EndOfStreamException();
                    position += count;
                }
                if (entry.CompressedBytes == 0) return stored;
                using (var input = new MemoryStream(stored, false))
                using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int count;
                    while ((count = inflater.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        if (output.Length + count > entry.OriginalBytes) throw new InvalidDataException("Invalid decompressed length.");
                        output.Write(buffer, 0, count);
                    }
                    if (output.Length != entry.OriginalBytes) throw new InvalidDataException("Invalid decompressed length.");
                    return output.ToArray();
                }
            }
        }
    }
}
