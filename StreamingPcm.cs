using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Tingye
{
    internal static class StreamingPcm
    {
        // A quiet run is committed when its next voiced sample arrives. This keeps the
        // same leading, internal and trailing pauses as the full-clip cache format.
        public static async Task<byte[]> ReadAsync(Stream body, int tailMs, Func<byte[], Task> deliver, CancellationToken token)
        {
            const int bytesPerMs = BreezeVoice.SampleRate * 2 / 1000;
            using (var raw = new MemoryStream())
            using (var pending = new MemoryStream())
            {
                var block = new byte[16384];
                int offset = 0, lastSignalEnd = 0, read;
                while ((read = await body.ReadAsync(block, 0, block.Length, token).ConfigureAwait(false)) != 0)
                {
                    if (raw.Length + read > BreezeVoice.MaxAudioBytes) throw new IOException("本地 Breeze 返回的音频过长。");
                    raw.Write(block, 0, read);
                    byte[] data = raw.GetBuffer();
                    int end = (int)raw.Length & ~1;
                    while (offset < end)
                    {
                        if (BreezeVoice.QuietPcm(data, offset)) { offset += 2; continue; }
                        int quiet = offset - lastSignalEnd;
                        if (lastSignalEnd == 0)
                        {
                            int keep = Math.Min(quiet, 20 * bytesPerMs);
                            pending.Write(data, offset - keep, keep);
                        }
                        else if (quiet > 170 * bytesPerMs)
                        {
                            pending.Write(data, lastSignalEnd, 60 * bytesPerMs);
                            pending.Write(data, offset - 60 * bytesPerMs, 60 * bytesPerMs);
                        }
                        else pending.Write(data, lastSignalEnd, quiet);
                        int start = offset;
                        do { offset += 2; } while (offset < end && !BreezeVoice.QuietPcm(data, offset));
                        pending.Write(data, start, offset - start);
                        lastSignalEnd = offset;
                    }
                    if (pending.Length >= 12288)
                    {
                        await deliver(pending.ToArray()).ConfigureAwait(false);
                        pending.SetLength(0);
                    }
                }
                if (raw.Length % 2 != 0 || lastSignalEnd == 0) throw new IOException("本地 Breeze 返回的音频为空或不完整。");
                var tail = new byte[tailMs * bytesPerMs];
                Buffer.BlockCopy(raw.GetBuffer(), lastSignalEnd, tail, 0, Math.Min(tail.Length, (int)raw.Length - lastSignalEnd));
                pending.Write(tail, 0, tail.Length);
                await deliver(pending.ToArray()).ConfigureAwait(false);
                return raw.ToArray();
            }
        }
    }
}
