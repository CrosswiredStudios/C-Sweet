using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CSweet.AgentRuntime.Core;

/// <summary>
/// Minimal, deterministic ISO-9660 media containing exactly one validated agent
/// bundle. The virtual DVD is a hypervisor-enforced read-only transport; this is
/// deliberately not a general-purpose ISO parser or authoring surface.
/// </summary>
public static class SingleFileIso9660
{
    public const int SectorSize = 2048;
    public const string ArtifactFileName = "ARTIFACT.CSAB;1";
    private const uint PathTableLittleEndianSector = 18;
    private const uint PathTableBigEndianSector = 19;
    private const uint RootDirectorySector = 20;
    private const uint ArtifactSector = 21;

    public static async Task WriteAsync(
        Stream artifact,
        long artifactLength,
        Stream output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(output);
        if (!artifact.CanRead || !output.CanWrite) throw new ArgumentException("The ISO streams have invalid access modes.");
        if (artifactLength is < 1 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(artifactLength));
        var artifactSectors = checked((uint)((artifactLength + SectorSize - 1) / SectorSize));
        var volumeSectors = checked(ArtifactSector + artifactSectors);
        var zero = new byte[SectorSize];
        for (var sector = 0; sector < 16; sector++) await output.WriteAsync(zero, cancellationToken);
        await output.WriteAsync(CreatePrimaryVolumeDescriptor(volumeSectors, checked((uint)artifactLength)), cancellationToken);
        await output.WriteAsync(CreateTerminator(), cancellationToken);
        await output.WriteAsync(CreatePathTable(littleEndian: true), cancellationToken);
        await output.WriteAsync(CreatePathTable(littleEndian: false), cancellationToken);
        await output.WriteAsync(CreateRootDirectory(checked((uint)artifactLength)), cancellationToken);

        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (copied < artifactLength)
        {
            var read = await artifact.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, artifactLength - copied)),
                cancellationToken);
            if (read == 0) throw new EndOfStreamException("The artifact ended before its declared length.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
        }
        var padding = checked((int)(artifactSectors * SectorSize - artifactLength));
        if (padding > 0) await output.WriteAsync(zero.AsMemory(0, padding), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    public static async Task<bool> VerifyArtifactDigestAsync(
        string isoPath,
        string expectedDigest,
        CancellationToken cancellationToken = default)
    {
        if (!IsDigest(expectedDigest) || string.IsNullOrWhiteSpace(isoPath) || !Path.IsPathFullyQualified(isoPath))
            return false;
        try
        {
            await using var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length < (ArtifactSector + 1L) * SectorSize) return false;
            var pvd = new byte[SectorSize];
            input.Position = 16L * SectorSize;
            await input.ReadExactlyAsync(pvd, cancellationToken);
            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001" || pvd[6] != 1)
                return false;
            var rootExtent = BinaryPrimitives.ReadUInt32LittleEndian(pvd.AsSpan(158, 4));
            if (rootExtent != RootDirectorySector) return false;
            var root = new byte[SectorSize];
            input.Position = rootExtent * (long)SectorSize;
            await input.ReadExactlyAsync(root, cancellationToken);
            var record = FindArtifactRecord(root);
            if (record is null || record.Value.Extent != ArtifactSector || record.Value.Length < 1 ||
                record.Value.Extent * (long)SectorSize + record.Value.Length > input.Length)
                return false;
            input.Position = record.Value.Extent * (long)SectorSize;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long remaining = record.Value.Length;
            while (remaining > 0)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) return false;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            var actual = "sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset());
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedDigest));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static byte[] CreatePrimaryVolumeDescriptor(uint volumeSectors, uint artifactLength)
    {
        var sector = new byte[SectorSize];
        sector[0] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
        sector[6] = 1;
        WritePaddedAscii(sector, 8, 32, "CSWEET");
        WritePaddedAscii(sector, 40, 32, "CSWEET_AGENT_ARTIFACT");
        WriteBothEndian(sector, 80, volumeSectors);
        WriteBothEndian(sector, 120, (ushort)1);
        WriteBothEndian(sector, 124, (ushort)1);
        WriteBothEndian(sector, 128, (ushort)SectorSize);
        WriteBothEndian(sector, 132, 10u);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(140, 4), PathTableLittleEndianSector);
        BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(148, 4), PathTableBigEndianSector);
        CreateDirectoryRecord(RootDirectorySector, SectorSize, directory: true, [0]).CopyTo(sector, 156);
        WritePaddedAscii(sector, 190, 128, "C-SWEET");
        WritePaddedAscii(sector, 318, 128, "C-SWEET");
        WritePaddedAscii(sector, 446, 128, "C-SWEET");
        WritePaddedAscii(sector, 574, 128, "CSWEET AGENT ARTIFACT");
        sector[881] = 1;
        _ = artifactLength;
        return sector;
    }

    private static byte[] CreateTerminator()
    {
        var sector = new byte[SectorSize];
        sector[0] = 255;
        Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
        sector[6] = 1;
        return sector;
    }

    private static byte[] CreatePathTable(bool littleEndian)
    {
        var sector = new byte[SectorSize];
        sector[0] = 1;
        if (littleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(2, 4), RootDirectorySector);
            BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(6, 2), 1);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(sector.AsSpan(2, 4), RootDirectorySector);
            BinaryPrimitives.WriteUInt16BigEndian(sector.AsSpan(6, 2), 1);
        }
        return sector;
    }

    private static byte[] CreateRootDirectory(uint artifactLength)
    {
        var sector = new byte[SectorSize];
        var current = CreateDirectoryRecord(RootDirectorySector, SectorSize, directory: true, [0]);
        var parent = CreateDirectoryRecord(RootDirectorySector, SectorSize, directory: true, [1]);
        var file = CreateDirectoryRecord(
            ArtifactSector, artifactLength, directory: false, Encoding.ASCII.GetBytes(ArtifactFileName));
        current.CopyTo(sector, 0);
        parent.CopyTo(sector, current.Length);
        file.CopyTo(sector, current.Length + parent.Length);
        return sector;
    }

    private static byte[] CreateDirectoryRecord(uint extent, uint length, bool directory, byte[] name)
    {
        var recordLength = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0);
        var record = new byte[recordLength];
        record[0] = checked((byte)recordLength);
        WriteBothEndian(record, 2, extent);
        WriteBothEndian(record, 10, length);
        record[18] = 126;
        record[19] = 1;
        record[20] = 1;
        record[25] = directory ? (byte)2 : (byte)0;
        WriteBothEndian(record, 28, (ushort)1);
        record[32] = checked((byte)name.Length);
        name.CopyTo(record, 33);
        return record;
    }

    private static (uint Extent, uint Length)? FindArtifactRecord(byte[] directory)
    {
        var offset = 0;
        while (offset < directory.Length && directory[offset] != 0)
        {
            var length = directory[offset];
            if (length < 34 || offset + length > directory.Length) return null;
            var nameLength = directory[offset + 32];
            if (33 + nameLength > length) return null;
            var name = Encoding.ASCII.GetString(directory, offset + 33, nameLength);
            if (name == ArtifactFileName)
                return (
                    BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 2, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 10, 4)));
            offset += length;
        }
        return null;
    }

    private static void WritePaddedAscii(byte[] target, int offset, int length, string value)
    {
        target.AsSpan(offset, length).Fill((byte)' ');
        var count = Math.Min(length, value.Length);
        Encoding.ASCII.GetBytes(value.AsSpan(0, count), target.AsSpan(offset, count));
    }

    private static void WriteBothEndian(byte[] target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), value);
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset + 4, 4), value);
    }

    private static void WriteBothEndian(byte[] target, int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset + 2, 2), value);
    }

    private static bool IsDigest(string value) => value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept("0123456789abcdef") < 0;
}
