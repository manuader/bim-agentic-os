#nullable enable
using System.Text.Json;

namespace Bim.Cli;

// P10-20 `bcf-export issues.json output.bcfzip|output.bcf`. Stub (red): not implemented yet.
internal static class BcfExportCommand
{
    public static void Run(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = "bcf_export_not_implemented" }));
        Environment.ExitCode = 1;
    }
}
