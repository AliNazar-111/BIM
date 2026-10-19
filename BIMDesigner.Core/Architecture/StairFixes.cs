using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A stair made to meet its rules, as far as its own settings can: its risers and going worked
/// out afresh for the storey's height within the limits for what it is for; its handrails put
/// on, high enough, the right size and clear of the wall; its guarding high enough; its open
/// risers barred or closed; its winders widened or turned to landings; its floor cut for
/// headroom. What its type has to change, it changes on a copy of the type given to this stair
/// alone, so the stairs sharing the type are left as they are. What it cannot change - a door
/// swinging over its landing, a floor too low over it - it says.
/// </summary>
public static class StairFixes
{
    /// <summary>Everything about a stair the fixes may change on the stair itself.</summary>
    private sealed record Settings(StairRailings Railings, StairCorner Corner, bool CutsFloorAbove, bool CutsRoof, StairState Geometry)
    {
        public static Settings Of(Stair stair) => new(stair.Railings, stair.Corner, stair.CutsFloorAbove, stair.CutsRoof, StairState.Of(stair));

        public void ApplyTo(Stair stair)
        {
            (stair.Railings, stair.Corner, stair.CutsFloorAbove, stair.CutsRoof) = (Railings, Corner, CutsFloorAbove, CutsRoof);
            Geometry.ApplyTo(stair);
        }
    }

    private sealed class SettingsCommand : IUndoableCommand
    {
        private readonly Stair _stair;
        private readonly Settings _before, _after;

        public SettingsCommand(Stair stair, Settings before, Settings after) => (_stair, _before, _after) = (stair, before, after);

        public string Name => "Make Stair Pass";

        public void Redo() => _after.ApplyTo(_stair);

        public void Undo() => _before.ApplyTo(_stair);
    }

    /// <summary>
    /// The change that makes a stair meet its rules, with what it did - or null, saying why
    /// nothing would: it passes already, or nothing it can change would help. The change is
    /// worked out and put back, for the caller to make as one step to undo.
    /// </summary>
    public static IUndoableCommand? MakeItPass(BimDocument document, Stair stair, out string message)
    {
        var problems = Stairs.Problems(document, stair);
        if (problems.Count == 0)
        {
            message = $"It meets {Stairs.Label(document.ProjectInformation.ClearanceStandard)} already.";
            return null;
        }

        var type = Stairs.TypeOf(document, stair);
        var copy = (StairType)TypeDuplicates.Duplicate(type, TypeNames.Unique(document, $"{type.Name} (to pass)"))!;
        var (before, typeId) = (Settings.Of(stair), stair.TypeId);
        var changes = new List<string>();
        Settings after;
        IReadOnlyList<string> left;

        // Hung by its head from a terrace or a balcony, it stays hung there as it changes.
        var hung = Stairs.HungFrom(document, stair);
        document.AddType(copy);
        stair.TypeId = copy.Id;
        try
        {
            // A few rounds: widening it can mend its winders, more risers its pitch.
            for (var round = 0; round < 3 && Stairs.Problems(document, stair).Count > 0; round++)
            {
                var start = stair.Start;
                if (!Fix(document, stair, copy, changes)) break;
                if (hung is not { } head) continue;

                // Moved by the fix itself - set back to come up at the edge - it hangs from there from now on; otherwise back where it hung.
                if (stair.Start.DistanceTo(start) > 0.5) hung = Stairs.HungFrom(document, stair) ?? hung;
                else Stairs.HangFrom(document, stair, head.Head, head.Up);
            }
            after = Settings.Of(stair);
            left = Stairs.Problems(document, stair);
        }
        finally
        {
            stair.TypeId = typeId;
            before.ApplyTo(stair);
            document.RemoveType(copy);
        }

        if (changes.Count == 0)
        {
            message = $"Nothing it can change would make it pass: {string.Join(" ", problems)}";
            return null;
        }

        var steps = new List<IUndoableCommand>();
        if (!Same(type, copy))
        {
            steps.Add(new AddTypeCommand(document, copy));
            steps.Add(new SetElementsTypeCommand(new[] { stair }, copy));
        }

        if (after != before) steps.Add(new SettingsCommand(stair, before, after));
        var made = string.Join("; ", changes.Distinct());
        message = left.Count == 0
            ? $"It passes now: {made}."
            : $"Changed {made}. Still short: {string.Join(" ", left)}";
        return new CompositeCommand("Make Stair Pass", steps);
    }

    /// <summary>Whether a type's copy is changed from it in anything the fixes touch.</summary>
    private static bool Same(StairType a, StairType b) =>
        (a.MaxRiser, a.Going, a.Nosing, a.OpenRisers, a.RiserBars, a.HandrailHeight, a.BalusterSpacing, a.HandrailProfile, a.HandrailWidth, a.HandrailDepth) ==
        (b.MaxRiser, b.Going, b.Nosing, b.OpenRisers, b.RiserBars, b.HandrailHeight, b.BalusterSpacing, b.HandrailProfile, b.HandrailWidth, b.HandrailDepth)
        && (a.WallClearance, a.HandrailExtension, a.HandrailEnds, a.GuardHeight) == (b.WallClearance, b.HandrailExtension, b.HandrailEnds, b.GuardHeight);

    /// <summary>One round of fixes, each for something it falls short in. False when there was nothing it could do.</summary>
    private static bool Fix(BimDocument document, Stair stair, StairType type, List<string> changes)
    {
        var standard = document.ProjectInformation.ClearanceStandard;
        var us = standard.IsUs();
        var house = us || stair.Use == StairUse.Private;
        var problems = Stairs.Problems(document, stair);
        var count = changes.Count;
        bool Has(string text) => problems.Any(problem => problem.Contains(text, StringComparison.Ordinal));
        void Set<T>(T now, T wanted, Action<T> set, string said)
        {
            if (EqualityComparer<T>.Default.Equals(now, wanted)) return;
            set(wanted);
            changes.Add(said);
        }

        // Its handrails: on, on both sides where it needs them, high enough, gripped, clear of the wall.
        if (Has("It has no handrail")) Set(stair.Railings, StairRailings.Auto, v => stair.Railings = v, "handrails put on");
        if (Has("with no railing:")) Set(stair.Railings, StairRailings.Auto, v => stair.Railings = v, "its open sides guarded");
        if (Has("handrail on both sides") || Has("handrails on both sides") || !house && stair.Railings is StairRailings.LeftSide or StairRailings.RightSide)
            Set(stair.Railings, StairRailings.BothSides, v => stair.Railings = v, "handrails on both sides");
        // 900, or 33 in (840) up alternating treads or a ship's ladder under the IRC, which asks 30 to 34 in there.
        var railHeight = us && stair.Shape is StairShape.AlternatingTread or StairShape.Ladder ? 840.0 : 900.0;
        if (Has("Its handrail is") && Has(" high:")) Set(type.HandrailHeight, railHeight, v => type.HandrailHeight = v, $"handrails {railHeight:0} high");
        if (Has("across:") || Has("deep, or round") || Has(" round:"))
        {
            Set(type.HandrailProfile, HandrailProfile.Round, v => type.HandrailProfile = v, "a round 44 handrail");
            type.HandrailWidth = 44;
        }

        if (Has("off the wall")) Set(type.WallClearance, 60.0, v => type.WallClearance = v, "handrails 60 off the wall");
        if (Has("ends are cut off")) Set(type.HandrailEnds, HandrailEnds.ReturnToWall, v => type.HandrailEnds = v, "handrail ends returned to the wall");
        if (Has("run on 300")) Set(type.HandrailExtension, 300.0, v => type.HandrailExtension = v, "handrails run on 300 past its ends");
        if (Has("past its top riser and a tread's depth") && Math.Max(305, Stairs.Layout(document, stair).Going) is var extension)
            Set(type.HandrailExtension, extension, v => type.HandrailExtension = v, $"handrails run on {extension:0} past its ends");
        if (Has("Its guarding is"))
        {
            var guard = standard == ClearanceStandard.Ada && stair.Use != StairUse.Private ? 1070.0 : us ? 920.0 : house ? 900.0 : 1100.0;
            Set(type.GuardHeight, guard, v => type.GuardHeight = v, $"guarding {guard:0} high where level");
        }
        if (Has("balusters are")) Set(type.BalusterSpacing, 120.0, v => type.BalusterSpacing = v, "balusters 120 apart");

        // Its risers: barred where open, closed where they may not be open, its nosings as asked.
        if (Has("risers are open") || Has("leave") && type.RiserBars)
            Set(type.OpenRisers, false, v => type.OpenRisers = v, "closed risers");
        else if (Has("leave")) Set(type.RiserBars, true, v => type.RiserBars = v, "bars across its open risers");
        if (Has("overlap by")) Set(type.Nosing, 20.0, v => type.Nosing = v, "nosings of 20");
        if (Has("Its nosings are") || Has("Its nosings stand")) Set(type.Nosing, 25.0, v => type.Nosing = v, "nosings of 25");

        // An alternating tread device under the IRC: treads 5 in (127) deep, a tread every half its going - and 8½ in (216) with their nosing.
        if (Has("deep: the IRC asks for 127")) Set(type.Going, Math.Max(type.Going, 255.0), v => type.Going = v, "a tread every 127 (5 in)");
        if (Has("with their nosing:"))
        {
            var nosing = Math.Ceiling((216 - Stairs.Layout(document, stair).Going) / 5) * 5;
            if (nosing > type.Nosing) Set(type.Nosing, nosing, v => type.Nosing = v, $"nosings of {nosing:0}");
        }

        // Its width: as wide as it must be.
        var layout = Stairs.Layout(document, stair);
        var wide = Has("wide:") || Has("between handrails") || Has("landings are") ? WidthWanted(stair, layout, standard) : stair.Width;
        if (Has("winders' going")) wide = Math.Max(wide, 860);
        if (wide > stair.Width + 0.5) Set(stair.Width, wide, v => stair.Width = v, $"{wide:0} wide");

        // Winders the IRC's walkline cannot be had on, or balanced ones too tight at their inside: a landing, or a newel's.
        if (Has("winders are too shallow at the walkline") || us && Has("winders are") && Has("narrow end"))
            Set(stair.Corner, StairCorner.Landing, v => stair.Corner = v, "a landing instead of winders");
        else if (stair.Corner == StairCorner.BalancedWinders && Has("winders are") && Has("narrow end"))
            Set(stair.Corner, StairCorner.Winders, v => stair.Corner = v, "winders round a newel");

        // Its head run on past the edge of the deck it comes up onto: its foot set back, so it comes up at the edge.
        if (Has("comes up at the edge") && Stairs.Overrun(document, stair, Stairs.Layout(document, stair), stair.TopLevelId) is { } overrun)
        {
            stair.Start += overrun.Back * overrun.By;
            changes.Add($"its foot set back {overrun.By:0}, to come up at the edge of the floor");
        }

        // Its floor cut where it wants headroom.
        if (Has("headroom over step")) Set(stair.CutsFloorAbove, true, v => stair.CutsFloorAbove = v, "the floor over it cut");
        if (Has("under the roof")) Set(stair.CutsRoof, true, v => stair.CutsRoof = v, "the roof over it cut");

        // A spiral or curve that comes round over itself too low: wider, so it takes more treads to go round.
        if (Has("comes round over itself") && layout.TreadsPerTurn is not null)
        {
            var type0 = Stairs.TypeOf(document, stair);
            var needed = Stairs.Headroom(standard, layout.Shape) + (type0.Construction == StairConstruction.Timber ? type0.TreadThickness : layout.Rise + 150);
            var middle = needed * layout.Going / (2 * Math.PI * layout.Rise);
            var width = Math.Ceiling(2 * (middle - layout.InnerRadius) / 10) * 10;
            if (width > stair.Width && width <= 2000) Set(stair.Width, width, v => stair.Width = v, $"{width:0} wide, to come round higher");
        }

        // A straight run rising too far for the IRC: a landing halfway up it.
        if (Has("with no landing") && stair.Shape == StairShape.Straight)
        {
            var (from, to) = layout.TopEdge;
            var head = from.MidpointTo(to);
            var half = (head - stair.Start).Dot(stair.Up) / 2;
            StairGrips.SetPath(stair, new[] { stair.Start, stair.Start + stair.Up * half, head + stair.Up * layout.Width });
            changes.Add("a landing halfway up");
        }

        // Its risers and going worked out afresh, as near as they can be to what it has.
        if (Has("Each riser") || Has("Each going") || Has("Each tread") || Has("pitched at") || Has("Twice the rise") || Has("going along the middle")
            || Has("Its going") || Has("Its treads are") && Has("walkline"))
            RiseAndGoing(document, stair, type, standard, changes);

        return changes.Count > count;
    }

    /// <summary>How wide a stair must be: the IRC's 36 in; a spiral's 800 (26 in under the IRC); 1000 between handrails, and landings 1200 long, on a stair other than a house's.</summary>
    private static double WidthWanted(Stair stair, StairLayout layout, ClearanceStandard standard)
    {
        if (standard == ClearanceStandard.Ada && stair.Use != StairUse.Private && layout.Shape is not (StairShape.Spiral or StairShape.AlternatingTread or StairShape.Ladder))
            return Math.Max(stair.Width, 1118);
        if (standard.IsUs()) return Math.Max(stair.Width, layout.Shape == StairShape.Spiral ? 660 : stair.Shape is StairShape.AlternatingTread or StairShape.Ladder ? 510 : 920);
        if (layout.Shape == StairShape.Spiral) return Math.Max(stair.Width, 800);
        return stair.Use == StairUse.Private ? stair.Width : Math.Max(stair.Width, 1200);
    }

    /// <summary>
    /// Its risers and going made to fit its rules: the storey's height shared among as few
    /// risers as keep within the rise it may have and the most it needs, with a going within its
    /// limits, its pitch and twice its rise and its going - as near as can be to what it has.
    /// </summary>
    private static void RiseAndGoing(BimDocument document, Stair stair, StairType type, ClearanceStandard standard, List<string> changes)
    {
        var layout = Stairs.Layout(document, stair);
        var height = layout.Top - layout.Bottom;
        var us = standard.IsUs();

        // Rise, going, pitch and 2R+G: as the stair is for, and what it is.
        var (riseLeast, riseMost, goingLeast, goingMost, pitchMost, twiceLeast, twiceMost) = (stair.Shape, stair.Use, us) switch
        {
            (StairShape.Spiral, _, true) => (100.0, 241.0, 171.0 * (layout.InnerRadius + layout.Width / 2) / (layout.InnerRadius + Math.Min(305, layout.Width)), 1000.0, 90.0, 0.0, 9999.0),
            (StairShape.Spiral, _, false) => (170.0, 220.0, Math.Max(190, 120 * (layout.InnerRadius + layout.Width / 2) / (layout.InnerRadius + Math.Min(270, layout.Width / 2))), 1000.0, 90.0, 0.0, 9999.0),
            (StairShape.AlternatingTread or StairShape.Ladder, _, true) => (100.0, 241.0, 0.0, 1000.0, 90.0, 0.0, 9999.0),
            (StairShape.AlternatingTread or StairShape.Ladder, _, false) => (100.0, 220.0, 220.0, 1000.0, 90.0, 0.0, 9999.0),
            (_, not StairUse.Private, true) when standard == ClearanceStandard.Ada => (102.0, 178.0, 279.0, 600.0, 90.0, 0.0, 9999.0),
            (_, _, true) => (100.0, 196.0, 254.0, 600.0, 90.0, 0.0, 9999.0),
            (_, StairUse.Utility, false) => (150.0, 190.0, 250.0, 400.0, 90.0, 550.0, 700.0),
            (_, StairUse.GeneralAccess, false) => (150.0, 170.0, 250.0, 400.0, 90.0, 550.0, 700.0),
            _ => (150.0, 220.0, 220.0, 300.0, 42.0, 550.0, 700.0)
        };

        // A house's stair outside: goings of 280 at least.
        if (!us && stair.Outside && stair.Use == StairUse.Private && stair.Shape is not (StairShape.Spiral or StairShape.AlternatingTread or StairShape.Ladder))
            goingLeast = Math.Max(goingLeast, 280);

        (int Risers, double Going)? best = null;
        var bestCost = double.MaxValue;
        for (var risers = Math.Max(2, (int)Math.Ceiling(height / riseMost - 1e-9)); risers <= Math.Max(2, (int)Math.Floor(height / riseLeast + 1e-9)) && risers < 200; risers++)
        {
            var rise = height / risers;
            var least = Math.Max(goingLeast, Math.Max(twiceLeast - 2 * rise, pitchMost >= 90 ? 0 : rise / Math.Tan(pitchMost * Math.PI / 180)));
            var most = Math.Min(goingMost, twiceMost - 2 * rise);
            var (low, high) = (Math.Ceiling(least / 5) * 5, Math.Floor(most / 5) * 5);
            if (low > high) continue;
            var going = Math.Clamp(type.Going, low, high);
            if (going < least - 1e-9 || going > most + 1e-9) continue;

            var cost = Math.Abs(risers - layout.Risers) * 100 + Math.Abs(going - type.Going);
            if (cost >= bestCost) continue;
            (best, bestCost) = ((risers, going), cost);
        }

        if (best is not { } chosen) return;
        if (chosen.Risers != layout.Risers)
        {
            type.MaxRiser = Math.Round(height / chosen.Risers + 0.5, 1);
            changes.Add($"{chosen.Risers} risers of {height / chosen.Risers:0.#} rather than {layout.Risers} of {layout.Rise:0.#}");
        }

        if (Math.Abs(chosen.Going - type.Going) > 0.5)
        {
            changes.Add($"a going of {chosen.Going:0} rather than {type.Going:0}");
            type.Going = chosen.Going;
        }
    }
}
