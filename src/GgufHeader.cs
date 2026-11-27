using System;
using System.IO;
using System.Text;

namespace AIMeter
{
    /// <summary>
    /// The few facts AI Meter reads from the header of a GGUF model file: its architecture, the sizes that give the
    /// key-value cache its weight, and whether it is a mixture of experts. The file is someone else's data: every
    /// length is bounded before it is used, nothing is allocated by a size the file declares beyond those bounds, and
    /// reading stops at a fixed amount, so that a damaged or hostile file costs little and returns nothing.
    /// </summary>
    internal sealed class GgufHeader
    {
        public string Architecture;
        public int Blocks, Embedding, Heads, HeadsKv, KeyLength, ValueLength;
        public int Experts, ExpertsUsed;   // Zero for a model that is not a mixture of experts.

        /// <summary>The metadata comes first in the file; past this much read, what is wanted is not there.</summary>
        private const long MostBytes = 64L << 20;
        private const int LongestKey = 256, LongestText = 256, MostPairs = 100000;

        /// <summary>
        /// Bytes the key-value cache takes for the given number of tokens, as an estimate: one key and one value per
        /// token, layer and key-value head, at two bytes a number (16-bit floats, llama.cpp's default). Zero when the
        /// header lacks a size.
        /// </summary>
        public long KvBytes(long tokens)
        {
            int heads = HeadsKv > 0 ? HeadsKv : Heads;
            int key = KeyLength > 0 ? KeyLength : Heads > 0 ? Embedding / Heads : 0;
            int value = ValueLength > 0 ? ValueLength : key;
            if (tokens <= 0 || Blocks <= 0 || heads <= 0 || key <= 0) return 0;
            return tokens * Blocks * heads * (key + value) * 2;
        }

        /// <summary>The header of a GGUF file (version 2 or 3, little-endian), or null for any other or unreadable file.</summary>
        public static GgufHeader Read(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
                using (var reader = new Source(stream))
                {
                    if (reader.Length < 24 || reader.ReadUInt32() != 0x46554747) return null;   // "GGUF"
                    uint version = reader.ReadUInt32();
                    if (version != 2 && version != 3) return null;
                    reader.ReadUInt64();   // Tensors.
                    ulong pairs = reader.ReadUInt64();
                    if (pairs > MostPairs) return null;
                    var header = new GgufHeader();
                    for (ulong i = 0; i < pairs && stream.Position < MostBytes; i++)
                    {
                        string key = Text(reader, LongestKey);
                        if (string.IsNullOrEmpty(key)) return null;   // No key, or one longer than any real one: not a header to trust.
                        uint type = reader.ReadUInt32();
                        if (!header.Take(key, type, reader) && !Skip(reader, type, 0)) return null;
                    }
                    return header.Architecture == null ? null : header;
                }
            }
            catch (IOException) { return null; }   // Also a file that ends before its header does.
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (OverflowException) { return null; }   // A count times a size that no file can hold.
        }

        /// <summary>Keeps the value when the key is one of those read; false leaves the value unread, to be skipped.</summary>
        private bool Take(string key, uint type, Source reader)
        {
            if (key == "general.architecture" && type == 8)
            {
                Architecture = Text(reader, LongestText) ?? "";
                return true;
            }
            // The model's own keys carry its architecture as a prefix: "llama.block_count", "qwen3moe.expert_count".
            int dot = key.IndexOf('.');
            string name = dot < 0 ? "" : key.Substring(dot + 1);
            // Head counts given per layer (an array) are left unread: without one figure there is no estimate.
            long? number = Array.IndexOf(SizeNames, name) < 0 ? null : Number(reader, type);
            if (number == null) return false;
            int value = number.Value < 0 || number.Value > int.MaxValue ? 0 : (int)number.Value;
            switch (name)
            {
                case "block_count": Blocks = value; break;
                case "embedding_length": Embedding = value; break;
                case "attention.head_count": Heads = value; break;
                case "attention.head_count_kv": HeadsKv = value; break;
                case "attention.key_length": KeyLength = value; break;
                case "attention.value_length": ValueLength = value; break;
                case "expert_count": Experts = value; break;
                case "expert_used_count": ExpertsUsed = value; break;
                default: throw new InvalidOperationException("A name among those read has no field: " + name);
            }
            return true;
        }

        /// <summary>The names, after the architecture's prefix, of the whole numbers that are read.</summary>
        private static readonly string[] SizeNames =
        {
            "block_count", "embedding_length", "attention.head_count", "attention.head_count_kv",
            "attention.key_length", "attention.value_length", "expert_count", "expert_used_count",
        };

        /// <summary>A whole number of any width, or null when the value is of another type (nothing is read then).</summary>
        private static long? Number(Source reader, uint type)
        {
            switch (type)
            {
                case 0: return reader.ReadByte();
                case 1: return reader.ReadSByte();
                case 2: return reader.ReadUInt16();
                case 3: return reader.ReadInt16();
                case 4: return reader.ReadUInt32();
                case 5: return reader.ReadInt32();
                case 10: return (long)Math.Min(reader.ReadUInt64(), long.MaxValue);
                case 11: return reader.ReadInt64();
                default: return null;
            }
        }

        /// <summary>A length-prefixed text no longer than the bound, or null (with the position past it) when it is longer.</summary>
        private static string Text(Source reader, int longest)
        {
            ulong length = reader.ReadUInt64();
            if (length > (ulong)longest) { Advance(reader, length); return null; }
            var bytes = reader.ReadBytes((int)length);
            if (bytes.Length != (int)length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Moves past a value of the given type without keeping it; false for a type or a nesting no real file has.</summary>
        private static bool Skip(Source reader, uint type, int depth)
        {
            int size = Size(type);
            if (size > 0) { Advance(reader, (ulong)size); return true; }
            if (type == 8) { Advance(reader, reader.ReadUInt64()); return true; }
            if (type != 9 || depth > 2) return false;
            uint element = reader.ReadUInt32();
            ulong count = reader.ReadUInt64();
            int each = Size(element);
            if (each > 0) { Advance(reader, checked(count * (ulong)each)); return true; }
            // Texts and nested arrays have no fixed size: each is stepped over, as far as the file goes.
            for (ulong i = 0; i < count; i++)
            {
                if (reader.BaseStream.Position >= MostBytes) throw new EndOfStreamException();
                if (!Skip(reader, element, depth + 1)) return false;
            }
            return true;
        }

        private static int Size(uint type)
        {
            switch (type)
            {
                case 0: case 1: case 7: return 1;
                case 2: case 3: return 2;
                case 4: case 5: case 6: return 4;
                case 10: case 11: case 12: return 8;
                default: return 0;
            }
        }

        /// <summary>
        /// The file with its length read once: asking the system for it at every step, through the WSL share, took most
        /// of the 24 seconds a header with a long list needed.
        /// </summary>
        private sealed class Source : BinaryReader
        {
            public readonly long Length;
            public Source(Stream stream) : base(stream, Encoding.UTF8) { Length = stream.Length; }
        }

        /// <summary>Written to and never read: where short stretches of the file go when they are stepped over.</summary>
        private static readonly byte[] Discarded = new byte[4096];

        /// <summary>
        /// Moves forward, refusing to leave the file: a length that points past its end is a damaged header. A short
        /// stretch is read and dropped, not jumped: a jump empties the read buffer, which a list of a hundred thousand
        /// short texts would then fill again at every step.
        /// </summary>
        private static void Advance(Source reader, ulong bytes)
        {
            var stream = reader.BaseStream;
            if (bytes > (ulong)(reader.Length - stream.Position)) throw new EndOfStreamException();
            if (bytes > (ulong)Discarded.Length) { stream.Seek((long)bytes, SeekOrigin.Current); return; }
            int left = (int)bytes;
            while (left > 0)
            {
                int read = stream.Read(Discarded, 0, left);
                if (read <= 0) throw new EndOfStreamException();
                left -= read;
            }
        }
    }
}
