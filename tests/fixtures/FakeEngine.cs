using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
class FakeEngine {
 delegate bool Handler(uint t);
 static Handler handler = OnSignal;
 static string target;
 [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(Handler h, bool add);
 static bool OnSignal(uint t) { if (t == 1) { File.WriteAllText(target, "stopped"); Environment.Exit(0); return true; } return false; }
 static void Main() { target=Console.ReadLine(); SetConsoleCtrlHandler(handler,true); File.WriteAllText(target,"ready"); File.WriteAllText(target + ".pid", System.Diagnostics.Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(30000); }
}
