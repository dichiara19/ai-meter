using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AIMeter
{
    /// <summary>
    /// Servers that are not text engines AI Meter knows by name: ComfyUI, recognised by its own interface, and any
    /// server that only answers the common /v1/models, shown while its process holds memory worth a model.
    /// </summary>
    internal static class LocalServerTests
    {
        internal static void Register(Action<string, Action> test)
        {
            test("ComfyUI is recognised by its own interface and read without starting work", ComfyUi);
            test("A server known only by /v1/models is one entry, and says what its model gives when it states it", CommonInterface);
            test("A server found only by the common interface is shown only while it holds memory worth a model", WorthShowing);
            test("Every supported language words a server of the common interface and what it makes", Translations);
        }

        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        /// <summary>A loopback server that answers each path with its JSON and any other with 404, until disposed.</summary>
        private sealed class Routes : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            private readonly Thread thread;
            public readonly int Port;
            public readonly List<string> Asked = new List<string>();

            public Routes(Dictionary<string, string> replies)
            {
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                thread = new Thread(() =>
                {
                    try
                    {
                        while (true)
                            using (var client = listener.AcceptTcpClient())
                            using (var stream = client.GetStream())
                            {
                                client.ReceiveTimeout = 3000;
                                var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                                string request = reader.ReadLine() ?? "", header;
                                while (!string.IsNullOrEmpty(header = reader.ReadLine())) { }
                                var words = request.Split(' ');
                                string path = words.Length > 1 ? words[1] : "", body;
                                lock (Asked) Asked.Add(words[0] + " " + path);
                                bool found = replies.TryGetValue(path, out body);
                                var bytes = Encoding.UTF8.GetBytes(found ? body : "{}");
                                var head = Encoding.ASCII.GetBytes("HTTP/1.1 " + (found ? "200 OK" : "404 Not Found") + "\r\nContent-Type: application/json\r\nContent-Length: "
                                    + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
                                stream.Write(head, 0, head.Length);
                                stream.Write(bytes, 0, bytes.Length);
                            }
                    }
                    catch (SocketException) { }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                thread.IsBackground = true;
                thread.Start();
            }

            public void Dispose() { listener.Stop(); thread.Join(4000); }
        }

        // The keys ComfyUI's server.py writes for GET /system_stats, with the values of an 8 GB card holding 5 GiB.
        private const string SystemStats = "{\"system\":{\"os\":\"posix\",\"ram_total\":16769007616,\"ram_free\":9000000000,\"comfyui_version\":\"0.3.75\",\"python_version\":\"3.12.3\",\"argv\":[\"main.py\",\"--listen\"]},"
            + "\"devices\":[{\"name\":\"cuda:0 NVIDIA GeForce RTX\",\"type\":\"cuda\",\"index\":0,\"vram_total\":8546942976,\"vram_free\":2100000000,\"torch_vram_total\":5368709120,\"torch_vram_free\":400000000}]}";

        private static void ComfyUi()
        {
            using (var server = new Routes(new Dictionary<string, string> { { "/system_stats", SystemStats }, { "/prompt", "{\"exec_info\":{\"queue_remaining\":2}}" } }))
            {
                Assert(LocalProvider.Identify(server.Port) == "comfyui", "ComfyUI was not recognised by /system_stats.");
                var m = LocalProvider.ComfyUi(server.Port).Single();
                Assert(m.Name == "ComfyUI" && m.Detail == "0.3.75" && m.Port == server.Port && !m.Generic, "ComfyUI's entry is not the server with its version.");
                Assert(m.Vram == 5368709120 && m.VramOrigin == MemoryOrigin.Reported, "The video memory PyTorch holds was not read as reported.");
                Assert(m.Busy == 2 && m.Context == 0 && m.Slots == 0, "The queue was not read, or a context was made up for a server that has none.");
                Assert(LocalProvider.WorthShowing(m), "A server known by its own interface was hidden for holding little memory.");
                lock (server.Asked) Assert(server.Asked.All(a => a.StartsWith("GET ", StringComparison.Ordinal)), "A request other than GET was sent: " + string.Join(", ", server.Asked));
            }
            // An idle ComfyUI on a PC without a card: no devices, an empty queue.
            using (var server = new Routes(new Dictionary<string, string> { { "/system_stats", "{\"system\":{\"comfyui_version\":\"0.3.75\"},\"devices\":[]}" } }))
            {
                var m = LocalProvider.ComfyUi(server.Port).Single();
                Assert(m.Vram == 0 && m.VramOrigin == MemoryOrigin.Unknown && m.Busy == 0, "An idle ComfyUI gave figures it does not report.");
            }
            // Another program with a /system_stats of its own is not ComfyUI.
            using (var server = new Routes(new Dictionary<string, string> { { "/system_stats", "{\"system\":{\"os\":\"linux\"}}" } }))
                Assert(LocalProvider.Identify(server.Port) == null && LocalProvider.ComfyUi(server.Port) == null, "A foreign /system_stats was read as ComfyUI's.");
        }

        private static void CommonInterface()
        {
            const string image = "{\"object\":\"list\",\"data\":[{\"id\":\"flux-dev\",\"object\":\"model\",\"owned_by\":\"someone\",\"architecture\":{\"input_modalities\":[\"text\"],\"output_modalities\":[\"image\"]}}]}";
            using (var server = new Routes(new Dictionary<string, string> { { "/v1/models", image } }))
            {
                Assert(LocalProvider.Identify(server.Port) == "openai", "A server answering only /v1/models was not recognised by it.");
                var m = LocalProvider.OpenAiCompatible(server.Port).Single();
                Assert(m.Name == "flux-dev" && m.Makes == "image" && m.Generic && m.Port == server.Port, "The one model of the server was not read with what it gives.");
                m.Server = LocalProvider.ServerName("openai");
                m.Where = "Windows";
                Assert(Ui.LocalFacts(m).SequenceEqual(new[] { "OpenAI-compatible", "images" }), "The facts do not say what the model gives: " + string.Join(" · ", Ui.LocalFacts(m)));
            }
            // Several models listed (what it can serve, not what is loaded): one entry, named by the caller after the program.
            const string many = "{\"data\":[{\"id\":\"a\",\"context_length\":8192},{\"id\":\"b\",\"architecture\":{\"output_modalities\":[\"audio\"]}}]}";
            using (var server = new Routes(new Dictionary<string, string> { { "/v1/models", many } }))
            {
                var m = LocalProvider.OpenAiCompatible(server.Port).Single();
                Assert(m.Name == null && m.Makes == null && m.Context == 0, "A server listing several models was named after one of them.");
            }
            using (var server = new Routes(new Dictionary<string, string> { { "/v1/models", "{\"data\":[{\"id\":\"chat\",\"max_model_len\":32768}]}" } }))
            {
                var m = LocalProvider.OpenAiCompatible(server.Port).Single();
                Assert(m.Name == "chat" && m.Makes == null && m.Context == 32768, "A text model's context was not read, or it was said to give something else.");
            }
            // A known engine keeps its own kind: vLLM also answers /v1/models.
            using (var server = new Routes(new Dictionary<string, string> { { "/v1/models", "{\"data\":[{\"id\":\"x\",\"owned_by\":\"vllm\"}]}" } }))
                Assert(LocalProvider.Identify(server.Port) == "vllm", "A server AI Meter knows was taken for an unknown one.");
            // A port that speaks HTTP and has none of the interfaces is no model server.
            using (var server = new Routes(new Dictionary<string, string>()))
                Assert(LocalProvider.Identify(server.Port) == null && LocalProvider.OpenAiCompatible(server.Port) == null, "A server without /v1/models was recognised.");

            Assert(LocalProvider.Output(Json.ParseAny("{\"architecture\":{\"output_modalities\":[\"text\",\"audio\"]}}")) == "audio"
                && LocalProvider.Output(Json.ParseAny("{\"architecture\":{\"output_modalities\":[\"text\"]}}")) == null
                && LocalProvider.Output(Json.ParseAny("{\"architecture\":{\"modality\":\"text->image\"}}")) == null && LocalProvider.Output(null) == null,
                "What a model gives was read from something other than its stated outputs.");
        }

        private static void WorthShowing()
        {
            long gb = 1L << 30;
            Func<string, long, long, LocalModel> generic = (where, ram, vram) => new LocalModel { Name = "x", Where = where, Ram = ram, Vram = vram, Generic = true };
            Assert(!LocalProvider.WorthShowing(generic("Windows", 200L << 20, 0)), "A proxy holding 200 MB was shown as a local model.");
            Assert(LocalProvider.WorthShowing(generic("Windows", 200L << 20, 6 * gb)) && LocalProvider.WorthShowing(generic("Windows", 3 * gb, 0)), "A server holding a model was hidden.");
            Assert(!LocalProvider.WorthShowing(generic("Windows", 0, 0)), "A server whose process was not found was shown.");
            // In WSL the video memory of the whole machine would go to it: only its own system memory counts.
            Assert(!LocalProvider.WorthShowing(generic("WSL", 200L << 20, 6 * gb)) && LocalProvider.WorthShowing(generic("WSL", 2 * gb, 0)), "In WSL video memory that is not the server's own made it a model.");
            Assert(LocalProvider.WorthAModel == gb && LocalProvider.WorthShowing(generic("Windows", gb, 0)) && !LocalProvider.WorthShowing(generic("Windows", gb - 1, 0)), "The bound is not a gigabyte.");
            Assert(LocalProvider.WorthShowing(new LocalModel { Name = "y", Where = "Windows" }), "A server known by its own interface needs memory to be shown.");
        }

        private static void Translations()
        {
            try
            {
                foreach (var lang in new[] { "it", "es", "fr", "de", "pt" })
                {
                    L.Init(lang);
                    foreach (var key in new[] { "OpenAI-compatible", "images" }) Assert(L.T(key) != key, "Missing translation for " + lang + ": " + key);
                }
            }
            finally { L.Init("en"); }
        }
    }
}
