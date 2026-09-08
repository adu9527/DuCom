using System.IO;
using System.IO.Pipes;
using System.Text;
using DuCom.Plugin;

namespace DuCom.PluginWorker;

/// <summary>
/// Worker-mode entry for the self-hosted plugin runner. Runs inside the sandboxed child
/// process (the same executable as the host, spawned with --plugin-worker). Reads the
/// one-time startup configuration from stdin (never from the command line), connects to
/// the host's named pipe, and hands control to the SDK runner which loads the plugin
/// assembly in its own AssemblyLoadContext.
/// </summary>
internal static class PluginWorkerMain
{
    public static int Run()
    {
        string tracePath = Path.Combine(Path.GetTempPath(), $"ducom-worker-trace-{Environment.ProcessId}.log");
        try
        {
            File.WriteAllText(tracePath, $"entered {DateTime.Now:O}\n");
            string json = ReadStdinToEnd();
            File.AppendAllText(tracePath, $"startup-json-bytes={json.Length}\n");
            PluginWorkerStartup startup = PluginWorkerStartup.ParseJson(json);
            File.AppendAllText(tracePath, $"pipe={startup.PipeName} dir={startup.PluginDirectory}\n");
            using NamedPipeClientStream pipe = new(
                ".",
                startup.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect((int)TimeSpan.FromSeconds(15).TotalMilliseconds);
            File.AppendAllText(tracePath, "connected\n");
            int exit = PluginWorkerRunner.RunAsync(pipe, startup).GetAwaiter().GetResult();
            File.AppendAllText(tracePath, $"exited {exit}\n");
            return exit;
        }
        catch (Exception exception)
        {
            try
            {
                File.AppendAllText(tracePath, $"FATAL {exception}\n");
            }
            catch (Exception)
            {
            }

            return -1;
        }
    }

    private static string ReadStdinToEnd()
    {
        using Stream stdin = Console.OpenStandardInput();
        using MemoryStream buffer = new();
        stdin.CopyTo(buffer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
