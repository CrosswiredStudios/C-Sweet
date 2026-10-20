namespace CSweet.TrustedServices;

/// <summary>Deletion boundary for host-selected owned children. Never traverses links.</summary>
public static class OwnedStorage
{
    public static string Child(string root, params string[] segments)
    {
        if (!Path.IsPathFullyQualified(root)) throw new IOException("An absolute storage root is required.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(root) == root) throw new IOException("A filesystem root cannot be a cleanup boundary.");
        var path = Path.GetFullPath(Path.Combine([root, .. segments]));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Cleanup target is outside its storage boundary.");
        for (var entry = new DirectoryInfo(Path.GetDirectoryName(path)!); entry is not null; entry = entry.Parent)
            RejectLink(entry);
        RejectLink(new FileInfo(path));
        return path;
    }

    public static void DeleteFile(string root, string path)
    {
        var resolved = Child(root, path);
        File.Delete(resolved);
    }

    public static void DeleteDirectory(string root, string path, CancellationToken token = default)
    {
        var resolved = Child(root, path);
        if (!Directory.Exists(resolved)) return;
        // Validate the entire tree before deleting any of it, then recheck each entry.
        Check(new DirectoryInfo(resolved));
        Delete(new DirectoryInfo(resolved));

        void Check(DirectoryInfo directory)
        {
            token.ThrowIfCancellationRequested();
            RejectLink(directory);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                RejectLink(entry);
                if (entry is DirectoryInfo child) Check(child);
            }
        }
        void Delete(DirectoryInfo directory)
        {
            token.ThrowIfCancellationRequested();
            RejectLink(directory);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                RejectLink(entry);
                if (entry is DirectoryInfo child) Delete(child);
                else { entry.Attributes &= ~FileAttributes.ReadOnly; entry.Delete(); }
            }
            directory.Delete();
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Cleanup refuses linked storage entries.");
    }
}
