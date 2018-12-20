using System.Diagnostics;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MessagePack.CodeGenerator
{
    internal static class ProcessUtil
    {
        public static async Task<int> ExecuteProcessAsync(string fileName, string args, Stream stdout, Stream stderr, TextReader stdin, CancellationToken ct = default(CancellationToken))
        {
            var psi = new ProcessStartInfo(fileName, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardError = stderr != null;
            psi.RedirectStandardOutput = stdout != null;
            psi.RedirectStandardInput = stdin != null;
            using (var proc = new Process())
            using (var cts = new CancellationTokenSource())
            using (var exitedct = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, ct))
            {
                proc.StartInfo = psi;
                proc.EnableRaisingEvents = true;
                proc.Exited += (sender, ev) =>
                {
                    cts.Cancel();
                };
                if (!proc.Start())
                {
                    throw new InvalidOperationException($"failed to start process(fileName = {fileName}, args = {args})");
                }
                Console.WriteLine($"process begin {proc.Id}");
                int exitCode = 0;
                await Task.WhenAll(
                    Task.Run(() =>
                    {
                        exitCode = StdinTask(proc, stdin, exitedct, cts);
                        if(exitCode < 0)
                        {
                            proc.Dispose();
                        }
                    })
                    ,
                    Task.Run(async () =>
                    {
                        if (stdout != null)
                        {
                            await RedirectOutputTask(proc.StandardOutput.BaseStream, stdout, exitedct.Token, "stdout");
                        }
                    })
                    ,
                    Task.Run(async () =>
                    {
                        if (stderr != null)
                        {
                            await RedirectOutputTask(proc.StandardError.BaseStream, stderr, exitedct.Token, "stderr");
                        }
                    })
                );
                Console.WriteLine($"await end");
                if(exitCode >= 0)
                {
                    Console.WriteLine($"exited");
                    return proc.ExitCode;
                }
                else
                {
                    Console.WriteLine($"cancelled");
                    return -1;
                }
            }
        }
        static int StdinTask(Process proc, TextReader stdin, CancellationTokenSource exitedct, CancellationTokenSource cts)
        {
            if (stdin != null)
            {
                while (!exitedct.Token.IsCancellationRequested)
                {
                    var l = stdin.ReadLine();
                    if (l == null)
                    {
                        break;
                    }
                    proc.StandardInput.WriteLine(l);
                }
                proc.StandardInput.Dispose();
            }
            exitedct.Token.WaitHandle.WaitOne();
            if (cts.IsCancellationRequested)
            {
                Console.WriteLine($"process exited");
                proc.WaitForExit();
                var exitCode = proc.ExitCode;
                return exitCode;
            }
            else
            {
                Console.WriteLine($"cancelled");
                proc.StandardOutput.Dispose();
                proc.StandardError.Dispose();
                proc.Kill();
                Console.WriteLine($"dispose end");
                return -1;
            }
        }

        static async Task RedirectOutputTask(Stream procStdout, Stream stdout, CancellationToken ct, string suffix)
        {
            if (stdout != null)
            {
                var buf = new byte[1024];
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var bytesread = await procStdout.ReadAsync(buf, 0, 1024, ct).ConfigureAwait(false);
                        if(bytesread <= 0)
                        {
                            break;
                        }
                        stdout.Write(buf, 0, bytesread);
                        // var l = await procStdout.ReadLineAsync();
                        // if(l == null)
                        // {
                        //     break;
                        // }
                        // var bytesread = Encoding.UTF8.GetBytes(l, 0, l.Length, buf, 0);
                        // stdout.Write(buf, 0, bytesread);
                    }
                    catch(NullReferenceException e)
                    {
                        Console.WriteLine($"{suffix}: {e}");
                        break;
                    }
                    catch(ObjectDisposedException e)
                    {
                        Console.WriteLine($"{suffix}: {e}");
                        break;
                    }
                }
                Console.WriteLine($"loop end:{suffix}");
            }
        }

    }
}