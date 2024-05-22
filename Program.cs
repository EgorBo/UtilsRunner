﻿using System.CommandLine;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Octokit;
using System.IO.Compression;

internal class Program
{
    public const string RepoOwner = "dotnet";
    public const string RepoName = "runtime";

    static async Task<int> Main(string[] args)
    {
        var artficatsOpt = new Option<string>(name: "--artifacts");
        var ghIssueOpt = new Option<int>(name: "--issue");
        var azContainerOpt = new Option<string>(name: "--az_container");
        var azCsOpt = new Option<string>(name: "--az_cs");
        var ghTokenOpt = new Option<string>(name: "--gh_token");

        var rootCommand = new RootCommand();
        var publishCommand = new Command("publish", "publish BDN results on GH")
        {
            artficatsOpt,
            ghIssueOpt,
            azCsOpt,
            azContainerOpt,
            ghTokenOpt,
        };
        rootCommand.AddCommand(publishCommand);
        publishCommand.SetHandler(async (artifacts, issue, azToken, azContainer, ghToken) =>
            {
                if (!Directory.Exists(artifacts))
                    throw new ArgumentException($"{artifacts} was not found");

                // First, upload the BDN artifacts to Azure Blob
                var zipFile = Path.Combine(Path.GetDirectoryName(artifacts)!, "BDN_Artifacts.zip");
                File.Delete(zipFile);
                ZipFile.CreateFromDirectory(artifacts, zipFile);
                var artifactsUrl = await UploadFileToAzure(azToken, azContainer, zipFile);

                string reply = "## Results\n";
                foreach (var resultsMd in Directory.GetFiles(artifacts, "*-report-github.md", SearchOption.AllDirectories))
                    reply += PrettifyMarkdown(await File.ReadAllLinesAsync(resultsMd)) + "\n\n";
                reply += $"See [BDN_Artifacts.zip]({artifactsUrl}) for details.";

                string baseHotFuncs = Path.Combine(artifacts, "base_functions.txt");
                string diffHotFuncs = Path.Combine(artifacts, "diff_functions.txt");
                string baseHotAsm = Path.Combine(artifacts, "base.asm");
                string diffHotAsm = Path.Combine(artifacts, "diff.asm");
                string baseFlame = Path.Combine(artifacts, "base_flamegraph.svg");
                string diffFlame = Path.Combine(artifacts, "diff_flamegraph.svg");

                var gtApp = "EgorBot";
                if (File.Exists(baseHotFuncs))
                {
                    try
                    {
                        reply += $"\n\n## 🔥 Profiler (`perf record`):\n";
                        reply += "<details>\n<summary>click me!</summary>\n\n### Annotated asm\n\n";
                        reply += $"[base_functions.txt]({await CreateGistAsync(gtApp, ghToken, "base_functions.txt", ReadContentSafe(baseHotFuncs))}) vs ";
                        reply += $"[diff_functions.txt]({await CreateGistAsync(gtApp, ghToken, "diff_functions.txt", ReadContentSafe(diffHotFuncs))})\n";
                        reply += $"[base_asm.asm]({await CreateGistAsync(gtApp, ghToken, "base_asm.asm", ReadContentSafe(baseHotAsm))}) vs ";
                        reply += $"[diff_asm.asm]({await CreateGistAsync(gtApp, ghToken, "diff_asm.asm", ReadContentSafe(diffHotAsm))})";

                        if (File.Exists(baseFlame) || File.Exists(diffFlame))
                        {
                            reply += "\n\n### Flamegraphs\n\n";
                            if (File.Exists(baseFlame))
                            {
                                string url = await UploadFileToAzure(azToken, azContainer, baseFlame);
                                reply += $"[base_flamegraph.svg]({url}) vs ";
                            }
                            if (File.Exists(diffFlame))
                            {
                                string url = await UploadFileToAzure(azToken, azContainer, diffFlame);
                                reply += $"[diff_flamegraph.svg]({url})";
                            }
                        }
                        reply += "\n\n_NOTE: for clean `perf` results, make sure you have just one `[Benchmark]` in your app._\n\n";
                        reply += "_NOTE: `perf` is not working correctly on Arm64 Azure VMs :'(_\n\n";
                        reply += "</details>\n\n";
                    }
                    catch
                    {
                    }
                }

                await CommentOnGithub(gtApp, ghToken, issue, reply);
            },
            artficatsOpt, ghIssueOpt, azCsOpt, azContainerOpt, ghTokenOpt);

        // Gosh, how I hate System.CommandLine for verbosity...
        return await rootCommand.InvokeAsync(args);
    }

    private static string ReadContentSafe(string file)
    {
        try
        {
            if (File.Exists(file))
                return File.ReadAllText(file);
            return "<error>";
        }
        catch (Exception e)
        {
            return e.ToString();
        }
    }

    private static string PrettifyMarkdown(string[] lines)
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

            if (line.StartsWith("Job-"))
                line = "  " + line;

            // Workaround for BDN's bug: https://github.com/dotnet/BenchmarkDotNet/issues/2545
            if (line.EndsWith(":|-"))
                line = line.Remove(line.Length - 1);

            // Rename coreruns
            line = line.Replace("/core_root_base/corerun", "Main")
                .Replace("/core_root_diff/corerun", "**PR**");
            content += line + "\n";
        }
        return content;
    }

    private static async Task<string> UploadFileToAzure(string azureCs, string containerName, string file)
    {
        // Upload to Azure Blob Storage
        var blobServiceClient = new BlobServiceClient(azureCs);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(Path.GetFileName(file));
        await using (FileStream uploadFileStream = File.OpenRead(file))
            await blobClient.UploadAsync(uploadFileStream, true);

        string contentType = "application/zip";
        if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            contentType = "image/png";
        if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            contentType = "image/svg+xml";

        await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = contentType });
        return blobClient.Uri.AbsoluteUri;
    }

    private static async Task<string> CreateGistAsync(string githubApp, string githubCreds, string fileName, string content)
    {
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
        await client.Issue.Comment.Create(RepoOwner, RepoName, issueId, comment);
    }
}
