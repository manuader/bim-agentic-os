#nullable enable
using System.Text.Json;

namespace Bim.Cli;

// P7-09 `generate-prototypes request.json output.json` (stub: not implemented yet).
internal static class GenerateCommand
{
    public static void Run(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = "generate_prototypes_not_implemented" }));
        Environment.ExitCode = 1;
    }
}
