using Bim.Core;
namespace Bim.Generative;

// Facade windows into the prototype BIM-IR (P7-06). FacadeIrBuilder.Prototype compiles a feasible space plan (for
// example a ranked prototype alternative's plan) into the existing BIM-IR records through IntentCompiler (levels, walls
// on the room axis lines, spaces; no openings); FacadeIrBuilder.AddWindows then adds the windows of a complete P7-05
// FacadeLayout as "window" elements hosted on the exterior walls they lie on, keeping them clear of the P7-04 entrance
// door. No BIM-IR schema change: a window element is the compiled opening form IntentCompiler already emits,
// GeometryMetres [ax,ay,bx,by,sill,height] (its stretch of the host wall axis in the host's direction, then the sill and
// the height head - sill), Dependencies [level, host wall]. The stretch is OffsetMetres..OffsetMetres + WidthMetres from
// the host's start point. Nothing here is persisted unless the caller commits the project.
public enum FacadeIrStatus { Complete, Failed, Refused }

// One window element added to the project. Id is the P7-05 window id ("window:<instance>/<n>"), ElementId the BIM-IR id
// IntentCompiler.Id(project, IntentCompiler.OpeningKey(Id)) (window ids hold ':' and '/', which intent keys never do, so
// they cannot collide with compiled intent openings). OffsetMetres is measured along the host axis from its start point
// (ax, ay); ShiftMetres is the signed move along the plan axis (x for south/north, y for east/west) applied to clear the
// entrance door (0 when the window stays where P7-05 put it).
public sealed record FacadeIrWindow(string Id, string Instance, int Level, Guid ElementId, Guid HostWallId, double OffsetMetres, double WidthMetres,
    double SillHeightMetres, double HeadHeightMetres, double ShiftMetres);

// Status is Complete when every window was hosted (Project holds the prototype's elements unchanged followed by the
// window elements in layout order), Failed when some window could not be hosted or kept clear of the entrance door
// (Project is null; the failed windows' checks say why), Refused when the inputs were refused (Checks hold only the
// refusals). Checks per window: facade_ir_window (hard, satisfied) for a hosted window, plus facade_ir_window_moved
// (soft, satisfied) when it was moved off the entrance door, or the failure code (hard, not satisfied).
public sealed record FacadeIrResult(FacadeIrStatus Status, Project? Project, IReadOnlyList<FacadeIrWindow> Windows, IReadOnlyList<PlanConstraint> Checks)
{
    public bool IsComplete => Status == FacadeIrStatus.Complete;
    public IReadOnlyList<PlanConstraint> Violations => Checks.Where(check => !check.Satisfied).ToArray();
}

// Prototype walls: one wall type of WallThicknessMetres x WallHeightMetres for every room side; level n sits at
// elevation n x WallHeightMetres. EntranceClearanceMetres is the least distance kept between a window and either jamb of
// the entrance door on the same wall line (the P7-05 end clearance default). Provenance stamps every compiled and added
// element (DefaultProvenance when null).
public sealed record FacadeIrOptions(double WallThicknessMetres = FacadeIrOptions.DefaultWallThicknessMetres, double WallHeightMetres = FacadeIrOptions.DefaultWallHeightMetres,
    double EntranceClearanceMetres = FacadeIrOptions.DefaultEntranceClearanceMetres, Provenance? Provenance = null)
{
    public const double DefaultWallThicknessMetres = 0.15;
    public const double DefaultWallHeightMetres = 2.6;
    public const double DefaultEntranceClearanceMetres = 0.3;
    public static Provenance DefaultProvenance { get; } = new("bim.generative.facade-ir", "proposed");
}

public static class FacadeIrCodes
{
    // Refusals.
    public const string ProjectInvalid = "facade_ir_project_invalid";
    public const string LayoutInvalid = "facade_ir_layout_invalid";
    public const string OptionsInvalid = "facade_ir_options_invalid";
    public const string ElementExists = "facade_ir_element_exists";
    // Satisfied checks.
    public const string Window = "facade_ir_window";
    public const string Moved = "facade_ir_window_moved";
    // Failures of a window.
    // No wall of the window's level holds the window's stretch on its axis line (the project is not this plan's prototype).
    public const string HostMissing = "facade_ir_host_missing";
    // More than one wall of the level holds the stretch.
    public const string HostAmbiguous = "facade_ir_host_ambiguous";
    // The stretch leaves the host's clear span (axis span less half the thickness of the perpendicular walls joined at
    // each end, as IntentCompiler), or the head is above the host wall height.
    public const string OutsideWall = "facade_ir_outside_wall";
    // The window overlaps the entrance door (with the clearance) and no move along its host keeps it inside the host's
    // usable span (clear span and P7-05 end clearance) and off the other windows.
    public const string EntranceOverlap = "facade_ir_entrance_overlap";
    // The window overlaps another window of the same host.
    public const string WindowOverlap = "facade_ir_window_overlap";
}
