using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Materials;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Core.Architecture;

/// <summary>The ways an eave is finished under its overhang.</summary>
public enum EaveKind
{
    /// <summary>Closed: the overhang finished with a soffit, fixed under the rafters and sloping with them.</summary>
    Closed,

    /// <summary>Open: the underside left unfinished - the rafters and the boarding over them on show, the rafters' ends under a fascia.</summary>
    Open,

    /// <summary>Exposed rafter: open underneath, and the rafters' ends left on show too, shaped, with no fascia over them.</summary>
    ExposedRafters,

    /// <summary>Box: enclosed by a level soffit back to the wall and a build-up of moulding - a frieze board, a bed moulding - so no rafter shows.</summary>
    Box,

    /// <summary>Abbreviated: an eave that stands out past the wall only slightly, or not at all - a frieze and moulding close under the roof's edge.</summary>
    Abbreviated
}

/// <summary>How the end of an exposed rafter is cut.</summary>
public enum RafterTail
{
    /// <summary>Straight down: the plain end a fascia is nailed to.</summary>
    Plumb,

    /// <summary>Square to the rafter.</summary>
    Square,

    /// <summary>Slant: cut back at an angle, the bottom further in than the top.</summary>
    Slant,

    /// <summary>Cove: a hollow curve scooped from the top of the end back to its bottom.</summary>
    Cove,

    /// <summary>Bullnose: rounded, a half circle.</summary>
    Bullnose,

    /// <summary>Scroll: an S-curve, as an ogee.</summary>
    Scroll
}

/// <summary>The moulding where a soffit meets the wall or its frieze.</summary>
public enum EaveMoulding
{
    None,

    /// <summary>Cove: a hollow quarter round in the corner - the plain bed moulding.</summary>
    Cove,

    /// <summary>Ogee: an S-curved crown moulding.</summary>
    Ogee
}

/// <summary>What stands under the eave against the wall at intervals.</summary>
public enum EaveBracket
{
    None,

    /// <summary>A bracket: a console, deep at its top under the eave, its front curving in to the wall.</summary>
    Bracket,

    /// <summary>A corbel: a block stepped out course by course from the wall.</summary>
    Corbel,

    /// <summary>Knee brace: a post on the wall, an arm under the eave and a strut between them, as on a craftsman bungalow.</summary>
    KneeBrace
}

/// <summary>
/// What the eave's underside is made of, as the IRC asks of one near a lot line (Table
/// R302.1(1)): plain, or one of the three it accepts 2 to 5 ft from the line.
/// </summary>
public enum EaveUnderside
{
    Unprotected,

    /// <summary>One-hour fire-resistance-rated construction on the underside.</summary>
    OneHour,

    HeavyTimber,

    FireRetardantTreated
}

/// <summary>
/// An eave's type: which of the five kinds it is - closed, open, exposed rafter, box or
/// abbreviated - and what it is built of: the rafters' size, spacing and tail cut; the boarding
/// over open rafters; the soffit board and the air through it; a frieze board and a moulding at
/// the wall; brackets, corbels or knee braces under it; and the fire protection of its underside.
/// </summary>
public sealed class EaveType : ElementType
{
    public EaveType(string name) : base(name)
    {
    }

    public override BuiltInCategory Category => BuiltInCategory.Eaves;

    public EaveKind Kind { get; set; } = EaveKind.Closed;

    /// <summary>How wide each rafter is, along the eave, mm.</summary>
    public double RafterWidth { get; set; } = 47;

    /// <summary>How deep the rafters are, square to the slope, mm; nothing for the depth of the roof's structure layer.</summary>
    public double RafterDepth { get; set; }

    /// <summary>How far apart the rafters are, centre to centre, mm.</summary>
    public double RafterSpacing { get; set; } = 400;

    /// <summary>How the rafters' ends are cut, where they are on show.</summary>
    public RafterTail Tail { get; set; } = RafterTail.Plumb;

    /// <summary>The boarding over open rafters, seen between them from below, mm; none at 0.</summary>
    public double DeckThickness { get; set; } = 18;

    public double SoffitThickness { get; set; } = 12;

    public SoffitBoard Board { get; set; } = SoffitBoard.Solid;

    /// <summary>
    /// How much open air the eave lets into the roof, mm² per metre of it: through the soffit
    /// board of a closed, box or abbreviated eave, through vents in the blocking between the
    /// rafters of an open one.
    /// </summary>
    public double FreeAirArea { get; set; }

    /// <summary>How deep the frieze board on the wall under the eave is, mm; none at 0.</summary>
    public double FriezeDepth { get; set; }

    public double FriezeThickness { get; set; } = 22;

    public EaveMoulding Moulding { get; set; } = EaveMoulding.None;

    /// <summary>How far the moulding reaches each way into its corner, mm.</summary>
    public double MouldingSize { get; set; } = 45;

    public EaveBracket Bracket { get; set; } = EaveBracket.None;

    /// <summary>How far apart the brackets are, mm; under an open eave each goes under the rafter nearest.</summary>
    public double BracketSpacing { get; set; } = 1200;

    /// <summary>How far down the wall a bracket reaches, mm.</summary>
    public double BracketHeight { get; set; } = 300;

    /// <summary>How far out from the wall a bracket reaches, mm; nothing for three quarters of the overhang.</summary>
    public double BracketDepth { get; set; }

    /// <summary>How thick a bracket is, along the eave, mm.</summary>
    public double BracketWidth { get; set; } = 75;

    public EaveUnderside Underside { get; set; } = EaveUnderside.Unprotected;

    /// <summary>What the rafters, boarding, frieze, moulding and brackets are made of.</summary>
    public Guid MaterialId { get; set; }

    public Guid SoffitMaterialId { get; set; }

    /// <summary>Whether a soffit closes it underneath: a closed, box or abbreviated eave.</summary>
    public bool HasSoffit => Kind is EaveKind.Closed or EaveKind.Box or EaveKind.Abbreviated;

    /// <summary>Whether it is open underneath, its rafters on show: an open or exposed rafter eave.</summary>
    public bool IsOpen => Kind is EaveKind.Open or EaveKind.ExposedRafters;

    /// <summary>Whether its soffit is level, from the roof's edge back to the wall: a box or abbreviated eave.</summary>
    public bool IsLevel => Kind is EaveKind.Box or EaveKind.Abbreviated;

    public EaveType Duplicate(string name) => new(name)
    {
        Kind = Kind, RafterWidth = RafterWidth, RafterDepth = RafterDepth, RafterSpacing = RafterSpacing, Tail = Tail,
        DeckThickness = DeckThickness, SoffitThickness = SoffitThickness, Board = Board, FreeAirArea = FreeAirArea,
        FriezeDepth = FriezeDepth, FriezeThickness = FriezeThickness, Moulding = Moulding, MouldingSize = MouldingSize,
        Bracket = Bracket, BracketSpacing = BracketSpacing, BracketHeight = BracketHeight, BracketDepth = BracketDepth,
        BracketWidth = BracketWidth, Underside = Underside, MaterialId = MaterialId, SoffitMaterialId = SoffitMaterialId,
        Description = Description, Cost = Cost
    };

    /// <summary>
    /// Changes the kind, bringing what the new kind is built with when the type has none of it:
    /// a box eave its frieze and bed moulding, an abbreviated one a smaller pair, an exposed
    /// rafter eave a shaped tail, an open one its boarding.
    /// </summary>
    public void ChangeKind(EaveKind kind)
    {
        Kind = kind;
        switch (kind)
        {
            case EaveKind.Box:
                if (FriezeDepth <= 0) FriezeDepth = 150;
                if (Moulding == EaveMoulding.None) Moulding = EaveMoulding.Cove;
                break;
            case EaveKind.Abbreviated:
                if (FriezeDepth <= 0) FriezeDepth = 100;
                if (Moulding == EaveMoulding.None) Moulding = EaveMoulding.Cove;
                break;
            case EaveKind.ExposedRafters:
                if (Tail == RafterTail.Plumb) Tail = RafterTail.Cove;
                if (DeckThickness <= 0) DeckThickness = 18;
                break;
            case EaveKind.Open:
                if (DeckThickness <= 0) DeckThickness = 18;
                break;
        }
    }

    public override IEnumerable<ParameterValue> GetTypeParameters(BimDocument? document = null)
    {
        foreach (var parameter in GetCommonTypeParameters()) yield return parameter;

        yield return ParameterValue.BindChoice(EaveParameters.Kind, () => EnumText.Humanise(Kind),
            v => { if (EnumText.TryParse<EaveKind>(v, out var kind) && kind != Kind) ChangeKind(kind); },
            EnumText.Choices<EaveKind>());
        if (RoofEdgeSweepParameters.MaterialOf(document, () => MaterialId, id => MaterialId = id) is { } material) yield return material;

        // The rafters: under a closed or box eave too, in its void, where a section shows them.
        yield return ParameterValue.BindValidated(EaveParameters.RafterWidth, () => RafterWidth, (double v) =>
        {
            if (v < 20 || v > 300) return false;
            RafterWidth = v;
            return true;
        });
        yield return ParameterValue.BindValidated(EaveParameters.RafterDepth, () => RafterDepth, (double v) =>
        {
            if (v != 0 && (v < 50 || v > 600)) return false;
            RafterDepth = v;
            return true;
        });
        yield return ParameterValue.BindValidated(EaveParameters.RafterSpacing, () => RafterSpacing, (double v) =>
        {
            if (v < RafterWidth + 50 || v > 1200) return false;
            RafterSpacing = v;
            return true;
        });
        if (IsOpen)
        {
            yield return ParameterValue.BindChoice(EaveParameters.Tail, () => EnumText.Humanise(Tail),
                v => { if (EnumText.TryParse<RafterTail>(v, out var tail)) Tail = tail; }, EnumText.Choices<RafterTail>());
            yield return ParameterValue.BindValidated(EaveParameters.DeckThickness, () => DeckThickness, (double v) =>
            {
                if (v < 0 || v > 50) return false;
                DeckThickness = v;
                return true;
            });
        }

        if (HasSoffit)
        {
            if (RoofEdgeSweepParameters.MaterialOf(document, () => SoffitMaterialId, id => SoffitMaterialId = id, EaveParameters.SoffitMaterial) is { } soffit)
                yield return soffit;
            yield return ParameterValue.BindValidated(EaveParameters.SoffitThickness, () => SoffitThickness, (double v) =>
            {
                if (v <= 0 || v > 50) return false;
                SoffitThickness = v;
                return true;
            });

            // Changing the kind of board brings its air with it, as a soffit's does.
            yield return ParameterValue.BindChoice(RoofEdgeSweepParameters.Board, () => EnumText.Humanise(Board),
                v =>
                {
                    if (!EnumText.TryParse<SoffitBoard>(v, out var board) || board == Board) return;
                    Board = board;
                    FreeAirArea = SoffitType.TypicalFreeAirArea(board);
                },
                EnumText.Choices<SoffitBoard>());
        }

        yield return ParameterValue.BindValidated(RoofEdgeSweepParameters.FreeAirArea, () => FreeAirArea, (double v) =>
        {
            if (v < 0 || (v > 0 && HasSoffit && Board is SoffitBoard.Solid or SoffitBoard.Hollow)) return false;
            FreeAirArea = v;
            return true;
        });

        yield return ParameterValue.BindValidated(EaveParameters.FriezeDepth, () => FriezeDepth, (double v) =>
        {
            if (v < 0 || v > 1000) return false;
            FriezeDepth = v;
            return true;
        });
        yield return ParameterValue.BindValidated(EaveParameters.FriezeThickness, () => FriezeThickness, (double v) =>
        {
            if (v < 5 || v > 100) return false;
            FriezeThickness = v;
            return true;
        });
        if (HasSoffit)
        {
            yield return ParameterValue.BindChoice(EaveParameters.Moulding, () => EnumText.Humanise(Moulding),
                v => { if (EnumText.TryParse<EaveMoulding>(v, out var moulding)) Moulding = moulding; }, EnumText.Choices<EaveMoulding>());
            yield return ParameterValue.BindValidated(EaveParameters.MouldingSize, () => MouldingSize, (double v) =>
            {
                if (v < 10 || v > 200) return false;
                MouldingSize = v;
                return true;
            });
        }

        yield return ParameterValue.BindChoice(EaveParameters.Bracket, () => EnumText.Humanise(Bracket),
            v => { if (EnumText.TryParse<EaveBracket>(v, out var bracket)) Bracket = bracket; }, EnumText.Choices<EaveBracket>());
        if (Bracket != EaveBracket.None)
        {
            yield return ParameterValue.BindValidated(EaveParameters.BracketSpacing, () => BracketSpacing, (double v) =>
            {
                if (v < 300 || v > 6000) return false;
                BracketSpacing = v;
                return true;
            });
            yield return ParameterValue.BindValidated(EaveParameters.BracketHeight, () => BracketHeight, (double v) =>
            {
                if (v < 50 || v > 1500) return false;
                BracketHeight = v;
                return true;
            });
            yield return ParameterValue.BindValidated(EaveParameters.BracketDepth, () => BracketDepth, (double v) =>
            {
                if (v != 0 && (v < 50 || v > 1500)) return false;
                BracketDepth = v;
                return true;
            });
            yield return ParameterValue.BindValidated(EaveParameters.BracketWidth, () => BracketWidth, (double v) =>
            {
                if (v < 20 || v > 300) return false;
                BracketWidth = v;
                return true;
            });
        }

        yield return ParameterValue.BindChoice(EaveParameters.Underside, () => EnumText.Humanise(Underside),
            v => { if (EnumText.TryParse<EaveUnderside>(v, out var underside)) Underside = underside; }, EnumText.Choices<EaveUnderside>());
    }
}

/// <summary>
/// An eave: what is built under a roof's overhang along its eaves, from the roof's edge back to
/// the wall - one of the five kinds its type says. The roof's build-up below its covering stops
/// at the wall's face, as it does on a building: out over the eave there are the rafters, the
/// boarding and the covering on them, and whatever closes them in - a soffit sloping under them,
/// a level one with a frieze and moulding, or nothing, the rafters on show. It follows the roof:
/// its edges are found by what they are, so it stays on them as the roof changes.
/// </summary>
public sealed class Eave : RoofEdgeSweep
{
    public override BuiltInCategory Category => BuiltInCategory.Eaves;

    /// <summary>
    /// Whether fire-stopping is carried into the eave where a party wall meets it - Approved
    /// Document B's compartment wall continued across the eaves (5.11b, Diagram 5.2 note 2).
    /// </summary>
    public bool FireStopped { get; set; } = true;

    /// <summary>
    /// Whether the eave is fireblocked at the wall, from its top plate to the underside of the
    /// roof's sheathing - what lets the IRC take its underside as needing no fire rating near a
    /// lot line (Table R302.1(1), note a). It closes the eave off from the roof: no air through it.
    /// </summary>
    public bool FireBlocked { get; set; }

    public override IEnumerable<ParameterValue> GetInstanceParameters(BimDocument document)
    {
        var type = document.FindType<EaveType>(TypeId);
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Roof,
            () => document.Find<Roof>(RoofId) is { } roof
                ? document.FindType<SlabType>(roof.TypeId)?.Name ?? "Roof"
                : "<none>");
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Edges, () => EdgeIds.Count);
        yield return ParameterValue.ReadOnly(EaveParameters.Overhang, () => Eaves.Strips(document, this).Select(strip => strip.Overhang).DefaultIfEmpty(0).Max());
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Length, () => Length(document));
        yield return ParameterValue.ReadOnly(EaveParameters.Rafters, () => Eaves.RafterCount(document, this));
        if (type is { Bracket: not EaveBracket.None })
            yield return ParameterValue.ReadOnly(EaveParameters.Brackets, () => Eaves.Brackets(document, this).Count);
        if (type is { HasSoffit: true }) yield return ParameterValue.Bind(EaveParameters.FireStopped, () => FireStopped, v => FireStopped = v);
        yield return ParameterValue.Bind(EaveParameters.FireBlocked, () => FireBlocked, v => FireBlocked = v);
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.InstanceFreeAirArea, () => Eaves.Air(document, this) * Length(document) / 1000);
        yield return ParameterValue.ReadOnly(RoofEdgeSweepParameters.Regulations, () => Eaves.Verdict(document, this));

        foreach (var parameter in GetCommonParameters(document)) yield return parameter;
    }
}

public static class EaveParameters
{
    public static readonly ParameterDefinition Kind = new("Eave", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition RafterWidth = new("Rafter Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RafterDepth = new("Rafter Depth (0 = the roof's structure)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition RafterSpacing = new("Rafter Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Tail = new("Rafter Tail", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition DeckThickness = new("Boarding over the Rafters (0 = none)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition SoffitMaterial = new("Soffit Material", ParameterDataType.Material, ParameterBinding.Type, ParameterGroup.MaterialsAndFinishes);
    public static readonly ParameterDefinition SoffitThickness = new("Soffit Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FriezeDepth = new("Frieze Depth (0 = none)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition FriezeThickness = new("Frieze Thickness", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Moulding = new("Moulding", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition MouldingSize = new("Moulding Size", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Bracket = new("Brackets", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);
    public static readonly ParameterDefinition BracketSpacing = new("Bracket Spacing", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BracketHeight = new("Bracket Height", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BracketDepth = new("Bracket Depth (0 = 3/4 of the overhang)", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition BracketWidth = new("Bracket Width", ParameterDataType.Length, ParameterBinding.Type, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Underside = new("Underside Fire Protection", ParameterDataType.Text, ParameterBinding.Type, ParameterGroup.Construction);

    public static readonly ParameterDefinition Overhang = new("Overhang", ParameterDataType.Length, ParameterBinding.Instance, ParameterGroup.Dimensions);
    public static readonly ParameterDefinition Rafters = new("Rafter Tails", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition Brackets = new("Bracket Count", ParameterDataType.Integer, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition FireStopped = new("Fire-Stopped at Party Walls", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
    public static readonly ParameterDefinition FireBlocked = new("Fireblocked at the Wall", ParameterDataType.YesNo, ParameterBinding.Instance, ParameterGroup.Construction);
}

/// <summary>
/// One stretch of an eave: under one edge of the roof, from its edge back to the wall's face -
/// mitred where it meets the next eave at a hip or valley, square where it meets a verge - with
/// the line of the wall's face under it and the roof's underside over it.
/// </summary>
public sealed record EaveStrip(
    int Edge, Point2D A, Point2D B, Vector2D Along, Vector2D Inward, double Overhang,
    IReadOnlyList<Point2D> Outline, Point2D WallFrom, Point2D WallTo, RoofPlane Plane,
    bool HipAtStart, bool HipAtEnd, bool SquareStart, bool SquareEnd)
{
    /// <summary>How long the roof's edge is here.</summary>
    public double Length => A.DistanceTo(B);

    /// <summary>How long the wall's face is under it.</summary>
    public double WallLength => (WallTo - WallFrom).Dot(Along);

    public double Stretch => Plane.VerticalStretch;

    /// <summary>The roof's underside over a point - the plane its build-up stands on.</summary>
    public double Under(Point2D at) => Plane.HeightAt(at);

    /// <summary>How far along the roof's edge a point is, from its start.</summary>
    public double AlongOf(Point2D point) => (point - A).Dot(Along);

    /// <summary>Whether there is any overhang here to build under.</summary>
    public bool HasArea => Overhang > 1 && Polygon2D.Area(Outline) > 100;
}

/// <summary>
/// Eaves: where they run, what each kind is built of, how they cut the roof back to its rafters,
/// and the rules they answer to - the air into a cold roof (BS 5250, IRC R806.2), fire-stopping
/// where a party wall meets them (Approved Document B 5.11b) and how near a lot line they may come
/// (IRC R302.1).
/// </summary>
public static class Eaves
{
    /// <summary>The eaves along a roof.</summary>
    public static IEnumerable<Eave> Of(BimDocument document, Roof roof) =>
        document.All<Eave>().Where(eave => eave.RoofId == roof.Id);

    /// <summary>The edges an eave goes along by itself: the roof's eaves - the edges it slopes up from.</summary>
    public static IReadOnlyList<Guid> EaveEdges(BimDocument document, Roof roof) =>
        RoofEdgeSweeps.GutterEdges(document, roof);

    /// <summary>
    /// What an eave takes over on the edges it is put on: a soffit there gives them up, since the
    /// eave closes its own overhang or leaves it open; and for an exposed rafter eave, a fascia,
    /// which would hide the rafters' ends. One left with no edges goes. As steps to make with it.
    /// </summary>
    public static IReadOnlyList<Documents.Commands.IUndoableCommand> TakeOver(BimDocument document, Guid roofId, EaveType? type, IEnumerable<Guid> edges)
    {
        var taken = edges.ToHashSet();
        var steps = new List<Documents.Commands.IUndoableCommand>();
        foreach (var sweep in document.All<RoofEdgeSweep>()
                     .Where(sweep => sweep.RoofId == roofId && (sweep is Soffit || sweep is Fascia && type is { Kind: EaveKind.ExposedRafters }))
                     .ToList())
        {
            if (!sweep.EdgeIds.Any(taken.Contains)) continue;
            var left = sweep.EdgeIds.Where(id => !taken.Contains(id)).ToList();
            steps.Add(left.Count == 0
                ? new Documents.Commands.DeleteElementsCommand(document, new Element[] { sweep })
                : new SetRoofEdgeSweepEdgesCommand(sweep, left, "Eave"));
        }

        return steps;
    }

    /// <summary>The strips of an eave, one under each of its edges the roof slopes up from.</summary>
    public static IReadOnlyList<EaveStrip> Strips(BimDocument document, Eave eave) =>
        document.Find<Roof>(eave.RoofId) is { } roof
            ? Strips(document, roof, eave.EdgeIds.ToHashSet())
            : Array.Empty<EaveStrip>();

    private static IReadOnlyList<EaveStrip> Strips(BimDocument document, Roof roof, IReadOnlySet<Guid> wanted)
    {
        if (roof.IsExtrusion || roof.Boundary.Count < 3 || roof.Edges.Count != roof.Boundary.Count) return Array.Empty<EaveStrip>();

        var boundary = roof.Boundary;
        var count = boundary.Count;
        var anticlockwise = Polygon2D.SignedArea(boundary) >= 0;
        var surface = roof.Surface(document);

        Vector2D Inward(int i) =>
            (boundary[(i + 1) % count] - boundary[i]).NormalisedOrDefault(Vector2D.UnitX).PerpendicularLeft() * (anticlockwise ? 1 : -1);
        double Over(int i) => Math.Max(0, roof.Edges[i].Overhang);
        (Point2D, Point2D) Inset(int i) => (boundary[i] + Inward(i) * Over(i), boundary[(i + 1) % count] + Inward(i) * Over(i));

        // At a hip or a valley - two eaves overhanging - each is mitred to the other on the line between.
        bool Overhangs(int i) => roof.Edges[i].DefinesSlope && Over(i) > 1;

        var strips = new List<EaveStrip>();
        for (var i = 0; i < count; i++)
        {
            if (!wanted.Contains(roof.Edges[i].Id) || !roof.Edges[i].DefinesSlope) continue;

            var (a, b) = (boundary[i], boundary[(i + 1) % count]);
            if (a.DistanceTo(b) < 1) continue;
            var along = (b - a).NormalisedOrDefault(Vector2D.UnitX);
            var inward = Inward(i);
            var overhang = Over(i);
            var (inA, inB) = Inset(i);
            var (previous, next) = ((i - 1 + count) % count, (i + 1) % count);

            Point2D? mitreStart = overhang > 1 && Overhangs(previous) ? Meet(Inset(previous), (inA, inB)) : null;
            Point2D? mitreEnd = overhang > 1 && Overhangs(next) ? Meet((inA, inB), Inset(next)) : null;
            var (outA, outB) = (mitreStart ?? inA, mitreEnd ?? inB);

            // The wall's face runs on to the next wall's face, whatever that edge does.
            var wallFrom = Meet(Inset(previous), (inA, inB)) ?? inA;
            var wallTo = Meet((inA, inB), Inset(next)) ?? inB;

            var face = surface.FaceAt(a.MidpointTo(b) + inward * 1);
            if (face is null || face.Plane.Rise < 1e-6) continue;

            var outline = new List<Point2D> { a, b, outB, outA };
            if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();

            strips.Add(new EaveStrip(i, a, b, along, inward, overhang, outline, wallFrom, wallTo, face.Plane,
                HipAtStart: mitreStart is { } s && (s - a).Dot(along) > 1,
                HipAtEnd: mitreEnd is { } e && (e - b).Dot(along) < -1,
                SquareStart: mitreStart is null,
                SquareEnd: mitreEnd is null));
        }

        return strips;
    }

    /// <summary>Where two lines, each through two points, cross; null if they run parallel.</summary>
    private static Point2D? Meet((Point2D A, Point2D B) first, (Point2D A, Point2D B) second)
    {
        var r = first.B - first.A;
        var s = second.B - second.A;
        var denominator = r.X * s.Y - r.Y * s.X;
        if (Math.Abs(denominator) < 1e-9) return null;

        var t = ((second.A.X - first.A.X) * s.Y - (second.A.Y - first.A.Y) * s.X) / denominator;
        return first.A + r * t;
    }

    /// <summary>
    /// How a roof's build-up stands over its eaves: how deep its covering is - every layer above its
    /// structure, which stays out over the eave - how deep the structure, and the whole.
    /// </summary>
    private readonly record struct BuildUp(double Covering, double Structure, double Total);

    private static BuildUp Layers(SlabType type)
    {
        var layers = type.Structure.Layers;
        var total = type.Structure.TotalWidth;
        var first = -1;
        for (var i = 0; i < layers.Count; i++)
            if (layers[i].Function == LayerFunction.Structure && layers[i].Thickness > 0)
            {
                first = i;
                break;
            }

        return first < 0 ? new BuildUp(total, 0, total) : new BuildUp(layers.Take(first).Sum(layer => layer.Thickness), layers[first].Thickness, total);
    }

    /// <summary>How deep an eave type's rafters are, square to the slope: its own depth, or the roof's structure's.</summary>
    private static double RafterDepth(EaveType type, BuildUp build) =>
        type.RafterDepth > 0 ? type.RafterDepth : build.Structure > 0 ? build.Structure : 150;

    /// <summary>
    /// The heights an eave is built to over a strip, straight down: the underside of the roof's
    /// covering, the boarding under it over open rafters, and the rafters under that.
    /// </summary>
    private sealed record Heights(EaveStrip Strip, double CoveringBottomAbove, double DeckDown, double RafterDepthDown)
    {
        /// <summary>The underside of the roof's covering over a point: what the boarding, or the rafters, are under.</summary>
        public double CoveringBottom(Point2D at) => Strip.Under(at) + CoveringBottomAbove;

        /// <summary>The top of the rafters over a point, under any boarding.</summary>
        public double RafterTop(Point2D at) => CoveringBottom(at) - DeckDown;

        public double RafterBottom(Point2D at) => RafterTop(at) - RafterDepthDown;
    }

    private static Heights HeightsOf(EaveStrip strip, EaveType type, BuildUp build) =>
        new(strip, (build.Total - build.Covering) * strip.Stretch, type.IsOpen ? type.DeckThickness * strip.Stretch : 0, RafterDepth(type, build) * strip.Stretch);

    /// <summary>Where along a strip each rafter is, from its start: set out from the wall's corner at the spacing, and a fly rafter at a verge.</summary>
    public static IReadOnlyList<double> RafterStations(EaveStrip strip, EaveType type)
    {
        var width = type.RafterWidth;
        var spacing = Math.Max(type.RafterSpacing, width + 1);
        var reach = strip.Outline.Select(strip.AlongOf).ToList();
        var (from, to) = (reach.Min() + width / 2, reach.Max() - width / 2);
        if (to < from) return Array.Empty<double>();

        var origin = strip.AlongOf(strip.WallFrom) + width / 2;
        var stations = new List<double>();
        for (var t = origin + Math.Ceiling((from - origin) / spacing - 1e-9) * spacing; t <= to + 1e-6; t += spacing) stations.Add(t);

        if (strip.SquareStart && (stations.Count == 0 || stations[0] - from > width)) stations.Insert(0, from);
        if (strip.SquareEnd && (stations.Count == 0 || to - stations[^1] > width)) stations.Add(to);
        return stations;
    }

    /// <summary>
    /// The rafter tails of a strip: each rafter's run from where it comes in under the roof's
    /// edge - or off a hip or valley - to the wall, as distances in from the edge, and whether its
    /// end is out at the edge, to be shaped.
    /// </summary>
    private static IEnumerable<(double At, double From, double To, bool AtEdge)> Tails(EaveStrip strip, EaveType type)
    {
        if (!strip.HasArea) yield break;
        foreach (var t in RafterStations(strip, type))
        {
            var start = strip.A + strip.Along * t;
            foreach (var (from, to) in Polygon2D.Clip(start - strip.Inward * 1, start + strip.Inward * (strip.Overhang + 1), strip.Outline, inside: true))
            {
                var (x0, x1) = ((from - start).Dot(strip.Inward), (to - start).Dot(strip.Inward));
                if (x1 - x0 < type.RafterWidth) continue;
                yield return (t, Math.Max(0, x0), x1, x0 < 0.5);
            }
        }
    }

    /// <summary>How many rafter tails an eave has, hip rafters with them.</summary>
    public static int RafterCount(BimDocument document, Eave eave)
    {
        if (document.FindType<EaveType>(eave.TypeId) is not { } type) return 0;
        var strips = Strips(document, eave);
        return strips.Sum(strip => Tails(strip, type).Count() + (strip.HipAtStart ? 1 : 0) + (strip.HipAtEnd && !Follows(strips, strip) ? 1 : 0));
    }

    /// <summary>Whether the eave goes on round the corner at a strip's end, so the hip rafter there is the next strip's.</summary>
    private static bool Follows(IReadOnlyList<EaveStrip> strips, EaveStrip strip) =>
        strips.Any(other => other.A == strip.B && !ReferenceEquals(other, strip));

    /// <summary>The section of a rafter's shaped end, from the top of it down to its bottom, in distance in from the edge and height.</summary>
    private static List<(double Along, double Z)> TailEnd(RafterTail shape, Func<double, double> top, Func<double, double> bottom, double depth, double rise)
    {
        const int steps = 8;
        var points = new List<(double, double)>();
        switch (shape)
        {
            case RafterTail.Square:
            {
                var s = depth / (1 + rise * rise);
                points.Add((0, top(0)));
                points.Add((rise * s, top(0) - s));
                break;
            }

            case RafterTail.Slant:
                points.Add((0, top(0)));
                points.Add((0.9 * depth, bottom(0.9 * depth)));
                break;

            case RafterTail.Cove:
            {
                // A hollow quarter round, its centre out below the top of the end.
                var reach = 0.7 * depth;
                var (centre, high) = (bottom(reach), top(0) - bottom(reach));
                for (var i = 0; i <= steps; i++)
                {
                    var angle = Math.PI / 2 * (1 - (double)i / steps);
                    points.Add((reach * Math.Cos(angle), centre + high * Math.Sin(angle)));
                }

                break;
            }

            case RafterTail.Bullnose:
            {
                var radius = depth / 2;
                var middle = top(radius) - radius;
                for (var i = 0; i <= steps; i++)
                {
                    var angle = Math.PI * i / steps;
                    points.Add((radius - radius * Math.Sin(angle), middle + radius * Math.Cos(angle)));
                }

                break;
            }

            case RafterTail.Scroll:
            {
                var reach = 0.8 * depth;
                var (high, low) = (top(0), bottom(reach));
                for (var i = 0; i <= steps; i++)
                {
                    var s = (double)i / steps;
                    points.Add((reach * (1 - Math.Cos(Math.PI * s)) / 2, high + s * (low - high)));
                }

                break;
            }

            default:
                points.Add((0, top(0)));
                points.Add((0, bottom(0)));
                break;
        }

        return points;
    }

    /// <summary>A rafter tail as a solid: its section, the end shaped or plain, carried across its width.</summary>
    private static void AddTail(Mesh3D mesh, EaveStrip strip, Heights heights, double at, double from, double to, bool shaped, EaveType type)
    {
        var start = strip.A + strip.Along * at;
        double Top(double x) => heights.RafterTop(start + strip.Inward * x);
        double Bottom(double x) => Top(x) - heights.RafterDepthDown;

        var section = shaped
            ? TailEnd(type.Tail, Top, Bottom, heights.RafterDepthDown, strip.Plane.Rise)
            : new List<(double Along, double Z)> { (from, Top(from)), (from, Bottom(from)) };
        var curve = section.Count > 2 ? Enumerable.Range(1, section.Count - 2).ToHashSet() : null;
        section.Add((to, Bottom(to)));
        section.Add((to, Top(to)));
        mesh.AddProfile(section, start - strip.Along * (type.RafterWidth / 2), strip.Inward, strip.Along, type.RafterWidth, curve);
    }

    /// <summary>A hip rafter's tail, along the mitre from the corner of the roof to the corner of the walls.</summary>
    private static void AddHipTail(Mesh3D mesh, Point2D corner, Point2D inner, Heights heights, double width)
    {
        var length = corner.DistanceTo(inner);
        if (length < width) return;

        var way = (inner - corner).NormalisedOrDefault(Vector2D.UnitX);
        double Top(double x) => heights.RafterTop(corner + way * x);
        var section = new List<(double Along, double Z)>
        {
            (0, Top(0)), (0, Top(0) - heights.RafterDepthDown), (length, Top(length) - heights.RafterDepthDown), (length, Top(length))
        };
        var across = way.PerpendicularLeft();
        mesh.AddProfile(section, corner - across * (width / 2), way, across, width);
    }

    /// <summary>The eave's strips taken in runs round the roof: each run's strips one after another, closed if it goes all round.</summary>
    private static IEnumerable<(List<EaveStrip> Strips, bool Closed)> Runs(IReadOnlyList<EaveStrip> strips, int edges)
    {
        if (strips.Count == 0) yield break;
        var on = strips.ToDictionary(strip => strip.Edge);
        if (strips.Count == edges)
        {
            yield return (strips.OrderBy(strip => strip.Edge).ToList(), true);
            yield break;
        }

        foreach (var strip in strips.OrderBy(strip => strip.Edge))
        {
            if (on.ContainsKey((strip.Edge - 1 + edges) % edges)) continue;
            var run = new List<EaveStrip>();
            for (var i = strip.Edge; on.TryGetValue(i % edges, out var next) && run.Count < edges; i++) run.Add(next);
            yield return (run, false);
        }
    }

    /// <summary>A section run along the wall's face under the eave - a frieze, a moulding - mitred round the corners.</summary>
    private static void AddAlongWall(Mesh3D mesh, IReadOnlyList<EaveStrip> run, bool closed, Func<EaveStrip, double> height, IReadOnlyList<Point2D> profile)
    {
        var up = new Vector3(0, 0, 1);
        var segments = run
            .Where(strip => strip.WallLength > 1)
            .Select(strip => new RoofEdgeSegment(
                new Vector3(strip.WallFrom.X, strip.WallFrom.Y, height(strip)), new Vector3(strip.WallTo.X, strip.WallTo.Y, height(strip)),
                new Vector3(-strip.Inward.X, -strip.Inward.Y, 0), up, profile))
            .ToList();
        if (segments.Count > 0) RoofEdgeSweeps.AddRun(mesh, new RoofEdgeRun(segments, closed && segments.Count == run.Count));
    }

    /// <summary>A moulding's section in the corner under the soffit, out from a face and down from the soffit, wound anticlockwise.</summary>
    private static IReadOnlyList<Point2D> MouldingSection(EaveMoulding moulding, double face, double size)
    {
        const int steps = 8;
        var points = new List<Point2D> { new(face, 0) };
        for (var i = 0; i <= steps; i++)
        {
            var s = (double)i / steps;
            points.Add(moulding == EaveMoulding.Ogee
                ? new Point2D(face + size * (1 + Math.Cos(Math.PI * s)) / 2, -size * s)
                : new Point2D(face + size - size * Math.Sin(Math.PI / 2 * s), -size + size * Math.Cos(Math.PI / 2 * s)));
        }

        if (Polygon2D.SignedArea(points) < 0) points.Reverse();
        return points;
    }

    /// <summary>
    /// Where the brackets of a strip go: along the wall's face at the spacing, the set centred on
    /// it - under an open eave each under the rafter nearest - as distances along the strip.
    /// </summary>
    private static IReadOnlyList<double> BracketStations(EaveStrip strip, EaveType type)
    {
        var width = type.BracketWidth;
        var length = strip.WallLength;
        if (type.Bracket == EaveBracket.None || length < width + 100) return Array.Empty<double>();

        var start = strip.AlongOf(strip.WallFrom);
        var count = (int)Math.Floor((length - width) / type.BracketSpacing) + 1;
        var first = (length - (count - 1) * type.BracketSpacing) / 2;
        var stations = Enumerable.Range(0, count).Select(k => start + first + k * type.BracketSpacing).ToList();
        if (!type.IsOpen) return stations;

        var rafters = RafterStations(strip, type).Where(t => t >= start + width / 2 && t <= start + length - width / 2).ToList();
        return rafters.Count == 0 ? stations : stations.Select(t => rafters.MinBy(r => Math.Abs(r - t))).Distinct().ToList();
    }

    /// <summary>
    /// Where an eave's brackets stand: the point on the wall's face each is fixed at, the strip
    /// it is under, and the underside it is under - its height at a distance out from the wall.
    /// </summary>
    public static IReadOnlyList<(EaveStrip Strip, Point2D At)> Brackets(BimDocument document, Eave eave)
    {
        if (document.FindType<EaveType>(eave.TypeId) is not { Bracket: not EaveBracket.None } type) return Array.Empty<(EaveStrip, Point2D)>();
        return Strips(document, eave)
            .Where(strip => strip.Overhang - Face(type) - 20 >= 40)
            .SelectMany(strip => BracketStations(strip, type).Select(t => (strip, strip.WallFrom + strip.Along * (t - strip.AlongOf(strip.WallFrom)))))
            .ToList();
    }

    /// <summary>How far out from the wall a bracket's back is: on the frieze, where there is one.</summary>
    private static double Face(EaveType type) => type.FriezeDepth > 0 ? type.FriezeThickness : 0;

    /// <summary>A bracket's section - console, corbel or knee brace - out from the wall and down from the underside over it, as parts wound anticlockwise.</summary>
    private static IEnumerable<IReadOnlyList<Point2D>> BracketSections(EaveBracket bracket, double back, double depth, double height, double member)
    {
        switch (bracket)
        {
            case EaveBracket.Corbel:
                yield return new[]
                {
                    new Point2D(back, -height), new Point2D(back + depth / 3, -height), new Point2D(back + depth / 3, -height * 2 / 3),
                    new Point2D(back + depth * 2 / 3, -height * 2 / 3), new Point2D(back + depth * 2 / 3, -height / 3),
                    new Point2D(back + depth, -height / 3), new Point2D(back + depth, 0), new Point2D(back, 0)
                };
                break;

            case EaveBracket.KneeBrace:
            {
                // A post on the wall, an arm under the eave out from it, and a strut from one to the
                // other, cut where it meets them.
                yield return new[] { new Point2D(back, -height), new Point2D(back + member, -height), new Point2D(back + member, 0), new Point2D(back, 0) };
                if (depth <= member * 2) break;
                yield return new[] { new Point2D(back + member, -member), new Point2D(back + depth, -member), new Point2D(back + depth, 0), new Point2D(back + member, 0) };
                var (from, to) = (new Point2D(back + member, -height + member * 1.5), new Point2D(back + depth - member * 1.5, -member));
                var way = (to - from).NormalisedOrDefault(Vector2D.UnitX);
                var (across, past) = (way.PerpendicularLeft() * (member / 2), way * (member * 2));
                var strut = new[] { from - across - past, to - across + past, to + across + past, from + across - past };
                var between = new[] { new Point2D(back + member, -height), new Point2D(back + depth, -height), new Point2D(back + depth, -member), new Point2D(back + member, -member) };
                foreach (var region in PolygonBoolean.Combine(strut, between, BooleanOperation.Intersection))
                    if (Polygon2D.Area(region.Outer) > 1) yield return Polygon2D.SignedArea(region.Outer) >= 0 ? region.Outer : region.Outer.Reverse().ToList();
                break;
            }

            default:
            {
                // A console: deep at its top, its front a hollow curve in to the wall at its foot.
                const int steps = 10;
                var points = new List<Point2D> { new(back, 0), new(back, -height), new(back + 0.15 * depth, -height) };
                for (var i = 1; i <= steps; i++)
                {
                    var angle = Math.PI + Math.PI / 2 * i / steps;
                    points.Add(new Point2D(back + depth + 0.85 * depth * Math.Cos(angle), -height - 0.88 * height * Math.Sin(angle)));
                }

                points.Add(new Point2D(back + depth, 0));
                yield return points;
                break;
            }
        }
    }

    /// <summary>
    /// The party walls an eave goes over: where each separating wall's line meets the wall's face
    /// under the eave, the wall coming to it, and how thick it is.
    /// </summary>
    public static IReadOnlyList<(EaveStrip Strip, Point2D At, double Width)> PartyWalls(BimDocument document, Eave eave)
    {
        var found = new List<(EaveStrip, Point2D, double)>();
        var strips = Strips(document, eave);
        if (strips.Count == 0) return found;

        foreach (var wall in document.Walls.Where(wall => !wall.IsCurved && wall.Length > 1))
        {
            if (document.GetWallType(wall) is not { Separating: true } type) continue;
            var way = (wall.End - wall.Start).NormalisedOrDefault(Vector2D.UnitX);
            foreach (var strip in strips)
            {
                if (Math.Abs(way.Dot(strip.Along)) > 0.9 || !strip.HasArea) continue;
                if (Meet((wall.Start, wall.End), (strip.WallFrom, strip.WallTo)) is not { } at) continue;
                var along = (at - strip.WallFrom).Dot(strip.Along);
                if (along < 0 || along > strip.WallLength) continue;

                // The party wall comes out to the outside wall under the eave: an end of it at, or in, it.
                if (Math.Min(at.DistanceTo(wall.Start), at.DistanceTo(wall.End)) > 600) continue;
                found.Add((strip, at, type.Structure.TotalWidth));
            }
        }

        return found;
    }

    /// <summary>The fire-stop filling the eave over a party wall: across the wall's thickness, from the wall's face out to the roof's edge.</summary>
    private static IReadOnlyList<Point2D> FireStopOutline(EaveStrip strip, Point2D at, double width)
    {
        var out_ = (at - strip.A).Dot(strip.Inward);
        var (left, right) = (at - strip.Along * (width / 2), at + strip.Along * (width / 2));
        var outline = new List<Point2D> { left, right, right - strip.Inward * out_, left - strip.Inward * out_ };
        if (Polygon2D.SignedArea(outline) < 0) outline.Reverse();
        return outline;
    }

    /// <summary>
    /// An eave as its solids, one for each material: the rafters, their boarding, the blocking,
    /// frieze, moulding and brackets in its timber; the soffit in its board; the fire-stopping
    /// over party walls. What the 3D view, an elevation and a section are drawn from.
    /// </summary>
    public static IReadOnlyList<Mesh3D> Meshes(BimDocument document, Eave eave, Guid levelId)
    {
        if (document.FindType<EaveType>(eave.TypeId) is not { } type) return Array.Empty<Mesh3D>();
        if (document.Find<Roof>(eave.RoofId) is not { } roof) return Array.Empty<Mesh3D>();
        if (document.FindType<SlabType>(roof.TypeId) is not { } roofType) return Array.Empty<Mesh3D>();

        var strips = Strips(document, eave);
        if (strips.Count == 0) return Array.Empty<Mesh3D>();
        var build = Layers(roofType);

        // Each part a solid of its own: parts that meet - a bracket against a moulding, a jack
        // rafter against a hip - would otherwise be one shape with holes where they overlap.
        var timberMaterial = document.FindMaterial(type.MaterialId);
        Mesh3D Timber() => new(eave.Id, levelId, MeshKind.Sweep, timberMaterial?.SurfaceColour ?? ColourRgb.FromHex("C9A672"), timberMaterial?.Name ?? "Eave");
        var timber = Timber();
        var boarding = Timber();
        var hips = Timber();
        var trim = Timber();
        var brackets = Timber();
        var soffitMaterial = document.FindMaterial(type.SoffitMaterialId);
        var soffit = new Mesh3D(eave.Id, levelId, MeshKind.Ceiling, soffitMaterial?.SurfaceColour ?? ColourRgb.FromHex("E8E6E0"), soffitMaterial?.Name ?? "Soffit");
        var stopMaterial = document.Materials.FirstOrDefault(material => material.Name == Views.ModelMeshBuilder.FireStopMaterial);
        var stops = new Mesh3D(eave.Id, levelId, MeshKind.Sweep, stopMaterial?.SurfaceColour ?? ColourRgb.FromHex("D8C98A"), stopMaterial?.Name ?? Views.ModelMeshBuilder.FireStopMaterial);

        var board = Math.Max(0.5, type.SoffitThickness);
        var frieze = type.FriezeDepth > 0 ? type.FriezeThickness : 0;

        // A box or abbreviated eave's soffit is level, at the underside of the roof's edge; an
        // abbreviated one standing out no further than its frieze has none.
        bool Soffited(EaveStrip strip) => type.HasSoffit && strip.HasArea && !(type.Kind == EaveKind.Abbreviated && strip.Overhang <= frieze + 5);
        double Level(EaveStrip strip) => strip.Under(strip.A) + eave.VerticalOffset;

        // The underside at the wall's face under each strip: what a frieze and moulding hang from.
        double UnderAtWall(EaveStrip strip) => !Soffited(strip)
            ? strip.Under(strip.WallFrom)
            : type.IsLevel ? Level(strip) - board : strip.Under(strip.WallFrom) - board * strip.Stretch;

        foreach (var strip in strips)
        {
            var heights = HeightsOf(strip, type, build);

            // The rafters, out from the wall - in the void of a closed eave too - and the hip rafters at the corners.
            foreach (var (at, from, to, atEdge) in Tails(strip, type))
                AddTail(timber, strip, heights, at, from, to, atEdge && type.IsOpen, type);
            if (strip.HipAtStart) AddHipTail(hips, strip.A, strip.WallFrom, heights, type.RafterWidth);
            if (strip.HipAtEnd && !Follows(strips, strip)) AddHipTail(hips, strip.B, strip.WallTo, heights, type.RafterWidth);

            if (type.IsOpen && strip.HasArea)
            {
                // The boarding on the rafters, seen between them; and the blocking between them at
                // the wall, down to the roof's underside there.
                if (heights.DeckDown > 0) boarding.AddExtrusion(strip.Outline, heights.RafterTop, heights.CoveringBottom);
                foreach (var block in Blocking(strip, type))
                    timber.AddExtrusion(block, strip.Under, heights.RafterTop);
            }

            if (Soffited(strip))
            {
                if (type.IsLevel)
                {
                    var level = Level(strip);
                    soffit.AddExtrusion(strip.Outline, level - board, level);
                    RoofEdgeSweeps.Face(soffit, new SoffitPiece(strip.Outline, level, level - board, strip.Length, strip.A, strip.Along, strip.Inward), type.Board);
                }
                else
                {
                    soffit.AddExtrusion(strip.Outline, p => strip.Under(p) - board * strip.Stretch, strip.Under);
                    var top = strip.Under(strip.A);
                    RoofEdgeSweeps.Face(soffit, new SoffitPiece(strip.Outline, top, top - board * strip.Stretch, strip.Length, strip.A, strip.Along, strip.Inward)
                        { TopAt = strip.Under }, type.Board);
                }
            }

            // Brackets against the wall - or its frieze - under the soffit or the rafter over each.
            if (type.Bracket != EaveBracket.None && strip.Overhang - Face(type) - 20 >= 40)
            {
                var available = strip.Overhang - Face(type) - 20;
                var depth = type.BracketDepth > 0 ? Math.Min(type.BracketDepth, available) : 0.75 * available;
                foreach (var t in BracketStations(strip, type))
                {
                    var wall = strip.WallFrom + strip.Along * (t - strip.AlongOf(strip.WallFrom));
                    double Underside(double x)
                    {
                        var over = wall - strip.Inward * x;
                        return type.IsOpen ? heights.RafterBottom(over)
                            : Soffited(strip) ? type.IsLevel ? Level(strip) - board : strip.Under(over) - board * strip.Stretch
                            : strip.Under(over);
                    }

                    foreach (var part in BracketSections(type.Bracket, Face(type), depth, type.BracketHeight, Math.Min(type.BracketWidth, type.BracketHeight / 3)))
                    {
                        var section = part.Select(point => (point.X, Underside(point.X) + point.Y)).ToList();
                        brackets.AddProfile(section, wall - strip.Along * (type.BracketWidth / 2), -strip.Inward, strip.Along, type.BracketWidth,
                            part.Count > 8 ? Enumerable.Range(3, part.Count - 5).ToHashSet() : null);
                    }
                }
            }
        }

        // The frieze and the moulding, run along the walls' faces round the corners.
        foreach (var (run, closed) in Runs(strips, roof.Boundary.Count))
        {
            if (frieze > 0)
                AddAlongWall(timber, run, closed, UnderAtWall,
                    new[] { new Point2D(0, -type.FriezeDepth), new Point2D(frieze, -type.FriezeDepth), new Point2D(frieze, 0), new Point2D(0, 0) });
            if (type.HasSoffit && type.Moulding != EaveMoulding.None && run.Any(Soffited))
                AddAlongWall(trim, run.Where(Soffited).ToList(), closed && run.All(Soffited), UnderAtWall, MouldingSection(type.Moulding, frieze, type.MouldingSize));
        }

        // Fire-stopping filling the void over each party wall, from the soffit up to the covering.
        if (type.HasSoffit && eave.FireStopped)
            foreach (var (strip, at, width) in PartyWalls(document, eave).Where(found => Soffited(found.Strip)))
            {
                var heights = HeightsOf(strip, type, build);
                Func<Point2D, double> bottom = type.IsLevel ? _ => Level(strip) : strip.Under;
                stops.AddExtrusion(FireStopOutline(strip, at, width), bottom, heights.CoveringBottom);
            }

        return new[] { timber, boarding, hips, trim, brackets, soffit, stops }.Where(mesh => !mesh.IsEmpty).ToList();
    }

    /// <summary>The blocks between an open eave's rafters at the wall's face, each from one rafter to the next, as outlines in plan.</summary>
    private static IEnumerable<IReadOnlyList<Point2D>> Blocking(EaveStrip strip, EaveType type)
    {
        var (start, end) = (strip.AlongOf(strip.WallFrom), strip.AlongOf(strip.WallTo));
        var half = type.RafterWidth / 2;
        var edges = new List<double> { start };
        foreach (var t in RafterStations(strip, type).Where(t => t + half > start && t - half < end))
        {
            edges.Add(t - half);
            edges.Add(t + half);
        }

        edges.Add(end);
        for (var i = 0; i + 1 < edges.Count; i += 2)
        {
            var (from, to) = (Math.Max(edges[i], start), Math.Min(edges[i + 1], end));
            if (to - from < 5) continue;
            Point2D At(double along) => strip.WallFrom + strip.Along * (along - start);
            var block = new List<Point2D> { At(from), At(to), At(to) - strip.Inward * type.RafterWidth, At(from) - strip.Inward * type.RafterWidth };
            if (Polygon2D.SignedArea(block) < 0) block.Reverse();
            yield return block;
        }
    }

    /// <summary>An eave as one solid, for what takes an element as one: its parts together, in its timber's colour.</summary>
    public static Mesh3D Mesh(BimDocument document, Eave eave, Guid levelId)
    {
        var parts = Meshes(document, eave, levelId);
        var material = document.FindMaterial(document.FindType<EaveType>(eave.TypeId)?.MaterialId ?? Guid.Empty);
        var mesh = new Mesh3D(eave.Id, levelId, MeshKind.Sweep, material?.SurfaceColour ?? ColourRgb.FromHex("C9A672"), material?.Name ?? "Eave");
        foreach (var part in parts) mesh.Append(part);
        return mesh;
    }

    /// <summary>An eave in plan: the overhang it is under, and where it has none, the frieze along the wall.</summary>
    public static IEnumerable<IReadOnlyList<Point2D>> Footprints(BimDocument document, Eave eave)
    {
        var frieze = Math.Max(20, document.FindType<EaveType>(eave.TypeId)?.FriezeThickness ?? 20);
        foreach (var strip in Strips(document, eave))
        {
            if (strip.HasArea)
            {
                yield return strip.Outline;
                continue;
            }

            var band = new List<Point2D> { strip.WallFrom, strip.WallTo, strip.WallTo - strip.Inward * frieze, strip.WallFrom - strip.Inward * frieze };
            if (Polygon2D.SignedArea(band) < 0) band.Reverse();
            if (Polygon2D.Area(band) > 1) yield return band;
        }
    }

    /// <summary>
    /// The roof's pieces cut back where its eaves are: the structure and everything under it - the
    /// rafters' layer, insulation, the ceiling's board - stops at the wall's face, and only the
    /// covering over the rafters goes on out over the eave. The eave puts the rafters back as they
    /// are out there: one by one, with what closes them in or leaves them on show.
    /// </summary>
    public static IReadOnlyList<RoofPiece> CutBack(BimDocument document, Roof roof, IReadOnlyList<RoofPiece> pieces)
    {
        if (!document.All<Eave>().Any(eave => eave.RoofId == roof.Id)) return pieces;
        if (document.FindType<SlabType>(roof.TypeId) is not { } type) return pieces;

        var layers = type.Structure.Layers;
        var first = -1;
        for (var i = 0; i < layers.Count; i++)
            if (layers[i].Function == LayerFunction.Structure && layers[i].Thickness > 0)
            {
                first = i;
                break;
            }

        if (first < 0) return pieces;
        var cut = new HashSet<MaterialLayer>(layers.Skip(first), ReferenceEqualityComparer.Instance);
        var wanted = Of(document, roof).SelectMany(eave => eave.EdgeIds).ToHashSet();
        var strips = Strips(document, roof, wanted).Where(strip => strip.HasArea).Select(strip => strip.Outline).ToList();
        if (strips.Count == 0) return pieces;

        var result = new List<RoofPiece>();
        foreach (var piece in pieces)
        {
            if (!cut.Contains(piece.Layer))
            {
                result.Add(piece);
                continue;
            }

            IReadOnlyList<IReadOnlyList<Point2D>> left = new[] { piece.Outline };
            foreach (var strip in strips)
                left = left.SelectMany(part => PolygonBoolean.Combine(part, strip, BooleanOperation.Difference).Select(region => region.Outer)).ToList();
            result.AddRange(left.Where(part => Polygon2D.Area(part) > 1).Select(part => piece with { Outline = part }));
        }

        return result;
    }

    /// <summary>
    /// How deep a roof's edge is at an open eave, in its own build-up's terms - its covering and
    /// the rafters' ends under it, where a fascia over them stops; null where no open eave is.
    /// </summary>
    public static double? OpenDepth(BimDocument document, Roof roof, Guid edgeId)
    {
        if (Of(document, roof).FirstOrDefault(eave => eave.EdgeIds.Contains(edgeId)) is not { } eave) return null;
        if (document.FindType<EaveType>(eave.TypeId) is not { IsOpen: true } type || document.FindType<SlabType>(roof.TypeId) is not { } roofType) return null;
        var build = Layers(roofType);
        return build.Covering + RafterDepth(type, build);
    }

    /// <summary>The air an eave lets into the roof, mm² a metre: none once it is fireblocked at the wall.</summary>
    public static double Air(BimDocument document, Eave eave) =>
        eave.FireBlocked ? 0 : document.FindType<EaveType>(eave.TypeId)?.FreeAirArea ?? 0;

    /// <summary>How near the eave's edge comes to the property line, mm - nothing where it reaches over it; null with no property line.</summary>
    public static double? DistanceToBoundary(BimDocument document, Eave eave)
    {
        if (document.All<PropertyLine>().FirstOrDefault(line => line.Boundary.Count >= 3) is not { } property) return null;
        var strips = Strips(document, eave);
        if (strips.Count == 0) return null;

        var boundary = property.Boundary;
        var nearest = double.MaxValue;
        foreach (var strip in strips)
            for (var s = 0; s <= 8; s++)
            {
                var point = strip.A + strip.Along * (strip.Length * s / 8);
                if (!Polygon2D.Contains(boundary, point)) return 0;
                for (var i = 0; i < boundary.Count; i++)
                    nearest = Math.Min(nearest, Line2D.DistanceFromSegment(point, boundary[i], boundary[(i + 1) % boundary.Count]));
            }

        return nearest;
    }

    /// <summary>
    /// The IRC's lines for a projection near a lot line: none closer than 2 ft; fire-protected
    /// underneath closer than 5 ft (Table R302.1(1)) - 3 ft in a house sprinklered throughout (Table R302.1(2)).
    /// </summary>
    public const double NoProjection = 610, ProtectedUnder = 1524, ProtectedUnderSprinklered = 914;

    private static string Feet(double millimetres) => $"{millimetres / 1000:0.00} m ({millimetres / 304.8:0.0} ft)";

    /// <summary>What an eave falls short of, as the project's standard asks.</summary>
    public static IReadOnlyList<PlacementProblem> Problems(BimDocument document, Eave eave, ClearanceStandard standard)
    {
        if (document.Find<Roof>(eave.RoofId) is not { } roof) return Array.Empty<PlacementProblem>();
        if (document.FindType<EaveType>(eave.TypeId) is not { } type) return Array.Empty<PlacementProblem>();

        var outlines = Footprints(document, eave).ToList();
        if (outlines.Count == 0) return Array.Empty<PlacementProblem>();
        var problems = new List<PlacementProblem>();
        void Add(string said, string rule) => problems.AddRange(outlines.Select(outline => new PlacementProblem(eave, said, rule, outline)));

        var air = Air(document, eave);
        if (RoofEdgeRules.EavesAirAsked(document, roof, standard) is var (asked, rule) && air < asked - 0.5)
            Add(eave.FireBlocked
                    ? "it is fireblocked at the wall, so it lets no air into the cold roof over it, its insulation on the ceiling."
                    : air > 0
                        ? $"it lets {air:#,0} mm² of air a metre into the cold roof over it, its insulation on the ceiling."
                        : "it lets no air into the cold roof over it, its insulation on the ceiling.",
                $"{rule} - {(type.IsOpen ? "vents in the blocking between the rafters" : "a vented soffit")}, or vents over the fascia.");

        if (!standard.IsUs())
        {
            if (type.HasSoffit && !eave.FireStopped && PartyWalls(document, eave).Count is > 0 and var walls)
                Add($"{(walls == 1 ? "a party wall meets it" : $"{walls} party walls meet it")}, and the eave over {(walls == 1 ? "it is" : "them is")} not fire-stopped.",
                    "Approved Document B (5.11b, Diagram 5.2 note 2) asks for a compartment wall to be continued across any eaves, with the fire-stopping carried into them - tick Fire-Stopped at Party Walls.");
            return problems;
        }

        if (DistanceToBoundary(document, eave) is { } distance)
        {
            var sprinklered = document.ProjectInformation.Sprinklered;
            var table = sprinklered ? "Table R302.1(2)" : "Table R302.1(1)";
            if (distance < NoProjection)
                Add(distance <= 0 ? "its edge reaches over the property line." : $"its edge is {Feet(distance)} from the property line.",
                    $"The IRC (R302.1, {table}) allows no projection less than 2 ft (610 mm) from the lot line - shorten the overhang there.");
            else if (distance < (sprinklered ? ProtectedUnderSprinklered : ProtectedUnder) && type.Underside == EaveUnderside.Unprotected && !eave.FireBlocked)
                Add($"its edge is {Feet(distance)} from the property line, its underside unprotected.",
                    $"The IRC ({table}) asks a projection 2 to {(sprinklered ? "3 ft from the lot line of a sprinklered house" : "5 ft from the lot line")} for one-hour fire-resistive construction on its underside, heavy timber or fire-retardant-treated wood - or fireblocking from the wall's top plate to the roof sheathing (note {(sprinklered ? "b" : "a")}).");
        }

        return problems;
    }

    /// <summary>What an eave comes to as its rules see it.</summary>
    public static string Verdict(BimDocument document, Eave eave)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var problems = Problems(document, eave, standard);
        if (problems.Count > 0)
        {
            var said = string.Join(" ", problems.Select(problem => problem.Said).Distinct().Select(text => $"{char.ToUpperInvariant(text[0])}{text[1..]}"));
            return $"{said} {string.Join(" ", problems.Select(problem => problem.Rule).Distinct())}";
        }

        if (document.Find<Roof>(eave.RoofId) is not { } roof) return "Nothing asked";
        var parts = new List<string>();
        parts.Add(RoofEdgeRules.EavesAirAsked(document, roof, standard) is var (asked, _)
            ? $"Airs the cold roof over it: {asked:#,0} mm² a metre asked"
            : "The roof over it is not a cold roof: no air asked");
        if (!standard.IsUs() && PartyWalls(document, eave).Count is > 0 and var walls && eave.FireStopped)
            parts.Add($"fire-stopped over {walls} party wall{(walls == 1 ? "" : "s")}");
        if (standard.IsUs() && DistanceToBoundary(document, eave) is { } distance)
            parts.Add($"its edge {Feet(distance)} from the property line");
        return string.Join("; ", parts);
    }
}
