using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// The helper owns a private hidden Windows console, so CTRL_BREAK only reaches
// this helper and its child. No configuration is put in process arguments.
internal static class FinleyTunnelHelper {
    private delegate bool ConsoleHandler(uint type);
    private static readonly ConsoleHandler handler = type => type == 0 || type == 1;
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AllocConsole();
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetConsoleCtrlHandler(ConsoleHandler callback, bool add);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
    private static readonly object sync = new object();
    private static volatile bool stopping;
    private static int cleanupFailed;
    private static Process child;
    private static void Write(string s) { lock(sync) { Console.Out.WriteLine(s); Console.Out.Flush(); } }
    private static void StopChild() {
        lock(sync) { if (stopping) return; stopping = true; }
        try {
            if (child.HasExited) return;
            if (!GenerateConsoleCtrlEvent(1, 0)) { Interlocked.Exchange(ref cleanupFailed, 1); Write("CLEANUP_UNCONFIRMED"); try { child.Kill(); } catch { } return; }
            if (!child.WaitForExit(10000)) {
                Interlocked.Exchange(ref cleanupFailed, 1);
                Write("CLEANUP_UNCONFIRMED");
                try { child.Kill(); } catch { }
            }
        } catch { Interlocked.Exchange(ref cleanupFailed, 1); Write("CLEANUP_UNCONFIRMED"); }
    }
    private static int Main(string[] args) {
        if (args.Length != 1 || !File.Exists(args[0])) { Write("ERROR engine unavailable"); return 1; }
        // The redirected stdin/stdout are pipes, and survive console detachment.
        FreeConsole();
        if (!AllocConsole()) { Write("ERROR console unavailable"); return 1; }
        var console = GetConsoleWindow(); if (console != IntPtr.Zero) ShowWindow(console, 0);
        if (!SetConsoleCtrlHandler(handler, true)) { Write("ERROR console handler unavailable"); return 1; }
        string config = Console.In.ReadLine();
        if (String.IsNullOrEmpty(config) || config.Length > 1024 * 1024) { Write("ERROR invalid configuration"); return 1; }
        try {
            child = new Process();
            child.StartInfo = new ProcessStartInfo { FileName = args[0], Arguments = "run -c stdin", UseShellExecute = false, CreateNoWindow = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(args[0]) };
            child.OutputDataReceived += (sender, e) => { }; child.ErrorDataReceived += (sender, e) => { };
            if (!child.Start()) { Write("ERROR engine could not start"); return 1; }
            child.BeginOutputReadLine(); child.BeginErrorReadLine();
            child.StandardInput.WriteLine(config); child.StandardInput.Close(); config = null;
            var listener = new Thread(() => { try { string command; while ((command = Console.In.ReadLine()) != null) { if (command == "STOP") { StopChild(); return; } } StopChild(); } catch { StopChild(); } });
            listener.IsBackground = true; listener.Start();
            Write("READY");
            child.WaitForExit();
            // StopChild's wait can race with process exit: wait briefly for the
            // listener to finish reporting a failed cleanup before returning.
            if (stopping) listener.Join(11000);
            Write("EXIT " + child.ExitCode);
            return cleanupFailed == 0 ? 0 : 2;
        } catch { if (child != null) { try { if (!child.HasExited) StopChild(); } catch { } } Write("ERROR engine could not start"); return 1; }
    }
}
