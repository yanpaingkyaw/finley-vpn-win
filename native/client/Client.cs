using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;

namespace Finley {
    internal static class Client {
        static int Main() {
            using (Stream input = Console.OpenStandardInput()) using (Stream output = Console.OpenStandardOutput()) {
                try {
                    WindowsSecurity.EnsureServiceRunning();
                    using (var pipe = new NamedPipeClientStream(".", Wire.Pipe, (PipeAccessRights)0x12019b, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None)) {
                        pipe.Connect(5000);
                        try { WindowsSecurity.VerifyServer(pipe); } catch (Fault) { throw; } catch { throw new Fault("SERVICE_IDENTITY_FAILED", "Finley could not verify its Windows service. Ask an administrator to repair Finley."); }
                        int done = 0;
                        // Separate readers preserve duplex cancellation and owner-loss notification.
                        var reader = new Thread(() => {
                            try { while (Volatile.Read(ref done) == 0) { var value = Wire.Read(pipe, 20000); Wire.Write(output, value); } }
                            catch { } finally { Interlocked.Exchange(ref done, 1); pipe.Dispose(); input.Dispose(); }
                        }) { IsBackground = true };
                        reader.Start();
                        try { while (Volatile.Read(ref done) == 0) Wire.Write(pipe, Wire.Read(input, 15000)); }
                        finally { Interlocked.Exchange(ref done, 1); pipe.Dispose(); }
                    }
                    return 0;
                } catch (Fault error) { try { Wire.Write(output, Wire.Error("bridge", error.Code, error.Message)); } catch { } return 1; }
                catch { try { Wire.Write(output, Wire.Error("bridge", "SERVICE_UNAVAILABLE", "Finley Tunnel Service is unavailable. Ask an administrator to install or repair Finley.")); } catch { } return 1; }
            }
        }
    }
}
