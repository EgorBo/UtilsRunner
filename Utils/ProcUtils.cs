using System.Diagnostics;
using System.Text;

internal static class ProcUtils
{
    public static async Task<string> Run(
        string path,
        string args = "",
        Dictionary<string, string>? envVars = null,
        string? workingDir = null,
        CancellationToken cancellationToken = default)
    {
        Logger.Debug($"\nExecuting a command in directory \"{workingDir}\":\n\t{path} {args}\nEnv.vars:\n{DumpEnvVars(envVars)}");

        var logger = new StringBuilder();
        Process? process = null;
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                Arguments = args,
            };

            if (workingDir != null)
                processStartInfo.WorkingDirectory = workingDir;

            if (envVars != null)
            {
                foreach (var envVar in envVars)
                    processStartInfo.EnvironmentVariables[envVar.Key] = envVar.Value;
            }

            cancellationToken.ThrowIfCancellationRequested();
            process = Process.Start(processStartInfo)!;
            process.EnableRaisingEvents = true;
            cancellationToken.ThrowIfCancellationRequested();

            process.ErrorDataReceived += (sender, e) =>
            {
                Logger.Error(e.Data);
            };
            process.OutputDataReceived += (sender, e) =>
            {
                Logger.Info(e.Data);
                logger.AppendLine(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            cancellationToken.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ExitCode: {process.ExitCode}");

            return logger.ToString().Trim('\r', '\n');
        }
        finally
        {
            // Just to make sure the process is killed
            try
            {
                if (process is { HasExited: false })
                    process.Kill();
            }
            catch {}
        }
    }

    private static string DumpEnvVars(Dictionary<string, string>? envVars)
    {
        if (envVars == null)
            return "";

        string envVar = "";
        foreach (var ev in envVars)
            envVar += $"{ev.Key}={ev.Value}\n";
        return envVar;
    }
}