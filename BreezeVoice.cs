using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tingye
{
    internal sealed class BreezeVoice
    {
        public const string VoiceId = "voc_u3nxkd5578wc";
        public const string VoiceName = "大肥鱼（机灵版）";
        public const int SampleRate = 24000;
        private const string Model = "breeze-tts-2";
        private const int MaxAudioBytes = 8 * 1024 * 1024;
        private const long CacheLimit = 192L * 1024 * 1024;
        private const long CacheTrimReserve = 16L * 1024 * 1024;
        private const long PlaybackCacheLimit = 24L * 1024 * 1024;
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        private readonly HttpClient http;
        private readonly string keyFile, cache;
        private readonly LocalSpeech local;
        private readonly object diskCacheGate = new object(), playbackCacheGate = new object();
        private readonly Dictionary<string, LinkedListNode<PlaybackClip>> playbackCache = new Dictionary<string, LinkedListNode<PlaybackClip>>();
        private readonly LinkedList<PlaybackClip> playbackOrder = new LinkedList<PlaybackClip>();
        private long playbackBytes;
        private long? diskCacheBytes;

        public BreezeVoice(string directory, string speechDirectory, HttpClient client = null)
        {
            keyFile = Path.Combine(directory, "breeze-key.bin");
            cache = Path.Combine(directory, "BreezeAudio");
            http = client ?? Client;
            local = new LocalSpeech(speechDirectory);
        }

        public bool HasKey { get { return !String.IsNullOrEmpty(ReadKey()); } }
        public bool HasLocal { get { return local.IsInstalled; } }
        public bool HasQwen { get { return local.QwenInstalled; } }

        public void Close() { local.Dispose(); }

        public void SetLocalPlaybackActive(bool active) { local.SetPlaybackActive(active); }

        public void SaveKey(string key)
        {
            key = key.Trim();
            if (!key.StartsWith("brz_", StringComparison.Ordinal) || key.Any(Char.IsWhiteSpace))
                throw new ArgumentException("请输入完整的 BreezeBlue API 密钥。");
            Directory.CreateDirectory(Path.GetDirectoryName(keyFile));
            byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
            WriteAtomically(keyFile, encrypted, true);
        }

        private string ReadKey()
        {
            if (File.Exists(keyFile))
            {
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                        File.ReadAllBytes(keyFile), null, DataProtectionScope.CurrentUser));
                }
                catch (CryptographicException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (Environment.GetEnvironmentVariable("BREEZE_API_KEY") ?? "").Trim();
        }

        public Task<byte[]> SynthesizeAsync(string text, bool paragraphEnd, float speed, CancellationToken token, bool useLocal = false)
        {
            token.ThrowIfCancellationRequested();
            string input = SpeechText.ForSynthesis(text, paragraphEnd);
            bool chinese = SpeechText.HasHan(input);
            string instructions = chinese
                ? "用清晰自然的普通话读原文。叙述轻快但稳，句间自然连贯，轻收尾。只朗读原文，不添词。"
                : "Read the original text clearly, with a light, steady audiobook delivery. Do not add words.";
            var payload = new JavaScriptSerializer().Serialize(new {
                text = input, model_id = Model, language_code = chinese ? "zh" : "en",
                instructions = instructions, voice_settings = new { speed = useLocal ? 1f : speed }
            });
            string identity = useLocal ? local.Identity : VoiceId;
            return SynthesizeCachedAsync(text, paragraphEnd, useLocal ? speed : 1f, token, identity, payload,
                () => useLocal ? local.SynthesizeAsync(input, instructions, token) : SynthesizeCloudAsync(payload, token));
        }

        public Task<byte[]> SynthesizeQwenAsync(string text, bool paragraphEnd, float speed, CancellationToken token, NeuralVoice voice)
        {
            token.ThrowIfCancellationRequested();
            string input = SpeechText.ForSynthesis(text, paragraphEnd);
            bool chinese = SpeechText.HasHan(input);
            bool serena = voice == NeuralVoice.QwenSerena;
            string speaker = serena ? "serena" : "vivian";
            string style = serena ? "温柔自然，像讲故事一样朗读，句尾自然收音。" : "清亮自然，像讲故事一样朗读，句尾自然收音。";
            var payload = new JavaScriptSerializer().Serialize(new {
                input = input, voice = speaker, language = chinese ? "zh" : "en", response_format = "wav",
                instructions = chinese ? style : "A clear and natural storytelling delivery.",
                seed = 42
            });
            return SynthesizeCachedAsync(text, paragraphEnd, speed, token, local.QwenIdentity(speaker), payload,
                () => local.SynthesizeQwenAsync(payload, token));
        }

        private async Task<byte[]> SynthesizeCachedAsync(string text, bool paragraphEnd, float speed,
            CancellationToken token, string voice, string payload, Func<Task<byte[]>> synthesize)
        {
            string cachedFile;
            using (var hash = SHA256.Create())
            {
                string identity = "v1\n" + voice + "\n" + paragraphEnd + "\n" + payload;
                cachedFile = Path.Combine(cache, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)))
                    .Replace("-", "").ToLowerInvariant() + ".pcm");
            }
            string playbackKey = cachedFile + ":" + speed.ToString("R", CultureInfo.InvariantCulture);
            byte[] ready = ReadPlaybackClip(playbackKey);
            if (ready != null) return ready;

            var pcm = await Task.Run(() => ReadCached(cachedFile), token).ConfigureAwait(false);
            if (pcm == null)
            {
                pcm = await synthesize().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                pcm = NormalizePauses(pcm, SpeechText.TailPauseMs(text, paragraphEnd));
                SaveCached(cachedFile, pcm);
            }
            token.ThrowIfCancellationRequested();
            if (speed != 1f)
                pcm = await local.ChangeSpeedAsync(pcm, speed, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            StorePlaybackClip(playbackKey, pcm);
            return pcm;
        }

        private byte[] ReadPlaybackClip(string key)
        {
            lock (playbackCacheGate)
            {
                LinkedListNode<PlaybackClip> node;
                if (!playbackCache.TryGetValue(key, out node)) return null;
                playbackOrder.Remove(node);
                playbackOrder.AddLast(node);
                return node.Value.Audio;
            }
        }

        private void StorePlaybackClip(string key, byte[] pcm)
        {
            lock (playbackCacheGate)
            {
                if (playbackCache.ContainsKey(key) || pcm.Length > PlaybackCacheLimit) return;
                playbackCache.Add(key, playbackOrder.AddLast(new PlaybackClip(key, pcm)));
                playbackBytes += pcm.Length;
                while (playbackBytes > PlaybackCacheLimit)
                {
                    var oldest = playbackOrder.First.Value;
                    playbackOrder.RemoveFirst();
                    playbackCache.Remove(oldest.Key);
                    playbackBytes -= oldest.Audio.Length;
                }
            }
        }

        private sealed class PlaybackClip
        {
            public readonly string Key;
            public readonly byte[] Audio;

            public PlaybackClip(string key, byte[] audio) { Key = key; Audio = audio; }
        }

        private async Task<byte[]> SynthesizeCloudAsync(string payload, CancellationToken token)
        {
            string key = ReadKey();
            if (String.IsNullOrEmpty(key)) throw new InvalidOperationException("请先在听页的「Breeze 密钥」中配置 API 密钥。");
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.breeze.blue/v1/text-to-speech/" +
                VoiceId + "?output_format=pcm_24000"))
            {
                request.Headers.Add("xi-api-key", key);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                return await ReadAudioAsync(http, request, false, token).ConfigureAwait(false);
            }
        }

        internal static async Task<byte[]> ReadAudioAsync(HttpClient client, HttpRequestMessage request,
            bool useLocal, CancellationToken token, bool qwen = false)
        {
            string provider = qwen ? "本地 Qwen3-TTS" : useLocal ? "本地 Breeze" : "BreezeBlue";
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(useLocal ? 115 : 75));
                try
                {
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        VerifyResponse(response, useLocal, qwen, provider);
                        using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new MemoryStream())
                        using (timeout.Token.Register(body.Dispose))
                        {
                            var buffer = new byte[16384];
                            int read;
                            while ((read = await body.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                            {
                                if (output.Length + read > MaxAudioBytes) throw new IOException(provider + " 返回的音频过长。");
                                output.Write(buffer, 0, read);
                            }
                            var expected = response.Content.Headers.ContentLength;
                            if (expected.HasValue && expected.Value != output.Length)
                                throw new IOException(provider + " 返回的音频不完整。");
                            byte[] pcm = qwen ? ReadWave(output.ToArray()) : output.ToArray();
                            if (!ValidPcm(pcm)) throw new IOException(provider + " 返回的音频为空或不完整。");
                            return pcm;
                        }
                    }
                }
                catch (Exception)
                {
                    token.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested)
                        throw new TimeoutException(provider + " 合成超时，可以从当前段落重试。");
                    throw;
                }
            }
        }

        private static bool ValidPcm(byte[] data)
        {
            return data.Length > 0 && data.Length <= MaxAudioBytes && data.Length % 2 == 0;
        }

        private static void VerifyResponse(HttpResponseMessage response, bool useLocal, bool qwen, string provider)
        {
            if (!response.IsSuccessStatusCode)
            {
                if (useLocal && response.StatusCode == (qwen ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Conflict))
                    throw new LocalSpeechBusyException(provider);
                if (useLocal) throw new IOException(provider + " 合成失败（HTTP " + (int)response.StatusCode + "）。");
                switch ((int)response.StatusCode)
                {
                    case 401:
                    case 403: throw new InvalidOperationException("BreezeBlue 拒绝了请求，请检查密钥和音色权限。");
                    case 402: throw new InvalidOperationException("BreezeBlue 额度不足。");
                    case 429: throw new InvalidOperationException("BreezeBlue 请求受限，请稍后从当前段落继续。");
                    default: throw new IOException("BreezeBlue 合成失败（HTTP " + (int)response.StatusCode + "）。");
                }
            }
            if (qwen) return;
            var type = response.Content.Headers.ContentType;
            if (type == null || (type.MediaType != "application/octet-stream"
                && type.MediaType != "audio/pcm" && type.MediaType != "audio/raw"))
                throw new IOException("语音服务返回了非 PCM 音频。");
        }

        private static byte[] ReadWave(byte[] wave)
        {
            using (var stream = new MemoryStream(wave))
            using (var reader = new BinaryReader(stream))
            {
                if (wave.Length >= 12 && reader.ReadUInt32() == 0x46464952)
                {
                    reader.ReadUInt32();
                    bool format = false;
                    if (reader.ReadUInt32() == 0x45564157)
                    {
                        while (stream.Length - stream.Position >= 8)
                        {
                            uint chunk = reader.ReadUInt32();
                            uint size = reader.ReadUInt32();
                            if (size > stream.Length - stream.Position) break;
                            long next = stream.Position + size + (size & 1);
                            if (chunk == 0x20746D66 && size >= 16)
                            {
                                format = reader.ReadUInt16() == 1 && reader.ReadUInt16() == 1 &&
                                    reader.ReadUInt32() == SampleRate;
                                if (format)
                                {
                                    reader.ReadUInt32();
                                    format = reader.ReadUInt16() == 2 && reader.ReadUInt16() == 16;
                                }
                            }
                            if (chunk == 0x61746164 && format && size > 0 && size % 2 == 0)
                                return reader.ReadBytes((int)size);
                            stream.Position = next;
                        }
                    }
                }
            }
            throw new IOException("本地 Qwen3-TTS 未返回完整的 24 kHz 单声道音频。");
        }

        private static byte[] NormalizePauses(byte[] pcm, int tailMs)
        {
            // Work directly in little-endian PCM16; all offsets and lengths below are bytes.
            int first = 0;
            while (first < pcm.Length && QuietPcm(pcm, first)) first += 2;
            if (first == pcm.Length) throw new IOException("合成音频没有可播放的声音。");
            int end = pcm.Length;
            while (QuietPcm(pcm, end - 2)) end -= 2;
            int source = Math.Max(0, first - 2 * (SampleRate * 20 / 1000));
            int written = 0;
            int halfPause = 2 * (SampleRate * 60 / 1000);
            for (int i = first; i < end;)
            {
                if (!QuietPcm(pcm, i)) { i += 2; continue; }
                int start = i;
                do { i += 2; } while (i < end && QuietPcm(pcm, i));
                if (i - start <= 2 * (SampleRate * 170 / 1000)) continue;
                int length = start + halfPause - source;
                if (source != written) Buffer.BlockCopy(pcm, source, pcm, written, length);
                written += length;
                source = i - halfPause;
            }
            int tail = 2 * (SampleRate * tailMs / 1000);
            int remaining = Math.Min(pcm.Length, end + tail) - source;
            if (source != written) Buffer.BlockCopy(pcm, source, pcm, written, remaining);
            int retained = written + remaining;
            int padding = Math.Max(0, tail - (pcm.Length - end));
            Array.Resize(ref pcm, retained + padding);
            if (padding != 0) Array.Clear(pcm, retained, padding);
            return pcm;
        }

        private static bool QuietPcm(byte[] pcm, int offset)
        {
            int sample = unchecked((short)(pcm[offset] | pcm[offset + 1] << 8));
            return sample >= -32 && sample <= 32;
        }

        private static byte[] ReadCached(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > MaxAudioBytes) return null;
                byte[] data = File.ReadAllBytes(path);
                if (!ValidPcm(data)) return null;
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return data;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private void SaveCached(string path, byte[] data)
        {
            lock (diskCacheGate)
            {
                try
                {
                    Directory.CreateDirectory(cache);
                    var previous = new FileInfo(path);
                    long oldLength = previous.Exists ? previous.Length : 0;
                    if (oldLength > 0 && oldLength <= MaxAudioBytes && oldLength % 2 == 0) return;
                    WriteAtomically(path, data, true);
                    diskCacheBytes += data.Length - oldLength;
                    if (diskCacheBytes <= CacheLimit) return;

                    var files = new DirectoryInfo(cache).GetFiles("*.pcm");
                    long size = files.Sum(file => file.Length);
                    if (size > CacheLimit)
                    {
                        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc))
                        {
                            if (size <= CacheLimit - CacheTrimReserve) break;
                            long length = file.Length;
                            file.Delete();
                            size -= length;
                        }
                    }
                    diskCacheBytes = size;
                }
                catch (IOException) { diskCacheBytes = null; }
                catch (UnauthorizedAccessException) { diskCacheBytes = null; }
            }
        }

        private static void WriteAtomically(string path, byte[] data, bool replace)
        {
            string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(staging, data);
                if (!File.Exists(path)) File.Move(staging, path);
                else if (replace) File.Replace(staging, path, null);
            }
            finally { File.Delete(staging); }
        }
    }
}
