public static class GitServices
{
    public static async Task CloneRepo(string repoUrl, string workingDir)
    {
        await ProcUtils.Run("git", $"clone {repoUrl}", workingDir: workingDir);
    }

    public static async Task ResetAndUpdateRepo(string workingDir)
    {
        // Remove all changes and untracked files
        await ProcUtils.Run("git", "checkout .", workingDir: workingDir);
        await ProcUtils.Run("git", "clean -f", workingDir: workingDir);

        // Pull the latest changes
        await ProcUtils.Run("git", "pull origin main", workingDir: workingDir);
    }

    public static async Task ApplyPatch(string patchContent, string workingDir)
    {
        string fileName = Guid.NewGuid().ToString("N") + ".patch";
        await File.WriteAllTextAsync(Path.Combine(workingDir, fileName), patchContent);
        await ProcUtils.Run("git", "apply " + fileName, workingDir: workingDir);
        File.Delete(Path.Combine(workingDir, fileName));
    }
}
