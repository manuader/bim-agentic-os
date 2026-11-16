#nullable enable
using System.Text.Json;

namespace Bim.Cli;

// P10-21 `jurisdiction-resolve`. Stub (red): not implemented yet.
internal static class JurisdictionResolveCommand
{
    public static void Run(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = "jurisdiction_resolve_not_implemented" }));
        Environment.ExitCode = 1;
    }
}
