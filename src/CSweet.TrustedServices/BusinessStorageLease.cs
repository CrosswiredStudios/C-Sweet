namespace CSweet.TrustedServices;

/// <summary>Cross-process fence shared by Git operations, backup workers and deletion.</summary>
public static class BusinessStorageLease
{
    public static FileStream Enter(string root, Guid business, bool deleting = false)
    {
        if (business == Guid.Empty) throw new ArgumentException("A business identity is required.");
        var directory = OwnedStorage.Child(root, ".business-cleanup");
        Directory.CreateDirectory(directory);
        var marker = OwnedStorage.Child(root, ".business-cleanup", business.ToString("N") + ".deleted");
        var path = OwnedStorage.Child(root, ".business-cleanup", business.ToString("N") + ".lock");
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            deleting ? FileShare.None : FileShare.ReadWrite);
        try
        {
            if (deleting)
            {
                using var tombstone = new FileStream(marker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                tombstone.Flush(true);
            }
            else if (File.Exists(marker)) throw new IOException("Business storage is being deleted.");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
}
