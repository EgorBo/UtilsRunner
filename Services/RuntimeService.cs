using System.Diagnostics;

public static class RuntimeService
{
    public static async Task Build(string workingDir, Action<string> logger)
    {
        // Not sure if this is needed
        await ProcUtils.Run("dotnet", "build-server shutdown");

        Stopwatch stopwatch = Stopwatch.StartNew();
        if (OperatingSystem.IsWindows())
        {
            // TODO: figure out why this is needed
            Dictionary<string, string> envVars = new();
            envVars["VCINSTALLDIR"] = Path.Combine(await GetVsPath(), @"VC\"); // has to end with "\"

            await ProcUtils.Run("cmd.exe", "/C build.cmd Clr -c Release", workingDir: workingDir, envVars: envVars);
            await ProcUtils.Run("cmd.exe", "/C build.cmd Libs -c Release", workingDir: workingDir, envVars: envVars);
            await ProcUtils.Run("cmd.exe", "/C build.cmd Release generatelayoutonly",
                workingDir: Path.Combine(workingDir, "src", "tests"), envVars: envVars);
        }
        else
        {
            await ProcUtils.Run("bash", "build.sh Clr -c Release", workingDir: workingDir);
            logger($"Runtime took {stopwatch.Elapsed.TotalMinutes:F0} min to build");
            stopwatch.Restart();
            await ProcUtils.Run("bash", "build.sh Libs -c Release", workingDir: workingDir);
            logger($"Libs took {stopwatch.Elapsed.TotalMinutes:F0} min to build");
            stopwatch.Stop();
            await ProcUtils.Run("bash", "build.sh Release generatelayoutonly",
                workingDir: Path.Combine(workingDir, "src", "tests"));
        }

        // Not sure if this is needed
        await ProcUtils.Run("dotnet", "build-server shutdown");
        logger("runtime build finished.");
    }

    // Workaround for ^
    private static async Task<string> GetVsPath()
    {
        Debug.Assert(OperatingSystem.IsWindows());
        var path = await ProcUtils.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe"),
            "-latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath");
        if (!Directory.Exists(path))
            throw new InvalidOperationException("Failed to find Visual Studio");
        return path;
    }
}
