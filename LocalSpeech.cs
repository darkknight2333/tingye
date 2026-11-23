using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tingye
{
    internal sealed class LocalSpeechBusyException : IOException
    {
        public LocalSpeechBusyException(string provider)
            : base(provider + " 仍在处理上一段。请稍等片刻，再从当前段落继续朗读。") { }
    }

    internal sealed class LocalSpeech : IDisposable
    {
        private static readonly TimeSpan IdleLifetime = TimeSpan.FromSeconds(25);
        public const string Choice = "breeze-local:tingye-clever";
        public const string Label = "机灵女声 · 本地";
        public const string QwenChoice = "qwen-local:vivian";
        public const string QwenLabel = "Vivian · Qwen3-TTS 1.7B";
        public const string QwenSerenaChoice = "qwen-local:serena";
        public const string QwenSerenaLabel = "Serena · Qwen3-TTS 1.7B";
        private const string Speaker = "tingye-clever";
        private readonly string root, model, runtime, voices, ffmpeg;
        private readonly string qwenRuntime, qwenModel, qwenTokenizer;
        private readonly SemaphoreSlim synthesis = new SemaphoreSlim(1, 1);
        private readonly Lazy<Task<bool>> smallGpu = new Lazy<Task<bool>>(() => Task.Run(new Func<bool>(HasSmallNvidiaGpu)));
        private readonly object processGate = new object();
        private readonly Timer idleTimer;
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(120) };
        private Process server;
        private SpeechProcessJob serverJob;
        private Uri address;
        private DateTime idleUntilUtc;
        private bool disposed, playbackActive;
        private bool serverUsesQwen;
        private bool serverReady;

        public LocalSpeech(string speechDirectory)
        {
            root = Path.Combine(speechDirectory, "breeze-local");
            model = Path.Combine(root, "breeze-tts-2-q4_k.gguf");
            runtime = Path.Combine(root, "runtime", "breeze-server.exe");
            voices = Path.Combine(root, "voices");
            ffmpeg = Path.Combine(root, "media", "ffmpeg.exe");
            var qwenRoot = Path.Combine(speechDirectory, "qwen-local");
            qwenRuntime = Path.Combine(qwenRoot, "koboldcpp-nocuda.exe");
            qwenModel = Path.Combine(qwenRoot, "Qwen3-TTS-12Hz-1.7B-CustomVoice-Q8_0.gguf");
            qwenTokenizer = Path.Combine(qwenRoot, "qwen3-tts-tokenizer-f16.gguf");
            idleTimer = new Timer(ReleaseIdleServer, null, Timeout.Infinite, Timeout.Infinite);
        }

        public bool QwenInstalled
        {
            get { return File.Exists(qwenRuntime) && File.Exists(qwenModel) && File.Exists(qwenTokenizer) && File.Exists(ffmpeg); }
        }

        public string QwenIdentity(string voice)
        {
            return "qwen-local:" + voice + ":1.7b-q8-v1:" +
                File.GetLastWriteTimeUtc(qwenModel).Ticks + ":" + File.GetLastWriteTimeUtc(qwenTokenizer).Ticks;
        }

        public bool IsInstalled
        {
            get
            {
                return File.Exists(model) && File.Exists(runtime) && File.Exists(ffmpeg)
                    && File.Exists(Path.Combine(voices, Speaker + ".breeze"));
            }
        }

        public string Identity
        {
            get
            {
                return Choice + ":q4-v1:" +
                    File.GetLastWriteTimeUtc(Path.Combine(voices, Speaker + ".breeze")).Ticks;
            }
        }

        public Task<byte[]> SynthesizeAsync(string text, string instructions, CancellationToken token,
            float speed = 1f, int tailMs = 140, Func<byte[], Task> streamAudio = null)
        {
            var payload = new JavaScriptSerializer().Serialize(new {
                input = text, voice = Speaker, instructions = instructions,
                response_format = "pcm", cfg_scale = 1.0
            });
            return RequestSpeechAsync(payload, false, token, streamAudio == null ? null :
                (Func<Stream, CancellationToken, Task<byte[]>>)((body, cancel) =>
                    ReadLiveAsync(body, speed, tailMs, streamAudio, cancel)));
        }

        public Task<byte[]> SynthesizeQwenAsync(string payload, CancellationToken token)
        {
            return RequestSpeechAsync(payload, true, token);
        }

        public void SetPlaybackActive(bool active)
        {
            lock (processGate)
            {
                if (disposed || playbackActive == active) return;
                playbackActive = active;
                if (active) idleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                else if (server != null) ScheduleIdleRelease();
            }
        }

        private async Task<byte[]> RequestSpeechAsync(string payload, bool useQwen, CancellationToken token,
            Func<Stream, CancellationToken, Task<byte[]>> streamReader = null)
        {
            await synthesis.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Uri endpoint = await EnsureServerAsync(token, useQwen).ConfigureAwait(false);
                var speech = new Uri(endpoint, "v1/audio/speech");
                var busyTime = Stopwatch.StartNew();
                int retryDelayMs = 150;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, speech))
                        {
                            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                            return await BreezeVoice.ReadAudioAsync(http, request, true, token, useQwen, streamReader).ConfigureAwait(false);
                        }
                    }
                    catch (LocalSpeechBusyException)
                    {
                        if (busyTime.Elapsed >= TimeSpan.FromSeconds(25)) throw;
                    }
                    // A canceled stream may still occupy the server's generation slot.
                    await Task.Delay(retryDelayMs, token).ConfigureAwait(false);
                    retryDelayMs = Math.Min(retryDelayMs * 2, 1000);
                }
            }
            catch
            {
                // Closing the HTTP stream alone may leave native inference running.
                lock (processGate) StopServer();
                throw;
            }
            finally { ReleaseSynthesis(); }
        }

        private void ReleaseSynthesis()
        {
            lock (processGate)
            {
                if (!disposed && !playbackActive && server != null) ScheduleIdleRelease();
            }
            synthesis.Release();
        }

        private void ScheduleIdleRelease()
        {
            idleUntilUtc = DateTime.UtcNow + IdleLifetime;
            idleTimer.Change(IdleLifetime, Timeout.InfiniteTimeSpan);
        }

        private void ReleaseIdleServer(object state)
        {
            if (!synthesis.Wait(0)) return;
            try
            {
                lock (processGate)
                {
                    if (disposed || playbackActive || server == null) return;
                    TimeSpan remaining = idleUntilUtc - DateTime.UtcNow;
                    if (remaining > TimeSpan.Zero) idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                    else StopServer();
                }
            }
            finally { synthesis.Release(); }
        }

        private Process StartTempo(float speed)
        {
            var info = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error -nostdin -filter_threads 1 -threads 1 -probesize 32 -analyzeduration 0 -f s16le -ar 24000 -ac 1 -i pipe:0 -af atempo="
                + speed.ToString(CultureInfo.InvariantCulture)
                + " -flush_packets 1 -f s16le -ar 24000 -ac 1 pipe:1") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            return Process.Start(info);
        }

        private async Task<byte[]> ReadLiveAsync(Stream body, float speed, int tailMs, Func<byte[], Task> deliver, CancellationToken token)
        {
            if (speed == 1f) return await StreamingPcm.ReadAsync(body, tailMs, deliver, token).ConfigureAwait(false);
            using (var process = StartTempo(speed))
            using (token.Register(() => Terminate(process)))
            {
                var diagnostics = process.StandardError.ReadToEndAsync();
                var drain = DrainTempoAsync(process, deliver, token);
                var write = WriteLiveInputAsync(process, body, tailMs, token);
                await Task.WhenAll(write, drain, diagnostics).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException("本地语音变速失败。");
                return await write.ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> WriteLiveInputAsync(Process process, Stream body, int tailMs, CancellationToken token)
        {
            using (var input = process.StandardInput.BaseStream)
                return await StreamingPcm.ReadAsync(body, tailMs, block => input.WriteAsync(block, 0, block.Length, token), token).ConfigureAwait(false);
        }

        private static async Task DrainTempoAsync(Process process, Func<byte[], Task> deliver, CancellationToken token)
        {
            try
            {
                var buffer = new byte[12288];
                int used = 0, read;
                var source = process.StandardOutput.BaseStream;
                while ((read = await source.ReadAsync(buffer, used, buffer.Length - used, token).ConfigureAwait(false)) > 0)
                {
                    used += read;
                    if (used < buffer.Length) continue;
                    await deliver(buffer).ConfigureAwait(false);
                    buffer = new byte[buffer.Length];
                    used = 0;
                }
                if (used % 2 != 0) throw new IOException("变速音频不完整。");
                if (used > 0) { Array.Resize(ref buffer, used); await deliver(buffer).ConfigureAwait(false); }
            }
            catch { Terminate(process); throw; }
        }

        public async Task<byte[]> ChangeSpeedAsync(byte[] audio, float speed, CancellationToken token)
        {
            if (speed == 1f) return audio;
            using (var process = StartTempo(speed))
            using (var output = new MemoryStream())
            using (token.Register(() => Terminate(process)))
            {
                Task write = WriteInputAsync(process, audio, token);
                Task read = process.StandardOutput.BaseStream.CopyToAsync(output, 16384, token);
                Task<string> diagnostics = process.StandardError.ReadToEndAsync();
                try { await Task.WhenAll(write, read, diagnostics).ConfigureAwait(false); }
                catch (IOException) { token.ThrowIfCancellationRequested(); throw; }
                token.ThrowIfCancellationRequested();
                process.WaitForExit();
                if (process.ExitCode != 0 || output.Length == 0 || output.Length % 2 != 0)
                    throw new IOException("本地语音变速失败。");
                return output.ToArray();
            }
        }

        private static async Task WriteInputAsync(Process process, byte[] audio, CancellationToken token)
        {
            using (var stream = process.StandardInput.BaseStream)
                await stream.WriteAsync(audio, 0, audio.Length, token).ConfigureAwait(false);
        }

        private async Task<Uri> EnsureServerAsync(CancellationToken token, bool useQwen)
        {
            string provider = useQwen ? "本地 Qwen3-TTS" : "本地 Breeze";
            token.ThrowIfCancellationRequested();
            bool offloadEncoder = !useQwen && String.IsNullOrEmpty(Environment.GetEnvironmentVariable("BREEZE_TE_CPU"))
                && await smallGpu.Value.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Process process;
            Uri endpoint;
            lock (processGate)
            {
                if (disposed) throw new ObjectDisposedException("LocalSpeech");
                if (server == null || server.HasExited || serverUsesQwen != useQwen)
                {
                    if (useQwen ? !QwenInstalled : !IsInstalled)
                        throw new FileNotFoundException(provider + " 模型或音色文件缺失。");
                    StopServer();
                    long modelBytes = useQwen ? new FileInfo(qwenModel).Length + new FileInfo(qwenTokenizer).Length
                        : new FileInfo(model).Length;
                    SpeechProcessJob.CheckAvailableMemory(modelBytes);
                    int port = FindLocalPort();
                    address = new Uri("http://127.0.0.1:" + port + "/");
                    string executable = useQwen ? qwenRuntime : runtime;
                    string arguments = useQwen
                        ? "--ttsmodel \"" + qwenModel + "\" --ttswavtokenizer \"" + qwenTokenizer +
                          "\" --usevulkan 0 --ttsgpu --ttsmaxlen 1024 --quiet --skiplauncher"
                        : "\"" + model + "\" --no-warmup --ws-port -1 --voices-dir \"" + voices + "\"";
                    var info = new ProcessStartInfo(executable, arguments + " --host 127.0.0.1 --port " + port) {
                        WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    if (offloadEncoder)
                    {
                        info.EnvironmentVariables["BREEZE_TE_CPU"] = "1";
                        if (String.IsNullOrEmpty(info.EnvironmentVariables["BREEZE_TE_THREADS"]))
                            info.EnvironmentVariables["BREEZE_TE_THREADS"] = Math.Min(4, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
                    }
                    serverJob = SpeechProcessJob.Create();
                    server = Process.Start(info);
                    serverJob.Add(server);
                    serverUsesQwen = useQwen;
                    server.BeginOutputReadLine();
                    server.BeginErrorReadLine();
                }
                if (serverReady) return address;
                process = server;
                endpoint = address;
            }
            var health = new Uri(endpoint, useQwen ? "api/extra/version" : "health");
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var response = await http.GetAsync(health, token).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            lock (processGate)
                            {
                                token.ThrowIfCancellationRequested();
                                if (disposed) throw new ObjectDisposedException("LocalSpeech");
                                serverReady = true;
                            }
                            return endpoint;
                        }
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            throw new IOException(provider + " 未能启动。请确认模型完整，并检查可用内存和显存。");
        }

        private static bool HasSmallNvidiaGpu()
        {
            var tool = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (!File.Exists(tool)) return false;
            try
            {
                using (var probe = Process.Start(new ProcessStartInfo(tool, "--query-gpu=memory.total --format=csv,noheader,nounits") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    if (!probe.WaitForExit(1500)) { Terminate(probe); return false; }
                    int mib;
                    return probe.ExitCode == 0 && Int32.TryParse(probe.StandardOutput.ReadToEnd().Trim(), out mib)
                        && mib > 0 && mib <= 6144;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (IOException) { return false; }
        }

        private static int FindLocalPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally { listener.Stop(); }
        }

        private static void Terminate(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private void StopServer()
        {
            serverReady = false;
            if (serverJob != null)
            {
                serverJob.Dispose();
                serverJob = null;
            }
            if (server == null) return;
            Terminate(server);
            try { server.WaitForExit(1200); }
            catch (System.ComponentModel.Win32Exception) { }
            server.Dispose();
            server = null;
        }

        public void Dispose()
        {
            lock (processGate)
            {
                disposed = true;
                idleTimer.Dispose();
                StopServer();
            }
            http.Dispose();
        }
    }
}
