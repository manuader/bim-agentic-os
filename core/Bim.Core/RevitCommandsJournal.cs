namespace Bim.Core;

// P2-05 stub (red): query.journal types only; the catalogue entry and the reply are not implemented yet.
public sealed record JournalQueryArgs(long? AfterSequence, bool Peek, int MaxEntries) : RevitCommandArgs
{
    public MirrorJournalReadOptions Options => throw new NotImplementedException("query.journal");
}

public static class RevitJournalQuery
{
    public const string Command = "query.journal";
    public static string Body(bool attached, MirrorJournal journal, MirrorJournalReport report) => throw new NotImplementedException("query.journal");
    public static string RawRead(MirrorJournalTracker tracker, bool attached, MirrorSnapshot snapshot, string documentPath, MirrorJournalReadOptions options) => throw new NotImplementedException("query.journal");
    public static string Read(MirrorJournalTracker tracker, bool attached, MirrorSnapshot snapshot, string documentPath, JournalQueryArgs args) => throw new NotImplementedException("query.journal");
}

public static partial class RevitCommandCatalogue
{
    public static IReadOnlyList<RevitCommandSpec> Journal { get; } = Array.Empty<RevitCommandSpec>();
}
