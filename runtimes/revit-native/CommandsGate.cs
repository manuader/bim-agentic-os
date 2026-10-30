using Bim.Core;
namespace Bim.Revit;

// Add-in exposure and legacy-operation decisions. Autodesk-free on purpose: tests/integration/RevitCommandGuard.Tests
// source-links this file; App.cs and CommandsExecutor.cs apply it.
public static class AddInGate
{
    // Same opt-in variable FixtureBootstrap reads; a production Revit never has it.
    public const string ConfigVariable = "BIM_AGENTIC_TEST_CONFIG";

    // The add-in creates its ExternalEvent and opens the pipe only after a fixture bootstrap whose config was named by
    // the opt-in variable validated it and completed. Otherwise a Revit that loads the manifest exposes nothing.
    public static bool PipeAllowed(string? configVariable, bool bootstrapCompleted) => !string.IsNullOrWhiteSpace(configVariable) && bootstrapCompleted;

    // Of the pre-ADR0009 primitive operations only "inspect" reads; every other one (level.create, wall.create,
    // wall.axis.update, element.delete, batch, and anything unknown) is guarded as a write before it can run.
    public static bool IsLegacyRead(string? operation) => operation == "inspect";

    // Legacy requests carry no document UniqueId (their UniqueId names an element), so identity is the active
    // document's saved path; then the same copies-only and link guard as a revit.command write.
    public static void CheckLegacyWrite(string? requestPath, CommandDocumentFacts document, Func<RevitCommandPolicy> policy, RevitFileSystemProbe probe)
    {
        if (document.PathName is not { Length: > 0 } path || requestPath is null || !CommandAdmission.SamePath(path, requestPath))
            throw new RevitCommandException(RevitCommandCodes.DocumentConflict, "DocumentPath", "the active document is not the saved document the request names");
        CommandAdmission.CheckWrite(path, document, policy, probe);
    }
}

// Catalogue v1 element rules. Wall kinds are WallKind names ("Basic", "Curtain", "Stacked").
public static class CommandRules
{
    // Only a basic wall type has one meaningful layer width; curtain and stacked report null.
    public static bool ReportsWallTypeWidth(string wallKind) => wallKind == "Basic";

    // Stacked walls are composed of sub-walls whose geometry and readback v1 does not model.
    public static void CheckWallTypeForCreate(string wallKind)
    {
        if (wallKind == "Stacked") throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.type", "stacked wall types are not supported by wall.create in catalogue v1");
    }

    // Doors and windows are placed on the host's location curve, which is only the wall's middle when the location
    // line is Wall Centerline; other location lines are refused rather than projected. Curtain walls take curtain
    // panels, not hosted doors/windows.
    public static void CheckOpeningHost(string wallKind, bool locationLineIsWallCenterline)
    {
        if (wallKind == "Curtain") throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_wall_uid", "curtain walls cannot host a door or window in catalogue v1");
        if (!locationLineIsWallCenterline) throw new RevitCommandException(RevitCommandCodes.ReferenceNotFound, "args.host_wall_uid", "the host wall's location line is not Wall Centerline; set it to Wall Centerline or pick another wall");
    }

    // Foundation slab types are not offered as floor types (query or floor.create).
    public static bool OffersFloorType(bool isFoundationSlab) => !isFoundationSlab;
}
