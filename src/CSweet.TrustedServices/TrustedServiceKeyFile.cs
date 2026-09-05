using System.Security.Cryptography;

namespace CSweet.TrustedServices;

/// <summary>Installation-owned key exchange for trusted services on the private deployment network.</summary>
public static class TrustedServiceKeyFile
{
    public static string Read(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Trusted service key path must be absolute.");
        var key = File.ReadAllText(path).Trim();
        if (Convert.FromBase64String(key).Length < 32) throw new InvalidOperationException("Trusted service key is invalid.");
        return key;
    }

    public static string GetOrCreate(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Trusted service key path must be absolute.");
        if (File.Exists(path)) return Read(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                stream.Write(System.Text.Encoding.UTF8.GetBytes(key));
                stream.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            return Read(path);
        }
        finally { File.Delete(temporary); }
    }
}
