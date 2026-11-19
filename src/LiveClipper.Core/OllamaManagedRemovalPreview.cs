namespace LiveClipper.Core;

public sealed record OllamaManagedRemovalFile(string RelativePath, long SizeBytes);

public sealed record OllamaManagedRemovalPlan(
    string Status,
    bool PlanValid,
    string Reason,
    string? ManifestRelativePath,
    IReadOnlyList<OllamaManagedRemovalFile> Files,
    long TotalBytes)
{
    public bool PreviewOnly => true;
    public bool CanUninstall => false;
    public string UncoveredSafetyChecks =>
        "Process ownership, running-file safety, and mutual exclusion with installation/removal tasks are not checked. " +
        "This filesystem snapshot is not transactional and never authorizes deletion.";
}

public static class OllamaManagedRemovalPreview
{
    public const string PreviewOnly = "preview-only";
    public const string Blocked = "blocked";

    public static async Task<OllamaManagedRemovalPlan> PreviewAsync(
        string privateRoot, CancellationToken token = default)
    {
        // Inspect owns root validation, manifest trust, and declared-file hashing.
        var identity = await OllamaManagedInstallation.InspectAsync(privateRoot, token);
        token.ThrowIfCancellationRequested();
        if (identity.Status != OllamaManagedInstallation.ManagedValid)
            return new(identity.Status == OllamaManagedInstallation.NotInstalled
                    ? OllamaManagedInstallation.NotInstalled : Blocked,
                false, identity.Reason, null, Array.Empty<OllamaManagedRemovalFile>(), 0);

        OllamaManagedRemovalPlan Reject(string reason) =>
            new(Blocked, false, reason, null, Array.Empty<OllamaManagedRemovalFile>(), 0);

        var program = identity.UninstallPreviewDirectory!;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var declaredFiles = new HashSet<string>(identity.VerifiedFiles, comparer);
        var manifestPath = Path.Combine(program, OllamaManagedInstallation.ManifestFileName);
        declaredFiles.Add(manifestPath);
        var declaredDirectories = new HashSet<string>(comparer) { program };
        foreach (var file in identity.VerifiedFiles)
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(program, file);
            // Data locations are never traversed, even if a manifest tries to claim one inside program.
            if (relative.Split(Path.DirectorySeparatorChar).Any(part =>
                    part.Equals(OllamaManagedInstallation.ModelsDirectoryName, StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("cache", StringComparison.OrdinalIgnoreCase)))
                return Reject("data-path-in-program: " + Relative(file));
            for (var parent = Path.GetDirectoryName(file); parent is not null && !comparer.Equals(parent, program);
                 parent = Path.GetDirectoryName(parent))
                declaredDirectories.Add(parent);
        }

        string Relative(string path) => Path.GetRelativePath(program, path).Replace(Path.DirectorySeparatorChar, '/');

        try
        {
            var pending = new Stack<string>();
            pending.Push(program);
            var seen = new HashSet<string>(comparer);
            var files = new List<OllamaManagedRemovalFile>();
            long totalBytes = 0;
            while (pending.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                var directoryAttributes = File.GetAttributes(directory);
                if ((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                    return Reject("program-path-is-reparse-point: " + Relative(directory));
                if ((directoryAttributes & FileAttributes.Directory) == 0)
                    return Reject("program-path-is-not-directory: " + Relative(directory));
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        return Reject("program-path-is-reparse-point: " + Relative(path));
                    if ((attributes & FileAttributes.Device) != 0)
                        return Reject("program-path-is-not-regular: " + Relative(path));
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!declaredDirectories.Contains(path))
                            return Reject("undeclared-program-directory: " + Relative(path));
                        pending.Push(path);
                        continue;
                    }
                    if (!declaredFiles.Contains(path))
                        return Reject("undeclared-program-file: " + Relative(path));
                    if (!seen.Add(path)) return Reject("duplicate-program-file: " + Relative(path));
                    var size = new FileInfo(path).Length;
                    totalBytes = checked(totalBytes + size);
                    files.Add(new(Relative(path), size));
                }
            }
            if (!seen.SetEquals(declaredFiles)) return Reject("declared-program-file-missing");
            token.ThrowIfCancellationRequested();
            return new(PreviewOnly, true, "managed-file-plan-only; runtime-and-transaction-gates-not-checked",
                OllamaManagedInstallation.ManifestFileName,
                files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList().AsReadOnly(), totalBytes);
        }
        catch (IOException) { return Reject("preview-io-error"); }
        catch (UnauthorizedAccessException) { return Reject("preview-access-denied"); }
        catch (OverflowException) { return Reject("preview-size-overflow"); }
    }
}
