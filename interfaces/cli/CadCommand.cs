#nullable enable
using System.Text.Json;

namespace Bim.Cli;

// P6-07 stub: cad-draft / cad-brief (implementation follows).
internal static class CadCommand
{
    public static void Run(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = "cad_not_implemented" }));
        Environment.ExitCode = 1;
    }
}
