using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Tingye
{
    // Submit successive PCM buffers to one Windows audio device.
    internal sealed class PcmPlayback : IDisposable
    {
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));
        private static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WaveHeader), "Flags").ToInt32();
        private readonly Queue<Buffer> buffers = new Queue<Buffer>();
        private readonly DispatcherTimer timer;
        private readonly int sampleRate;
        private IntPtr device;
        private bool paused;

        public PcmPlayback(int sampleRate = BreezeVoice.SampleRate)
        {
            this.sampleRate = sampleRate;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += delegate { GetCompletedBuffers(); };
        }

        public int Pending { get { return buffers.Count; } }
        public event Action Drained;

        public Task Enqueue(byte[] audio, Action started)
        {
            return Enqueue(audio, 0, started);
        }

        public Task Enqueue(byte[] audio, int offset, Action started)
        {
            if (device == IntPtr.Zero)
            {
                var format = new WaveFormat {
                    Format = 1, Channels = 1, SamplesPerSecond = (uint)sampleRate,
                    BytesPerSecond = (uint)(sampleRate * 2), BlockAlign = 2, BitsPerSample = 16
                };
                IntPtr opened;
                Check(waveOutOpen(out opened, UInt32.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
                device = opened;
                if (paused) Check(waveOutPause(device));
            }

            var buffer = new Buffer(audio, offset, started);
            bool prepared = false;
            try
            {
                Check(waveOutPrepareHeader(device, buffer.Header, HeaderSize));
                prepared = true;
                Check(waveOutWrite(device, buffer.Header, HeaderSize));
            }
            catch
            {
                if (prepared) waveOutUnprepareHeader(device, buffer.Header, HeaderSize);
                buffer.Release();
                throw;
            }
            buffers.Enqueue(buffer);
            if (!paused)
            {
                StartFirst();
                timer.Start();
            }
            return buffer.Done.Task;
        }

        private void GetCompletedBuffers()
        {
            while (buffers.Count != 0)
            {
                var buffer = buffers.Peek();
                if ((Marshal.ReadInt32(buffer.Header, FlagsOffset) & 1) == 0) break; // WHDR_DONE
                uint result = waveOutUnprepareHeader(device, buffer.Header, HeaderSize);
                if (result != 0) break;
                buffers.Dequeue();
                buffer.Release();
                buffer.Done.TrySetResult(true);
                if (!paused) StartFirst();
            }
            if (buffers.Count == 0)
            {
                timer.Stop();
                if (Drained != null) Drained();
            }
        }

        private void StartFirst()
        {
            if (buffers.Count == 0) return;
            var buffer = buffers.Peek();
            var started = buffer.Started;
            buffer.Started = null;
            if (started != null) started();
        }

        public void Pause()
        {
            if (device != IntPtr.Zero) Check(waveOutPause(device));
            paused = true;
            timer.Stop();
        }

        public void Resume()
        {
            if (device != IntPtr.Zero) Check(waveOutRestart(device));
            paused = false;
            StartFirst();
            if (buffers.Count > 0) timer.Start();
        }

        public void Dispose()
        {
            timer.Stop();
            if (device == IntPtr.Zero) return;
            Check(waveOutReset(device));
            while (buffers.Count != 0)
            {
                var buffer = buffers.Dequeue();
                Check(waveOutUnprepareHeader(device, buffer.Header, HeaderSize));
                buffer.Release();
                buffer.Done.TrySetCanceled();
            }
            Check(waveOutClose(device));
            device = IntPtr.Zero;
        }

        private static void Check(uint result)
        {
            if (result != 0) throw new IOException("Windows 音频设备错误（" + result + "）。");
        }

        private sealed class Buffer
        {
            private GCHandle pcm;
            public readonly IntPtr Header;
            public Action Started;
            public readonly TaskCompletionSource<bool> Done =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Buffer(byte[] audio, int offset, Action started)
            {
                Started = started;
                pcm = GCHandle.Alloc(audio, GCHandleType.Pinned);
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader {
                    Data = IntPtr.Add(pcm.AddrOfPinnedObject(), offset), Length = (uint)(audio.Length - offset)
                }, Header, false);
            }

            public void Release()
            {
                Marshal.FreeHGlobal(Header);
                pcm.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort Format, Channels;
            public uint SamplesPerSecond, BytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint Length, Recorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr output, uint id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr output, IntPtr header, uint size);
        [DllImport("winmm.dll")] private static extern uint waveOutPause(IntPtr output);
        [DllImport("winmm.dll")] private static extern uint waveOutRestart(IntPtr output);
        [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr output);
        [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr output);
    }
}
