using System.Buffers.Binary;
using System.Text;

namespace Bim.Core;

// Minimal deterministic ZIP writer for BCF archives: every entry is STORED (method 0, no compression), so the bytes do
// not depend on the runtime's deflate implementation; version made by / needed are fixed at 20 with host MS-DOS (0),
// general-purpose flags, internal and external attributes are 0, no extra fields or comments, and every entry carries
// the same DOS date/time (2-second resolution: odd seconds round down). Names must be relative ASCII paths.
public static class BcfZip
{
    private const ushort Version = 20;
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static (ushort Time, ushort Date) DosDateTime(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        if (utc.Year < 1980 || utc.Year > 2107) throw new BcfExportException(BcfCodes.OptionsInvalid, "timestamp outside 1980..2107");
        return ((ushort)((utc.Hour << 11) | (utc.Minute << 5) | (utc.Second / 2)), (ushort)(((utc.Year - 1980) << 9) | (utc.Month << 5) | utc.Day));
    }

    public static byte[] Write(IReadOnlyList<BcfEntry> entries, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > ushort.MaxValue) throw new BcfExportException(BcfCodes.TotalTooLarge, "too many zip entries");
        var (time, date) = DosDateTime(timestamp);
        using var output = new MemoryStream();
        var central = new List<(byte[] Name, uint Crc, uint Size, uint Offset)>(entries.Count);
        Span<byte> header = stackalloc byte[30];
        Span<byte> centralHeader = stackalloc byte[46];
        foreach (var entry in entries)
        {
            var name = Encoding.ASCII.GetBytes(entry.Path);
            if (name.Length == 0 || name.Length > ushort.MaxValue || entry.Path.Any(c => c > 0x7E || c < 0x20) || entry.Path.StartsWith('/') || entry.Path.Contains('\\', StringComparison.Ordinal))
                throw new BcfExportException(BcfCodes.OptionsInvalid, "invalid zip entry name");
            if ((ulong)output.Length + (ulong)entry.Content.Length + 30UL + (ulong)name.Length > uint.MaxValue) throw new BcfExportException(BcfCodes.TotalTooLarge, "archive exceeds 4 GiB");
            var crc = Crc32(entry.Content);
            var size = (uint)entry.Content.Length;
            var offset = (uint)output.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], date);
            BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[18..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(header[22..], size);
            BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);
            output.Write(header);
            output.Write(name);
            output.Write(entry.Content);
            central.Add((name, crc, size, offset));
        }
        var centralOffset = (uint)output.Length;
        foreach (var (name, crc, size, offset) in central)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[4..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[6..], Version);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[8..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[10..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[12..], time);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[14..], date);
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader[16..], crc);
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader[20..], size);
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader[24..], size);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[28..], (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[30..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[32..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[34..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(centralHeader[36..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader[38..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(centralHeader[42..], offset);
            output.Write(centralHeader);
            output.Write(name);
        }
        var centralSize = (uint)((uint)output.Length - centralOffset);
        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[4..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[6..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)central.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)central.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], centralOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(end[20..], 0);
        output.Write(end);
        return output.ToArray();
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
