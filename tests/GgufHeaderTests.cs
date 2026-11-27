using System;
using System.IO;
using System.Linq;
using System.Text;

namespace AIMeter
{
    /// <summary>The header of a GGUF model file: read for a few facts, and never trusted for a length.</summary>
    internal static class GgufHeaderTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("A GGUF header gives the architecture, the experts and the sizes of the key-value cache", ReadsHeader);
            test("A damaged, foreign or hostile model file gives no header and costs little", RefusesBadFiles);
            test("A model's file says it is a mixture of experts and how much its key-value cache weighs", DescribesModel);
            test("Every supported language words the experts of a model file", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private static void Text(BinaryWriter w, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            w.Write((ulong)bytes.Length);
            w.Write(bytes);
        }

        private static void Number(BinaryWriter w, string key, uint value) { Text(w, key); w.Write(4u); w.Write(value); }

        /// <summary>A file with the given pairs, written as llama.cpp writes them; the count can lie, to test a damaged file.</summary>
        private static string Write(string name, ulong declaredPairs, Action<BinaryWriter> pairs, uint version = 3)
        {
            string path = Path.Combine(Settings.TestFolder, name);
            using (var stream = File.Create(path))
            using (var w = new BinaryWriter(stream, Encoding.UTF8))
            {
                w.Write(0x46554747u);
                w.Write(version);
                w.Write(291ul);   // Tensors.
                w.Write(declaredPairs);
                pairs(w);
                w.Write(new byte[64]);   // What follows the metadata.
            }
            return path;
        }

        /// <summary>A mixture of experts as its file describes it, with a vocabulary before and after the keys that are read.</summary>
        private static void Mixture(BinaryWriter w)
        {
            Text(w, "general.architecture"); w.Write(8u); Text(w, "qwen3moe");
            Text(w, "general.name"); w.Write(8u); Text(w, new string('n', 5000));   // Longer than any text that is kept.
            Text(w, "general.file_type"); w.Write(4u); w.Write(15u);
            Text(w, "tokenizer.ggml.scores"); w.Write(9u); w.Write(6u); w.Write(3000ul); w.Write(new byte[12000]);   // An array of floats.
            Number(w, "qwen3moe.block_count", 48);
            Number(w, "qwen3moe.embedding_length", 2048);
            Number(w, "qwen3moe.attention.head_count", 32);
            Number(w, "qwen3moe.attention.head_count_kv", 4);
            Number(w, "qwen3moe.attention.key_length", 128);
            Number(w, "qwen3moe.attention.value_length", 128);
            Text(w, "qwen3moe.expert_count"); w.Write(10u); w.Write(128ul);   // A 64-bit number.
            Number(w, "qwen3moe.expert_used_count", 8);
            Text(w, "tokenizer.ggml.tokens"); w.Write(9u); w.Write(8u); w.Write(2000ul);   // An array of texts.
            for (int i = 0; i < 2000; i++) Text(w, "token" + i);
            Text(w, "general.alignment"); w.Write(4u); w.Write(32u);
        }

        private static void ReadsHeader()
        {
            var h = GgufHeader.Read(Write("moe.gguf", 14, Mixture));
            Assert(h != null && h.Architecture == "qwen3moe", "The architecture was not read.");
            Assert(h.Blocks == 48 && h.Embedding == 2048 && h.Heads == 32 && h.HeadsKv == 4 && h.KeyLength == 128 && h.ValueLength == 128, "The sizes were not read past the arrays.");
            Assert(h.Experts == 128 && h.ExpertsUsed == 8, "The experts were not read.");
            // One key and one value of 128 numbers per token, layer and key-value head, two bytes a number.
            Assert(h.KvBytes(32768) == 32768L * 48 * 4 * (128 + 128) * 2, "The cache's size is not tokens by layers by heads by key and value.");
            Assert(GgufHeader.Read(Write("v2.gguf", 14, Mixture, 2)).Blocks == 48, "A version 2 file was not read.");

            // Without key lengths they are the embedding shared among the heads; without key-value heads, the heads.
            h = GgufHeader.Read(Write("dense.gguf", 4, w =>
            {
                Text(w, "general.architecture"); w.Write(8u); Text(w, "llama");
                Number(w, "llama.block_count", 32);
                Number(w, "llama.embedding_length", 4096);
                Number(w, "llama.attention.head_count", 32);
            }));
            Assert(h.Experts == 0 && h.KvBytes(8192) == 8192L * 32 * 32 * (128 + 128) * 2, "A dense model's cache was not estimated from its embedding.");
            Assert(h.KvBytes(0) == 0 && new GgufHeader().KvBytes(8192) == 0, "An estimate was made without tokens or without sizes.");

            // Head counts given per layer cannot give one figure: no estimate, and the rest is still read.
            h = GgufHeader.Read(Write("perlayer.gguf", 3, w =>
            {
                Text(w, "general.architecture"); w.Write(8u); Text(w, "x");
                Text(w, "x.attention.head_count"); w.Write(9u); w.Write(4u); w.Write(2ul); w.Write(8u); w.Write(16u);
                Number(w, "x.block_count", 2);
            }));
            Assert(h.Heads == 0 && h.Blocks == 2 && h.KvBytes(1000) == 0, "Head counts per layer were read as one figure, or stopped the reading.");
        }

        private static void RefusesBadFiles()
        {
            string good = Write("good.gguf", 14, Mixture);
            var bytes = File.ReadAllBytes(good);
            Func<string, byte[], GgufHeader> read = (name, content) =>
            {
                string path = Path.Combine(Settings.TestFolder, name);
                File.WriteAllBytes(path, content);
                return GgufHeader.Read(path);
            };
            Assert(read("cut.gguf", bytes.Take(bytes.Length / 2).ToArray()) == null && read("short.gguf", bytes.Take(10).ToArray()) == null, "A file cut short gave a header.");
            var other = (byte[])bytes.Clone();
            other[0] = (byte)'X';
            Assert(read("other.bin", other) == null && read("empty.gguf", new byte[0]) == null, "A file of another kind gave a header.");
            Assert(GgufHeader.Read(Path.Combine(Settings.TestFolder, "missing.gguf")) == null, "A missing file gave a header.");
            Assert(GgufHeader.Read(Write("v1.gguf", 14, Mixture, 1)) == null, "A version that is laid out differently was read.");
            Assert(GgufHeader.Read(Write("many.gguf", 2000000, Mixture)) == null, "More pairs than any file has were accepted.");
            Assert(GgufHeader.Read(Write("more.gguf", 15, Mixture)) == null, "A count of pairs past the end of the file gave a header.");

            // Lengths that point outside the file: a text, an array of numbers, an array of texts.
            Assert(GgufHeader.Read(Write("longtext.gguf", 2, w => { Text(w, "general.architecture"); w.Write(8u); Text(w, "a"); Text(w, "general.name"); w.Write(8u); w.Write(ulong.MaxValue); })) == null,
                "A text longer than the file was followed.");
            Assert(GgufHeader.Read(Write("longarray.gguf", 2, w => { Text(w, "general.architecture"); w.Write(8u); Text(w, "a"); Text(w, "t"); w.Write(9u); w.Write(10u); w.Write(ulong.MaxValue); })) == null,
                "An array longer than the file, or than a number can count, was followed.");
            Assert(GgufHeader.Read(Write("longlist.gguf", 2, w => { Text(w, "general.architecture"); w.Write(8u); Text(w, "a"); Text(w, "t"); w.Write(9u); w.Write(8u); w.Write(ulong.MaxValue); })) == null,
                "A list of more texts than the file holds was followed.");
            Assert(GgufHeader.Read(Write("longkey.gguf", 1, w => { Text(w, new string('k', 1000)); w.Write(4u); w.Write(1u); })) == null, "A key longer than any real one was accepted.");
            Assert(GgufHeader.Read(Write("badtype.gguf", 2, w => { Text(w, "general.architecture"); w.Write(8u); Text(w, "a"); Text(w, "t"); w.Write(99u); })) == null, "A value of no known type was skipped by guessing.");
            // Arrays nested deeper than any file nests them.
            Assert(GgufHeader.Read(Write("nested.gguf", 2, w =>
            {
                Text(w, "general.architecture"); w.Write(8u); Text(w, "a");
                Text(w, "t");
                for (int i = 0; i < 6; i++) { w.Write(9u); if (i > 0) w.Write(1ul); }
                w.Write(4u); w.Write(1ul); w.Write(7u);
            })) == null, "Arrays nested without end were followed.");
            // A file with no architecture is not a model's header.
            Assert(GgufHeader.Read(Write("nameless.gguf", 1, w => Number(w, "x.block_count", 2))) == null, "A header without an architecture was accepted.");
        }

        private static void DescribesModel()
        {
            var header = GgufHeader.Read(Write("described.gguf", 14, Mixture));
            // Two slots of 16K tokens each: the cache holds 32K.
            var m = new LocalModel { Name = "a", Server = "llama.cpp", Where = "Windows", Context = 16384, Slots = 2 };
            LocalProvider.Describe(m, header);
            long expected = 32768L * 48 * 4 * 256 * 2;
            Assert(m.Segments.Experts == 128 && m.Segments.ExpertsUsed == 8 && m.Segments.KvBytes == expected && m.Segments.KvCapacity == 32768, "The header's facts did not reach the model.");
            Assert(Ui.SegmentLines(m).SequenceEqual(new[] { "MoE experts: 128 experts, 8 per token", "KV cache: ~3.0 GB · up to 32K tokens" }), "The model's file is not stated: " + string.Join(" / ", Ui.SegmentLines(m)));

            // What the engine reported stands: the header only adds.
            var reported = new LocalModel { Name = "b", Context = 8192, Slots = 1, Segments = new ModelSegments { Engine = "X", KvCapacity = 4096, KvType = "int8", TokensPerSecond = 12 } };
            LocalProvider.Describe(reported, header);
            Assert(reported.Segments.Engine == "X" && reported.Segments.KvCapacity == 4096 && reported.Segments.KvType == "int8" && reported.Segments.TokensPerSecond == 12 && reported.Segments.Experts == 128,
                "The header replaced what the engine reported.");

            // A dense model whose context is unknown has nothing to add: no empty segments appear.
            var plain = new LocalModel { Name = "c" };
            LocalProvider.Describe(plain, new GgufHeader { Architecture = "llama", Blocks = 32, Embedding = 4096, Heads = 32 });
            Assert(plain.Segments == null && Ui.SegmentLines(plain).Count == 0, "A header with nothing to say made segments.");
        }

        private static void Translations()
        {
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    Assert(L.F("{0} experts, {1} per token", 128, 8) != "128 experts, 8 per token" && L.T("{0} experts") != "{0} experts", "Missing translation for " + lang + ".");
                }
            }
            finally { L.Init("en"); }
        }
    }
}
