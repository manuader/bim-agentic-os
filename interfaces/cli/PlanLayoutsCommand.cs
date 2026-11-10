#nullable enable
using System.Text.Json;

namespace Bim.Cli;

// P7-11 `plan-layouts brief.json output.json [--alternatives n] [--seed s]`: stub (red phase), refuses every call.
internal static class PlanLayoutsCommand
{
    public static void Run(string[] args)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", code = "plan_layouts_not_implemented", arguments = args.Length }));
        Environment.ExitCode = 1;
    }
}
