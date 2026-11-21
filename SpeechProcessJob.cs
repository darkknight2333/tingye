using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tingye
{
    // Keep the local model and any workers it launches tied to the reader's lifetime.
    internal sealed class SpeechProcessJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private const int ExtendedLimitInformation = 9;
        private const uint LimitPriorityClass = 0x20;
        private const uint KillOnClose = 0x2000;

        private SpeechProcessJob() : base(true) { }

        public static void CheckAvailableMemory(long modelBytes)
        {
            const ulong runtimeReserve = 768UL * 1024 * 1024;
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            ulong required = (ulong)modelBytes + runtimeReserve;
            if (GlobalMemoryStatusEx(ref memory) &&
                (memory.AvailablePhysical < required || memory.AvailableCommit < required))
                throw new IOException("当前可用内存不足以启动本地大模型。可先选择「自动匹配」使用轻量语音。");
        }

        public static SpeechProcessJob Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                job.Dispose();
                throw error;
            }
            try
            {
                var limits = new ExtendedLimits {
                    Basic = new BasicLimits {
                        Flags = KillOnClose | LimitPriorityClass,
                        Priority = (uint)ProcessPriorityClass.BelowNormal
                    }
                };
                if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return job;
            }
            catch
            {
                job.Dispose();
                throw;
            }
        }

        public void Add(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override bool ReleaseHandle() { return CloseHandle(handle); }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            public uint Length, Load;
            public ulong TotalPhysical, AvailablePhysical, TotalCommit, AvailableCommit;
            public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SpeechProcessJob CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SpeechProcessJob job, int type, ref ExtendedLimits info, int length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SpeechProcessJob job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
