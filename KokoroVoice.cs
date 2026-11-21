using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Tingye
{
    internal sealed class KokoroVoice
    {
        private const string NativeLibrary = "sherpa-onnx-c-api.dll";
        private static readonly string[] Lexicons = { "lexicon-us-en.txt", "lexicon-zh.txt" };
        private static readonly string[] Rules = { "date-zh.fst", "phone-zh.fst", "number-zh.fst" };
        private readonly string directory, model;
        private readonly object gate = new object();
        private Engine engine;

        public KokoroVoice(string directory)
        {
            this.directory = directory;
            model = Path.Combine(directory, "kokoro-multi-lang-v1_1");
        }

        public bool IsInstalled
        {
            get
            {
                return Environment.Is64BitProcess && File.Exists(Path.Combine(directory, "runtime", NativeLibrary)) &&
                    File.Exists(Path.Combine(directory, "runtime", "onnxruntime.dll")) &&
                    Directory.Exists(Path.Combine(model, "espeak-ng-data")) &&
                    File.Exists(Path.Combine(model, "model.onnx")) && File.Exists(Path.Combine(model, "voices.bin")) &&
                    File.Exists(Path.Combine(model, "tokens.txt")) &&
                    Array.TrueForAll(Lexicons, HasModelFile) && Array.TrueForAll(Rules, HasModelFile);
            }
        }

        public Task<byte[]> SynthesizeAsync(string text, bool paragraphEnd, int speaker, float rate, CancellationToken token)
        {
            return Task.Run(() => {
                lock (gate)
                {
                    token.ThrowIfCancellationRequested();
                    if (engine == null) engine = Open();
                    token.ThrowIfCancellationRequested();
                    string synthesisText = SpeechText.ForSynthesis(text, paragraphEnd);
                    IntPtr result = SherpaOnnxOfflineTtsGenerate(engine, Encoding.UTF8.GetBytes(synthesisText + '\0'), speaker, rate);
                    if (result == IntPtr.Zero) throw new InvalidOperationException("语音模型没有返回音频。");
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var data = (Audio)Marshal.PtrToStructure(result, typeof(Audio));
                        if (data.Size == 0) return null;
                        if (data.Size < 0 || data.Size > 4000000 || data.SampleRate < 8000 || data.SampleRate > 96000 || data.Samples == IntPtr.Zero)
                            throw new InvalidDataException("语音模型返回了无效音频。");
                        var samples = new float[data.Size];
                        Marshal.Copy(data.Samples, samples, 0, samples.Length);
                        return EncodeWave(samples, data.SampleRate, SpeechText.TailPauseMs(text, paragraphEnd));
                    }
                    finally { SherpaOnnxDestroyOfflineTtsGeneratedAudio(result); }
                }
            }, token);
        }

        private Engine Open()
        {
            const uint loadFromLibraryDirectory = 0x100;
            const uint searchDefaultDirectories = 0x1000;
            if (LoadLibraryEx(Path.Combine(directory, "runtime", NativeLibrary), IntPtr.Zero,
                loadFromLibraryDirectory | searchDefaultDirectories) == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法加载离线语音运行库。");

            using (var utf8 = new Utf8Strings())
            {
                var config = new TtsConfig {
                    Model = new ModelConfig {
                        Threads = Math.Min(Environment.ProcessorCount, 4), Provider = utf8.Add("cpu"),
                        Kokoro = new KokoroConfig {
                            Model = utf8.Add(Path.Combine(model, "model.onnx")), Voices = utf8.Add(Path.Combine(model, "voices.bin")),
                            Tokens = utf8.Add(Path.Combine(model, "tokens.txt")), DataDirectory = utf8.Add(Path.Combine(model, "espeak-ng-data")),
                            Scale = 1, Lexicon = utf8.Add(ModelFiles(Lexicons))
                        }
                    },
                    MaxSentences = 1, SilenceScale = 1f,
                    RuleFsts = utf8.Add(ModelFiles(Rules))
                };
                var loaded = SherpaOnnxCreateOfflineTts(ref config);
                if (loaded.IsInvalid) throw new InvalidOperationException("离线语音模型初始化失败。");
                return loaded;
            }
        }

        private bool HasModelFile(string name) { return File.Exists(Path.Combine(model, name)); }

        private string ModelFiles(string[] files)
        {
            return String.Join(",", Array.ConvertAll(files, name => {
                var path = Path.Combine(model, name);
                if (!File.Exists(path)) throw new FileNotFoundException("离线语音模型缺少 " + name, path);
                return path;
            }));
        }

        private static byte[] EncodeWave(float[] samples, int sampleRate, int tailPauseMs)
        {
            int tail = sampleRate * tailPauseMs / 1000;
            int audioSamples = SpeechPauses.Shorten(samples, sampleRate, tail);
            int silenceSamples = tail - SpeechPauses.TrailingQuiet(samples, audioSamples, tail);
            int dataSize = (audioSamples + silenceSamples) * sizeof(short);
            using (var stream = new MemoryStream(44 + dataSize))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x46464952); // RIFF
                writer.Write(36 + dataSize);
                writer.Write(0x45564157); // WAVE
                writer.Write(0x20746d66); // fmt
                writer.Write(16);
                writer.Write((short)1);   // PCM
                writer.Write((short)1);   // Mono
                writer.Write(sampleRate);
                writer.Write(sampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);
                writer.Write(0x61746164); // data
                writer.Write(dataSize);
                for (int i = 0; i < audioSamples; i++)
                    writer.Write((short)(Math.Max(-1, Math.Min(1, samples[i])) * Int16.MaxValue));
                writer.Write(new byte[silenceSamples * sizeof(short)]);
                return stream.ToArray();
            }
        }

        private sealed class Utf8Strings : IDisposable
        {
            private readonly List<IntPtr> pointers = new List<IntPtr>();

            public IntPtr Add(string text)
            {
                var bytes = Encoding.UTF8.GetBytes(text + '\0');
                var pointer = Marshal.AllocHGlobal(bytes.Length);
                pointers.Add(pointer);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                return pointer;
            }

            public void Dispose()
            {
                foreach (var pointer in pointers) Marshal.FreeHGlobal(pointer);
            }
        }

        private sealed class Engine : SafeHandleZeroOrMinusOneIsInvalid
        {
            private Engine() : base(true) { }
            protected override bool ReleaseHandle() { SherpaOnnxDestroyOfflineTts(handle); return true; }
        }

        // Layouts match sherpa-onnx v1.13.8, sherpa-onnx/c-api/c-api.h.
        [StructLayout(LayoutKind.Sequential)]
        private struct VitsConfig { public IntPtr Model, Lexicon, Tokens, DataDirectory; public float Noise, NoiseW, Scale; public IntPtr Dict; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MatchaConfig { public IntPtr Acoustic, Vocoder, Lexicon, Tokens, DataDirectory; public float Noise, Scale; public IntPtr Dict; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KokoroConfig { public IntPtr Model, Voices, Tokens, DataDirectory; public float Scale; public IntPtr Dict, Lexicon, Language; }
        [StructLayout(LayoutKind.Sequential)]
        private struct KittenConfig { public IntPtr Model, Voices, Tokens, DataDirectory; public float Scale; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ZipvoiceConfig { public IntPtr Tokens, Encoder, Decoder, Vocoder, DataDirectory, Lexicon; public float FeatScale, Shift, Target, Guidance; }
        [StructLayout(LayoutKind.Sequential)]
        private struct PocketConfig { public IntPtr LmFlow, LmMain, Encoder, Decoder, TextConditioner, Vocab, TokenScores; public int CacheCapacity; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SupertonicConfig { public IntPtr Duration, Encoder, Estimator, Vocoder, TtsJson, UnicodeIndexer, Style; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ModelConfig
        {
            public VitsConfig Vits;
            public int Threads, Debug;
            public IntPtr Provider;
            public MatchaConfig Matcha;
            public KokoroConfig Kokoro;
            public KittenConfig Kitten;
            public ZipvoiceConfig Zipvoice;
            public PocketConfig Pocket;
            public SupertonicConfig Supertonic;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TtsConfig { public ModelConfig Model; public IntPtr RuleFsts; public int MaxSentences; public IntPtr RuleFars; public float SilenceScale; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Audio { public IntPtr Samples; public int Size, SampleRate; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern Engine SherpaOnnxCreateOfflineTts(ref TtsConfig config);
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SherpaOnnxDestroyOfflineTts(IntPtr instance);
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SherpaOnnxOfflineTtsGenerate(Engine instance, byte[] text, int speaker, float speed);
        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern void SherpaOnnxDestroyOfflineTtsGeneratedAudio(IntPtr audio);
    }
}
