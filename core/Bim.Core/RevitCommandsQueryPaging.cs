namespace Bim.Core;

// P1-15 query.elements client paging (stub, red step).
public static class RevitElementQueryPagingCodes
{
    public const string CommandNotPageable = "paging_command_invalid";
    public const string CursorLoop = "cursor_loop";
    public const string PageLimitExceeded = "page_limit_exceeded";
}

public sealed record ElementQueryAllResult(string Status, string Code, string? Field, string? Detail, int Pages, int? Matched, IReadOnlyList<string> Elements)
{
    public bool IsSuccess => Status == RevitCommandResponse.Succeeded;
    public string ToJson() => "{}";
}

public static class RevitElementQueryPaging
{
    public const int MaxPages = 1000;

    public static ElementQueryAllResult All(string envelopeJson, IRevitCommandTransport transport, int maxPages = MaxPages) =>
        new(RevitCommandResponse.Failed, "not_implemented", null, null, 0, null, Array.Empty<string>());
}

public sealed class RevitServiceTransport : IRevitCommandTransport
{
    public RevitServiceTransport(string pipeName, RevitCommandPolicy policy, Func<string, string, string?> pipe) { }
    public string? Send(string envelopeJson) => null;
}
