namespace Bim.Core;

// P2-06 stub: API surface only (RED step).
public sealed record ProvenanceEntry(long Sequence, Guid ElementId, ProvenanceSource Source, string PrevHash, string Hash);

public sealed record ProvenanceAppendResult(string Code, ProvenanceEntry? Entry, string? Detail)
{
    public bool Ok => Code is ProvenanceCodes.Appended or ProvenanceCodes.Replayed;
}

public sealed record ProvenanceLoadResult(string Code, ProvenanceLedger? Ledger, long? Sequence, string? Detail)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public sealed record ProvenanceQueryResult(string Code, IReadOnlyList<ProvenanceEntry> Entries)
{
    public bool Ok => Code == ProvenanceCodes.Ok;
}

public static class ProvenanceLedgerFormat
{
    public const int Version = 1;
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public static string ComputeHash(ProvenanceEntry entry) => "";
    public static string Write(IEnumerable<ProvenanceEntry> entries) => "";
}

public sealed class ProvenanceLedger
{
    public int Count => 0;
    public string HeadHash => ProvenanceLedgerFormat.GenesisHash;
    public IReadOnlyList<ProvenanceEntry> Entries() => Array.Empty<ProvenanceEntry>();
    public ProvenanceAppendResult Append(Guid elementId, ProvenanceSource? source) => new("not_implemented", null, null);
    public IReadOnlyList<ProvenanceEntry> ByElement(Guid elementId) => Array.Empty<ProvenanceEntry>();
    public ProvenanceQueryResult BySource(string? kind, string? sourceId = null) => new("not_implemented", Array.Empty<ProvenanceEntry>());
    public string Serialize() => "";
    public static ProvenanceLoadResult Load(string? json) => new("not_implemented", null, null, null);
}
