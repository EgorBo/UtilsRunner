﻿using System.CommandLine;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Octokit;
using System.IO.Compression;

internal class Program
{
    static async Task<int> Main(string[] args)
    {
        var fileOpt = new Option<string>(name: "--file");
        var artficatsOpt = new Option<string>(name: "--artifacts");
        var ghIssueOpt = new Option<int>(name: "--issue");
        var azContainerOpt = new Option<string>(name: "--az_container");
        var azCsOpt = new Option<string>(name: "--az_cs");
        var cpuOpt = new Option<string>(name: "--cpu", () => "");
        var ghTokenOpt = new Option<string>(name: "--gh_token");
        var jobIdOpt = new Option<string>(name: "--jobid", () => "");
        var isPrOpt = new Option<bool>(name: "--ispr", () => true);

        var rootCommand = new RootCommand();
        var publishCommand = new Command("publish", "publish BDN results on GH")
        {
            artficatsOpt,
            ghIssueOpt,
            azCsOpt,
            azContainerOpt,
            cpuOpt,
            ghTokenOpt,
            jobIdOpt,
            isPrOpt
        };
        rootCommand.AddCommand(publishCommand);

        var uploadCommand = new Command("upload", "upload file to Azure")
        {
            fileOpt,
            azCsOpt,
            azContainerOpt,
        };
        rootCommand.AddCommand(uploadCommand);

        uploadCommand.SetHandler(async (file, azToken, azContainer) =>
        {
            string id = Guid.NewGuid().ToString("N").Substring(0, 8);
            var uploadedUrl = await UploadFileToAzure(azToken, azContainer, file, id);
            Console.WriteLine(uploadedUrl);
        }, fileOpt, azCsOpt, azContainerOpt);

        publishCommand.SetHandler(async (artifacts, issue, azToken, azContainer, cpu, ghToken, jobId, isPr) =>
            {
                if (!Directory.Exists(artifacts))
                    throw new ArgumentException($"{artifacts} was not found");

                string id = string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString("N").Substring(0, 8) : jobId;

                // First, upload the BDN artifacts to Azure Blob
                var zipFile = Path.Combine(Path.GetDirectoryName(artifacts)!, "BDN_Artifacts.zip");
                File.Delete(zipFile);
                ZipFile.CreateFromDirectory(artifacts, zipFile);
                var artifactsUrl = await UploadFileToAzure(azToken, azContainer, zipFile, id);

                if (string.IsNullOrWhiteSpace(cpu))
                    cpu = "Intel";

                string reply = $"## Benchmark results on `{cpu}`\n  \n";
                foreach (var resultsMd in Directory.GetFiles(artifacts, "*-report-github.md",
                             SearchOption.AllDirectories))
                {
                    reply += PrettifyMarkdown(await File.ReadAllLinesAsync(resultsMd), isPr, cpu) + "\n\n";
                }

                reply += $"[BDN_Artifacts.zip]({artifactsUrl})";

                var gtApp = "EgorBot";
                foreach (string subDir in Directory.GetDirectories(artifacts))
                {
                    var benchName = Path.GetFileName(subDir);
                    if (!benchName.StartsWith("PerfBench__"))
                    {
                        continue;
                    }

                    string baseHotFuncs = Path.Combine(subDir, "base_functions.txt");
                    string diffHotFuncs = Path.Combine(subDir, "diff_functions.txt");
                    string baseHotAsm = Path.Combine(subDir, "base.asm");
                    string diffHotAsm = Path.Combine(subDir, "diff.asm");
                    string baseStat = Path.Combine(subDir, "base.stats");
                    string diffStat = Path.Combine(subDir, "diff.stats");
                    string baseFlame = Path.Combine(subDir, "base_flamegraph.svg");
                    string diffFlame = Path.Combine(subDir, "diff_flamegraph.svg");

                    benchName = benchName.Replace("PerfBench__", "");
                    reply += $"\n#### Profile for `{benchName}`:\n";

                    if (File.Exists(baseHotFuncs) && File.Exists(diffHotFuncs))
                    {
                        try
                        {
                            reply += $"\n\nFlame graphs: [Main]({await UploadFileToAzure(azToken, azContainer, baseFlame, id)}) vs ";
                            reply += $"[PR]({await UploadFileToAzure(azToken, azContainer, diffFlame, id)}) 🔥\n";

                            reply += $"Hot asm: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_asm_{id}.asm", ReadContentSafe(baseHotAsm))}) vs ";
                            reply += $"[PR]({await CreateGistAsync(gtApp, ghToken, $"diff_asm_{id}.asm", ReadContentSafe(diffHotAsm))})\n";

                            reply += $"Hot functions: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_functions_{id}.txt", ReadContentSafe(baseHotFuncs))}) vs ";
                            reply += $"[PR]({await CreateGistAsync(gtApp, ghToken, $"diff_functions_{id}.txt", ReadContentSafe(diffHotFuncs))})\n";

                            reply += $"Counters: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_counters_{id}.txt", ReadContentSafe(baseStat))}) vs ";
                            reply += $"[PR]({await CreateGistAsync(gtApp, ghToken, $"diff_counters_{id}.txt", ReadContentSafe(diffStat))})\n";
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine(exc.ToString());
                        }
                    }
                    else if (File.Exists(baseHotFuncs))
                    {
                        try
                        {
                            reply += $"\n\nFlame graphs: [Main]({await UploadFileToAzure(azToken, azContainer, baseFlame, id)}) 🔥\n";
                            reply += $"Hot asm: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_asm_{id}.asm", ReadContentSafe(baseHotAsm))})\n";
                            reply += $"Hot functions: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_functions_{id}.txt", ReadContentSafe(baseHotFuncs))})\n";
                            reply += $"Counters: [Main]({await CreateGistAsync(gtApp, ghToken, $"base_counters_{id}.txt", ReadContentSafe(baseStat))})\n";
                        }
                        catch (Exception exc)
                        {
                            Console.WriteLine(exc.ToString());
                        }
                    }

                    reply += "\n\n\n";
                }
                await CommentOnGithub(gtApp, ghToken, issue, reply);
            },
            artficatsOpt, ghIssueOpt, azCsOpt, azContainerOpt, cpuOpt, ghTokenOpt, jobIdOpt, isPrOpt);

        // Gosh, how I hate System.CommandLine for verbosity...
        return await rootCommand.InvokeAsync(args);
    }

    private static string ReadContentSafe(string file)
    {
        try
        {
            if (File.Exists(file))
                return File.ReadAllText(file);
            return "<file was not found>";
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    private static string PrettifyMarkdown(string[] lines, bool isPr, string cpu)
    {
        string content = "";
        foreach (string i in lines)
        {
            // Remove some noise
            string line = i.Trim();
            if (string.IsNullOrEmpty(line) ||
                line.StartsWith(".NET SDK ") ||
                line.StartsWith("[Host]"))
                continue;

            line = line.Replace("Unknown processor", cpu);

            if (line.StartsWith("Job-"))
                line = "  " + line;

            // Workaround for BDN's bug: https://github.com/dotnet/BenchmarkDotNet/issues/2545
            if (line.EndsWith(":|-"))
                line = line.Remove(line.Length - 1);

            // Rename coreruns
            line = line.Replace("/core_root_base/corerun", isPr ? "Main" : "Before")
                .Replace("/core_root_diff/corerun", isPr ? "PR" : "After");
            content += line + "\n";
        }
        return content;
    }

    private static async Task<string> UploadFileToAzure(string azureCs, string containerName, string file, string id)
    {
        if (!File.Exists(file))
            return "file.notfound";

        // Upload to Azure Blob Storage
        var blobServiceClient = new BlobServiceClient(azureCs);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

        var ext = Path.GetExtension(file);
        var filename = Path.GetFileNameWithoutExtension(file);
        var blobClient = containerClient.GetBlobClient(filename + $"_{id}" + ext);
        await using (FileStream uploadFileStream = File.OpenRead(file))
            await blobClient.UploadAsync(uploadFileStream, true);

        string contentType = "application/zip";
        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            contentType = "image/png";
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            contentType = "image/svg+xml";
        if (file.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            contentType = "text/plain";

        await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = contentType });
        return blobClient.Uri.AbsoluteUri;
    }

    private static async Task<string> CreateGistAsync(string githubApp, string githubCreds, string fileName, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "file.notfound";
        GitHubClient client = new(new ProductHeaderValue(githubApp));
        client.Credentials = new Credentials(githubCreds);
        var gist = new NewGist();
        gist.Description = fileName;
        gist.Public = false;
        gist.Files.Add(fileName, content);
        var result = await client.Gist.Create(gist);
        return result.HtmlUrl;
    }

    private static async Task CommentOnGithub(string githubApp, string githubCreds, int issueId, string comment)
    {
        GitHubClient client = new(new ProductHeaderValue(githubApp));
        client.Credentials = new Credentials(githubCreds);
        await client.Issue.Comment.Create("EgorBot", "runtime-utils", issueId, comment);
    }
}
