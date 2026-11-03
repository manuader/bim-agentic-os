using System.Globalization;
using Bim.Core;
namespace Bim.Generative;

// Typology templates: a fixed room programme per typology, written as a project brief v1 (Bim.Core.ProjectBrief) whose
// site is the requested lot. Every room has count 1, so a room's instance id is its room id. Shared rooms keep the same
// area ranges across typologies (only living-dining targets grow), so a room approved in one typology stays valid in
// another. Room names are English and match the CABA pack's name patterns (living*, kitchen*, bedroom*, bath*).
// Programme figures are illustrative defaults for a prototype, not a regulation; CabaRules carries the code minimums.
public static class PrototypeTemplates
{
    const string Pack = "ar.caba.codigo-edificacion";

    // CABA minimum area and shortest side of the dwelling rooms the templates use, by room-name pattern, from the
    // ar.caba.codigo-edificacion skill pack (rule ids in Reference). The planner and the prototype checks apply them.
    public static PlannerRules CabaRules { get; } = new(new[]
    {
        new PlannerRule(NamePattern: "living*", MinAreaSquareMetres: 9, MinSideMetres: 2.8, Reference: Pack + " living-area-min living-side-min"),
        new PlannerRule(NamePattern: "kitchen*", MinAreaSquareMetres: 3, MinSideMetres: 1.5, Reference: Pack + " kitchen-area-min kitchen-side-min"),
        new PlannerRule(NamePattern: "bedroom*", MinAreaSquareMetres: 7.5, MinSideMetres: 2.5, Reference: Pack + " bedroom-area-min bedroom-side-min"),
        new PlannerRule(NamePattern: "bath*", MinAreaSquareMetres: 1.4, MinSideMetres: 0.9, Reference: Pack + " bathroom-area-min bathroom-side-min"),
    });

    // CABA corridor minimum side (pack rule corredor-side-min, parameter corredor_lado_min_m): 1.00 m, 0.95 m on parcels
    // narrower than 8.66 m. The template hall is the dwelling's circulation, so it gets this minimum.
    public const double CorridorMinSideMetres = 1.0;
    public const double NarrowParcelCorridorMinSideMetres = 0.95;
    public const double NarrowParcelWidthMetres = 8.66;
    // Template minimum side of the laundry, so a washing machine and a person fit; a programme choice, not a CABA rule.
    public const double LaundryMinSideMetres = 1.2;

    // Every planner rule of a prototype on the lot: CabaRules, the CABA corridor minimum side for the hall (by room id,
    // narrow-parcel value when the lot is narrower than 8.66 m) and the template laundry minimum side.
    public static PlannerRules Rules(PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        var narrow = lot.WidthMetres < NarrowParcelWidthMetres;
        var rules = new List<PlannerRule>(CabaRules.Rules)
        {
            new(RoomId: "hall", MinSideMetres: narrow ? NarrowParcelCorridorMinSideMetres : CorridorMinSideMetres,
                Reference: Pack + " corredor-side-min (corredor_lado_min_m " + (narrow ? "0.95 m, parcel narrower than 8.66 m" : "1.00 m") + ")"),
            new(RoomId: "laundry", MinSideMetres: LaundryMinSideMetres, Reference: "prototype template laundry minimum side (not a CABA rule)"),
        };
        return new PlannerRules(rules);
    }

    public static string Code(ApartmentTypology typology) => typology switch
    {
        ApartmentTypology.OneBedroom => "1br",
        ApartmentTypology.TwoBedroom => "2br",
        ApartmentTypology.ThreeBedroom => "3br",
        _ => "unknown",
    };

    // The template brief of a defined typology on the lot. The lot is not validated here (PrototypeGenerator refuses
    // out-of-range lots); an undefined typology throws ArgumentOutOfRangeException.
    public static ProjectBrief Brief(ApartmentTypology typology, PrototypeLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);
        if (!Enum.IsDefined(typology)) throw new ArgumentOutOfRangeException(nameof(typology), "undefined apartment typology");
        var bedrooms = (int)typology;
        var template = new BriefProvenance(BriefSource.Inferred, 0.7, "prototype template " + Code(typology) + " v1");
        var rooms = new List<BriefRoom>
        {
            Room("living_dining", "Living-dining", bedrooms switch { 1 => 20, 2 => 24, _ => 28 }, 18, null, template),
            Room("kitchen", "Kitchen", 7, 5, 10, template),
            Room("bedroom_1", "Bedroom 1", 12, 10, 14, template),
        };
        if (bedrooms >= 2) rooms.Add(Room("bedroom_2", "Bedroom 2", 9.5, 7.5, 12, template));
        if (bedrooms >= 3) rooms.Add(Room("bedroom_3", "Bedroom 3", 9, 7.5, 12, template));
        rooms.Add(Room("bathroom", "Bathroom", 4.5, 3.5, null, template));
        if (bedrooms >= 3) rooms.Add(Room("bathroom_2", "Bathroom 2", 3.5, 2.5, null, template));
        if (bedrooms >= 2) rooms.Add(Room("laundry", "Laundry", 3, 2, null, template));
        rooms.Add(Room("hall", "Hall", bedrooms >= 3 ? 5 : 4, 3, null, template));
        var adjacencies = new List<BriefAdjacency>
        {
            new("kitchen", "living_dining", BriefAdjacencyKind.Required, template),
            new("hall", "living_dining", BriefAdjacencyKind.Required, template),
            new("bedroom_1", "bathroom", BriefAdjacencyKind.Preferred, template),
            new("kitchen", "bedroom_1", BriefAdjacencyKind.Forbidden, template),
        };
        if (bedrooms >= 2) adjacencies.Add(new("kitchen", "laundry", BriefAdjacencyKind.Preferred, template));
        if (bedrooms >= 3) adjacencies.Add(new("bedroom_2", "bathroom_2", BriefAdjacencyKind.Preferred, template));
        var budget = bedrooms switch { 1 => 55.0, 2 => 75.0, _ => 95.0 };
        var lotSource = new BriefProvenance(BriefSource.User, 1, "prototype lot");
        var name = "Prototype " + Text(bedrooms) + "-bedroom apartment, lot " + SpacePlanSerializer.Format(lot.WidthMetres) + " x " + SpacePlanSerializer.Format(lot.DepthMetres) + " m";
        return new ProjectBrief(ProjectBrief.CurrentVersion, name,
            new BriefConstraints(new BriefValue<string>("CABA", template), new BriefSite(new BriefValue<double>(lot.WidthMetres, lotSource), new BriefValue<double>(lot.DepthMetres, lotSource)),
                new BriefValue<int>(1), new BriefValue<double>(budget, template)),
            rooms, adjacencies);
    }

    static BriefRoom Room(string id, string name, double target, double min, double? max, BriefProvenance provenance) =>
        new(id, name, null, new BriefValue<double>(target), new BriefValue<double>(min), max is { } high ? new BriefValue<double>(high) : null, null, provenance);

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
