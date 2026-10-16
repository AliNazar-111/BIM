using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Core.Architecture;

/// <summary>
/// A ramp made to meet its rules, as far as its own settings can: made no steeper than 1:12,
/// widened to the least its rules ask, given its landings at either end, its handrails put on as
/// asked and its sides guarded, closed underneath; and on a copy of its type given to it alone -
/// so the ramps sharing the type are left as they are - its handrails made the height, length
/// and size asked and its kerb 100 high. Hung by its head from a door, it stays there as it
/// changes. What it cannot change - a lift or steps beside it, a door swinging over its landing,
/// a floor too low over it - it says.
/// </summary>
public static class RampFixes
{
    /// <summary>Everything about a ramp the fixes may change on the ramp itself.</summary>
    private sealed record Settings(double Gradient, double Width, double TopLanding, double BottomLanding, StairRailings Railings, bool ClosedUnderneath)
    {
        public static Settings Of(Ramp ramp) => new(ramp.Gradient, ramp.Width, ramp.TopLanding, ramp.BottomLanding, ramp.Railings, ramp.ClosedUnderneath);

        public void ApplyTo(Ramp ramp) =>
            (ramp.Gradient, ramp.Width, ramp.TopLanding, ramp.BottomLanding, ramp.Railings, ramp.ClosedUnderneath) =
            (Gradient, Width, TopLanding, BottomLanding, Railings, ClosedUnderneath);
    }

    private sealed class SettingsCommand : IUndoableCommand
    {
        private readonly Ramp _ramp;
        private readonly Settings _before, _after;

        public SettingsCommand(Ramp ramp, Settings before, Settings after) => (_ramp, _before, _after) = (ramp, before, after);

        public string Name => "Make Ramp Pass";

        public void Redo() => _after.ApplyTo(_ramp);

        public void Undo() => _before.ApplyTo(_ramp);
    }

    /// <summary>
    /// The change that makes a ramp meet its rules, with what it did - or null, saying why nothing
    /// would: it passes already, or nothing it can change would help. The change is worked out and
    /// put back, for the caller to make as one step to undo.
    /// </summary>
    public static IUndoableCommand? MakeItPass(BimDocument document, Ramp ramp, out string message)
    {
        var problems = Ramps.Problems(document, ramp);
        if (problems.Count == 0)
        {
            message = $"{Ramps.Verdict(document, ramp)} already.";
            return null;
        }

        var type = Ramps.TypeOf(document, ramp);
        var copy = (RampType)TypeDuplicates.Duplicate(type, TypeNames.Unique(document, $"{type.Name} (to pass)"))!;
        var (before, typeId) = (Settings.Of(ramp), ramp.TypeId);
        var changes = new List<string>();
        Settings after;
        IReadOnlyList<string> left;

        document.AddType(copy);
        ramp.TypeId = copy.Id;
        try
        {
            // A few rounds: made gentler it is longer, and may then want more of it.
            for (var round = 0; round < 3 && Ramps.Problems(document, ramp).Count > 0; round++)
                if (!Fix(document, ramp, copy, changes)) break;
            after = Settings.Of(ramp);
            left = Ramps.Problems(document, ramp);
        }
        finally
        {
            ramp.TypeId = typeId;
            before.ApplyTo(ramp);
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
            steps.Add(new SetElementsTypeCommand(new[] { ramp }, copy));
        }

        if (after != before) steps.Add(new SettingsCommand(ramp, before, after));
        var made = string.Join("; ", changes.Distinct());
        message = left.Count == 0 ? $"It passes now: {made}." : $"Changed {made}. Still short: {string.Join(" ", left)}";
        return new CompositeCommand("Make Ramp Pass", steps);
    }

    /// <summary>Whether a type's copy is changed from it in anything the fixes touch.</summary>
    private static bool Same(RampType a, RampType b) =>
        (a.HandrailHeight, a.HandrailExtension, a.HandrailDiameter, a.KerbHeight) == (b.HandrailHeight, b.HandrailExtension, b.HandrailDiameter, b.KerbHeight);

    /// <summary>One round of fixes, each for something it falls short in. False when there was nothing it could do.</summary>
    private static bool Fix(BimDocument document, Ramp ramp, RampType type, List<string> changes)
    {
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var problems = Ramps.Problems(document, ramp);
        var count = changes.Count;
        bool Has(string text) => problems.Any(problem => problem.Contains(text, StringComparison.Ordinal));
        void Set<T>(T now, T wanted, Action<T> set, string said)
        {
            if (EqualityComparer<T>.Default.Equals(now, wanted)) return;
            set(wanted);
            changes.Add(said);
        }

        // Its own settings: gentle enough, wide enough, landed at either end, handrailed and guarded as asked, closed underneath.
        if (Has("it is steeper than") && Math.Max(12, Ramps.SteepestAllowed(document, ramp)) is var steepest)
            Set(ramp.Gradient, steepest, v => ramp.Gradient = v, $"made 1:{steepest:0}");
        var (least, _) = Ramps.LeastWidth(document, ramp);
        if (Has(" wide: ") && least > 0) Set(ramp.Width, least, v => ramp.Width = v, $"widened to {least:0}");
        var end = Ramps.EndLanding(document, ramp);
        if (Has("Its head comes up onto")) Set(ramp.TopLanding, end, v => ramp.TopLanding = v, $"a landing {end:0} long at its head");
        if (Has("Its foot starts from")) Set(ramp.BottomLanding, end, v => ramp.BottomLanding = v, $"a landing {end:0} long at its foot");
        if (Has("It has handrails") || Has("with nothing along it")) Set(ramp.Railings, StairRailings.Auto, v => ramp.Railings = v, "handrails and guarding as asked");
        if (Has("The space under it")) Set(ramp.ClosedUnderneath, true, v => ramp.ClosedUnderneath = v, "closed underneath");

        // Its type's copy: handrails the height, run-on and size asked, a kerb 100 high.
        if (Has("Its handrails stand")) Set(type.HandrailHeight, 900.0, v => type.HandrailHeight = v, "handrails 900 high");
        if (Has("Its handrails run on")) Set(type.HandrailExtension, us ? 305.0 : 300.0, v => type.HandrailExtension = v, $"handrails running on {(us ? 305 : 300)} past its ends");
        if (Has("Its handrails are")) Set(type.HandrailDiameter, 42.0, v => type.HandrailDiameter = v, "handrails 42 round");
        if (Has("Its kerb is") || Has("no edge protection")) Set(type.KerbHeight, 100.0, v => type.KerbHeight = v, "a kerb 100 high");

        return changes.Count > count;
    }
}
