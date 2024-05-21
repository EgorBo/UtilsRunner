using System.CommandLine;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Octokit;
using System.IO.Compression;
using static System.Runtime.InteropServices.JavaScript.JSType;

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
        var gistTokenOpt = new Option<string>(name: "--gist_token");
        var ghAppNameOpt = new Option<string>(name: "--gh_appname");

        var rootCommand = new RootCommand();
        var publishCommand = new Command("publish", "publish BDN results on GH")
        {
            artficatsOpt,
            ghIssueOpt,
            azCsOpt,
            azContainerOpt,
            ghTokenOpt,
            gistTokenOpt,
            ghAppNameOpt
        };
        rootCommand.AddCommand(publishCommand);
        publishCommand.SetHandler(async (artifacts, issue, azToken, azContainer, ghToken, gistToken, gtApp) =>
            {
                if (!Directory.Exists(artifacts))
                    throw new ArgumentException($"{artifacts} was not found");

                // First, upload the BDN artifacts to Azure Blob
                var zipFile = Path.Combine(Path.GetDirectoryName(artifacts)!, "BDN_Artifacts.zip");
                File.Delete(zipFile);
                ZipFile.CreateFromDirectory(artifacts, zipFile);
                var artifactsUrl = await UploadZipToAzure(azToken, azContainer, zipFile);

                string reply = "";
                foreach (var resultsMd in Directory.GetFiles(artifacts, "*-report-github.md", SearchOption.AllDirectories))
                    reply += PrettifyMarkdown(await File.ReadAllLinesAsync(resultsMd)) + "\n---\n";
                reply += $"Check [BDN_Artifacts.zip]({artifactsUrl}) for details.";

                string baseHotFuncs = Path.Combine(artifacts, "base_functions.txt");
                string diffHotFuncs = Path.Combine(artifacts, "diff_functions.txt");
                string baseHotAsm = Path.Combine(artifacts, "base.asm");
                string diffHotAsm = Path.Combine(artifacts, "diff.asm");

                if (File.Exists(baseHotFuncs))
                {
                    reply += $"<br/>Profiler's (`perf record`) output:";
                    reply += $"<br/>({await CreateGistAsync(gtApp, gistToken, "base_functions.txt", File.ReadAllText(baseHotFuncs))})";
                    reply += $"<br/>({await CreateGistAsync(gtApp, gistToken, "diff_functions.txt", File.ReadAllText(diffHotFuncs))})";
                    reply += $"<br/>({await CreateGistAsync(gtApp, gistToken, "base_asm.asm", File.ReadAllText(baseHotAsm))})";
                    reply += $"<br/>({await CreateGistAsync(gtApp, gistToken, "diff_asm.asm", File.ReadAllText(diffHotAsm))})";
                }

                await CommentOnGithub(gtApp, ghToken, issue, reply);
            },
            artficatsOpt, ghIssueOpt, azCsOpt, azContainerOpt, ghTokenOpt, gistTokenOpt, ghAppNameOpt);

        // Gosh, how I hate System.CommandLine for verbosity...
        return await rootCommand.InvokeAsync(args);
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

    private static async Task<string> UploadZipToAzure(string azureCs, string containerName, string file)
    {
        // Upload to Azure Blob Storage
        var blobServiceClient = new BlobServiceClient(azureCs);
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(Path.GetFileName(file));
        await using (FileStream uploadFileStream = File.OpenRead(file))
            await blobClient.UploadAsync(uploadFileStream, true);
        await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = "application/zip" });
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
