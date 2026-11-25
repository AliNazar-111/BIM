using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// The electricity escape and firefighting rely on (round 138), by Approved Document B Volume 2 5.26 and 5.30.
///
/// In a building other than a dwelling: an escape stair's lights on a circuit of their own - no lighting circuit feeding lights both in an
/// escape stair's rooms and in another part of the escape routes, a corridor, lobby, hall or landing (5.26). A protected circuit to equipment
/// that works in a fire - a fire alarm's panel, call points, sounders, beacons and detectors, an emergency door release, a firefighting lift -
/// wired in fire-resisting cable, PH 30 to BS EN 50200 (5.30a), and feeding nothing else (5.30c); the fire alarm's own wiring the same.
///
/// Not done: the cable's route through parts of negligible fire risk (5.30b) and its supports (5.29); BS 5839-1's, BS 5266-1's and BS 8519's
/// own grades of cable (5.31); emergency lighting from a central battery, the lights here being self-contained.
/// </summary>
public static class FireCircuits
{
    /// <summary>Whether a fitting is equipment working in a fire, on a protected circuit.</summary>
    public static bool LifeSafety(ComponentType type) =>
        type.Form is ComponentForm.FireAlarmPanel or ComponentForm.FireAlarmCallPoint or ComponentForm.FireAlarmSounder or ComponentForm.VisualAlarm
            or ComponentForm.SmokeDetector or ComponentForm.HeatDetector or ComponentForm.DoorRelease
        || type.Form == ComponentForm.Lift && FirefightingShafts.FirefightingLift(type);

    private static bool Light(ComponentType type) => type.Form is ComponentForm.CeilingLight or ComponentForm.WallLight or ComponentForm.EmergencyLight or ComponentForm.ExitSign;

    /// <summary>What the circuits feeding escape and firefighting fall short of, each said on its circuit.</summary>
    public static IEnumerable<MepProblem> Problems(BimDocument document)
    {
        if (document.ProjectInformation.ClearanceStandard.IsUs() || !HorizontalEscape.Applies(document)) yield break;
        var circuits = document.All<MepSystem>().Where(system => system.Kind is SystemKind.Power or SystemKind.FireAlarm).ToList();
        if (circuits.Count == 0) yield break;
        var stairRooms = VerticalEscape.Stairs(document).SelectMany(stair => stair.Rooms).ToHashSet();

        foreach (var circuit in circuits)
        {
            var members = circuit.Members.Select(id => document.Find<Component>(id)).OfType<Component>()
                .Select(piece => (Piece: piece, Type: document.FindType<ComponentType>(piece.TypeId))).Where(entry => entry.Type is not null).Select(entry => (entry.Piece, Type: entry.Type!)).ToList();
            if (members.Count == 0) continue;

            // A protected circuit: fire-resisting cable (5.30a), and nothing else on it (5.30c).
            var safety = members.Where(entry => LifeSafety(entry.Type)).ToList();
            if (safety.Count > 0 || circuit.Kind == SystemKind.FireAlarm && HorizontalEscape.Applies(document))
            {
                var names = (safety.Count > 0 ? safety : members).Select(entry => entry.Type.Name).Distinct().ToList();
                var said = string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" and {names.Count - 3} more" : string.Empty);
                if (!circuit.FireResisting)
                    yield return new MepProblem(circuit, $"it feeds {said}, equipment working in a fire, and its cable is not fire-resisting - Approved Document B Volume 2 (5.30a) asks a protected circuit to equipment that operates during a fire wired in cable of PH 30 or better to BS EN 50200: tick Fire-Resisting Cable (PH 30) once it is");
                var others = members.Where(entry => !LifeSafety(entry.Type)).Select(entry => entry.Type.Name).Distinct().ToList();
                if (safety.Count > 0 && others.Count > 0)
                    yield return new MepProblem(circuit, $"it feeds {said} and also {string.Join(", ", others.Take(3))}{(others.Count > 3 ? $" and {others.Count - 3} more" : string.Empty)} - Approved Document B Volume 2 (5.30c) asks a protected circuit to equipment working in a fire separate from any circuit for another purpose");
            }

            // An escape stair's lights on a circuit of their own (5.26).
            if (circuit.Kind != SystemKind.Power) continue;
            var lights = members.Where(entry => Light(entry.Type)).Select(entry => RoomsOf.Containing(document, entry.Piece.LevelId, entry.Piece.Location)).OfType<Room>().ToList();
            var stair = lights.FirstOrDefault(stairRooms.Contains);
            var route = lights.FirstOrDefault(room => !stairRooms.Contains(room) && FireSeparation.Circulation(room) && !room.Name.Contains("stair", StringComparison.OrdinalIgnoreCase));
            if (stair is not null && route is not null)
                yield return new MepProblem(circuit, $"it lights the {stair.Name}, an escape stair, and the {route.Name} - Approved Document B Volume 2 (5.26) asks an escape stair's lighting on a separate circuit from the supply to any other part of the escape route");
        }
    }
}
