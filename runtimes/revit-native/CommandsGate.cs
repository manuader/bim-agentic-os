using Bim.Core;
namespace Bim.Revit;

// Red step: signatures only.
public static class AddInGate
{
    public const string ConfigVariable = "BIM_AGENTIC_TEST_CONFIG";
    public static bool PipeAllowed(string? configVariable, bool bootstrapCompleted) => throw new NotImplementedException();
    public static bool IsLegacyRead(string? operation) => throw new NotImplementedException();
    public static void CheckLegacyWrite(string? requestPath, CommandDocumentFacts document, Func<RevitCommandPolicy> policy, RevitFileSystemProbe probe) => throw new NotImplementedException();
}

public static class CommandRules
{
    public static bool ReportsWallTypeWidth(string wallKind) => throw new NotImplementedException();
    public static void CheckWallTypeForCreate(string wallKind) => throw new NotImplementedException();
    public static void CheckOpeningHost(string wallKind, bool locationLineIsWallCenterline) => throw new NotImplementedException();
    public static bool OffersFloorType(bool isFoundationSlab) => throw new NotImplementedException();
}
