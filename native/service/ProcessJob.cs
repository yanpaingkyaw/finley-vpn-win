using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Finley {
    internal sealed class ProcessJob : IDisposable {
        readonly object sync = new object();
        IntPtr handle;
        [StructLayout(LayoutKind.Sequential)] struct Basic { internal long ProcessTime, JobTime; internal uint Flags; internal UIntPtr Minimum, Maximum; internal uint Active; internal UIntPtr Affinity; internal uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] struct Io { internal ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] struct Limits { internal Basic Basic; internal Io Io; internal UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(IntPtr job, int kind, ref Limits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(IntPtr job, uint code);
        [StructLayout(LayoutKind.Sequential)] struct Accounting { internal long User, Kernel, PeriodUser, PeriodKernel; internal uint Faults, Total, Active, Terminated; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryInformationJobObject(IntPtr job, int kind, out Accounting value, uint size, IntPtr returned);
        internal ProcessJob() {
            handle = CreateJobObject(IntPtr.Zero, null); if (handle == IntPtr.Zero) throw new Win32Exception();
            var limits = new Limits(); limits.Basic.Flags = 0x2000;
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(Limits)))) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }
        internal void Assign(Process child) { lock (sync) { if (handle == IntPtr.Zero || !AssignProcessToJobObject(handle, child.Handle)) throw new Win32Exception(); } }
        internal void Terminate() { lock (sync) { if (handle != IntPtr.Zero && !TerminateJobObject(handle, 2)) throw new Win32Exception(); } }
        internal void Drain() {
            lock (sync) {
            if (handle == IntPtr.Zero) throw new InvalidOperationException("Job was closed before cleanup verification.");
            Terminate(); DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (true) {
                Accounting value;
                if (!QueryInformationJobObject(handle, 1, out value, (uint)Marshal.SizeOf(typeof(Accounting)), IntPtr.Zero)) throw new Win32Exception();
                if (value.Active == 0) return;
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("Tunnel processes did not exit.");
                System.Threading.Thread.Sleep(20);
            }
            }
        }
        public void Dispose() { lock (sync) { if (handle != IntPtr.Zero) { WindowsSecurity.CloseHandle(handle); handle = IntPtr.Zero; } } }
    }
}
