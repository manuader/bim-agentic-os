namespace Bim.Revit;
internal static class NativeEndpoint
{
    internal static string ForProcess(int processId)
    {
        if(processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        return "bim-agentic-os-2025-" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
