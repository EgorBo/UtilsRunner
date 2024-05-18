class Program2
{
    private static readonly string AzureBlobCS = Environment.GetEnvironmentVariable("EGORBOT_AZURE_BLOB_CS");
    private static readonly string BaseWorkingDir = Environment.GetEnvironmentVariable("EGORBOT_WORKDIR")!;
    private static readonly string PrPatchUrl = Environment.GetEnvironmentVariable("EGORBOT_PATCHURL")!;
    private static readonly string GithubBotName = "EgorBot";
    private static readonly string GithubAppName = "EgorPerfBot";
    private static readonly string GithubRepoName = "runtime";
    private static readonly string GithubRepoOwner = "dotnet";

    static async Task Main()
    {
        try
        {
            // Get the patch
            string patch = await new HttpClient().GetStringAsync(PrPatchUrl);
            if (string.IsNullOrWhiteSpace(patch))
                throw new InvalidOperationException("Failed to get the patch");

            // Build the coreroots
            var (baseCoreRoot, diffCoreRoot) = await BuildCoreRuns(BaseWorkingDir, patch,
                msg => { });
            Logger.Info("Build finished!");
        }
        catch (Exception e)
        {
            var errorText = e.InnerException?.Message ?? e.Message;
            Logger.Error(errorText);
        }
    }

    static async Task BuildBenchmark(string benchmarkDir, string snippet)
    {
        try
        {
            if (Directory.Exists(benchmarkDir))
                Directory.Delete(benchmarkDir, true);
            Directory.CreateDirectory(benchmarkDir);

            await ProcUtils.Run("dotnet", "new console", workingDir: benchmarkDir);
            await ProcUtils.Run("dotnet", "add package BenchmarkDotNet", workingDir: benchmarkDir);
            await File.WriteAllTextAsync(Path.Combine(benchmarkDir, "Program.cs"), snippet);
            await ProcUtils.Run("dotnet", "build -c Release", workingDir: benchmarkDir);
        }
        catch (Exception exc)
        {
            throw new ArgumentException("Benchmark failed to build", exc);
        }
    }

    static async Task<BenchmarkResult> RunBenchmark(string benchmarkDir, string baseCoreRoot, string diffCoreRoot, string extraArgs)
    {
        string bdnArgs =
            // Run all benchmarks
            "--filter \"*\" " +
            // Hide some useless columns
            "-h Job StdDev RatioSD Median Error " +
            // Provide our corerun paths
            $"--coreRun \"{baseCoreRoot}\" \"{diffCoreRoot}\"" +
            // Custom args
            extraArgs;

        await ProcUtils.Run("dotnet", "run -c Release -- " + bdnArgs.Trim(' ', '\r', '\n', '\t'),
            cancellationToken: new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token,
            workingDir: benchmarkDir);

        string[] markdownResults = Directory.GetFiles(Path.Combine(benchmarkDir, "BenchmarkDotNet.Artifacts", "results"), "*.md");

        if (markdownResults.Length == 0)
            throw new InvalidOperationException("No *.md files found");

        string resultsContent = markdownResults
            .Where(md => md.EndsWith("-github.md", StringComparison.OrdinalIgnoreCase))
            .Aggregate("", (current, md) =>
                current + $"{string.Join('\n', File.ReadAllLines(md).Where(l => !string.IsNullOrWhiteSpace(l)))}\n  \n");

        string asmContent = markdownResults
            .Where(md => md.EndsWith("-asm.md", StringComparison.OrdinalIgnoreCase))
            .Aggregate("", (current, md) =>
                current + $"{string.Join('\n', File.ReadAllLines(md).Where(l => !string.IsNullOrWhiteSpace(l)))}\n  \n");

        return new BenchmarkResult(resultsContent, asmContent);
    }

    static async Task<(string, string)> BuildCoreRuns(string baseWorkDir, string patch, Action<string> logger)
    {
        string runtimeDir = baseWorkDir;
        Directory.CreateDirectory(runtimeDir);
        if (Directory.Exists(Path.Combine(runtimeDir, "runtime")))
        {
            // Reset
            runtimeDir = Path.Combine(runtimeDir, "runtime");
            await GitServices.ResetAndUpdateRepo(runtimeDir);
        }
        else
        {
            // Clone repo
            await GitServices.CloneRepo($"https://github.com/{GithubRepoOwner}/{GithubRepoName}.git", runtimeDir);
            runtimeDir = Path.Combine(runtimeDir, "runtime");
        }

        // Build repo
        await RuntimeService.Build(runtimeDir, logger);

        // To avoid detecting arch/config, we'll just take the first folder
        string[] dirs = Directory.GetDirectories(Path.Combine(runtimeDir, "artifacts", "tests", "coreclr"));
        string dir = dirs.Single(dir => dir.EndsWith(".Release"));

        string coreRootDir = Path.Combine(dir, "Tests");
        string coreRoot = Path.Combine(coreRootDir, "Core_Root");

        if (Directory.Exists(Path.Combine(coreRootDir, "Main")))
            Directory.Delete(Path.Combine(coreRootDir, "Main"), true);
        if (Directory.Exists(Path.Combine(coreRootDir, "PR")))
            Directory.Delete(Path.Combine(coreRootDir, "PR"), true);

        Directory.Move(coreRoot, Path.Combine(coreRootDir, "Main"));

        // Apply the patch and rebuild
        await GitServices.ApplyPatch(patch, runtimeDir);
        await RuntimeService.Build(runtimeDir, logger);

        Directory.Move(coreRoot, Path.Combine(coreRootDir, "PR"));

        string baseCoreRoot = Path.Combine(Path.Combine(coreRootDir, "Main"), "corerun");
        string diffCoreRoot = Path.Combine(Path.Combine(coreRootDir, "PR"), "corerun");

        if (OperatingSystem.IsWindows())
        {
            baseCoreRoot += ".exe";
            diffCoreRoot += ".exe";
        }
        return (baseCoreRoot, diffCoreRoot);
    }
}

record BenchmarkResult(string Markdown, string Asm);
