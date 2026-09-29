using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BimWindow = BIMDesigner.Core.Architecture.Window;

namespace BIMDesigner.Tests;

/// <summary>
/// An audit of editing a house as the app makes one - walls drawn clockwise, a partition, doors
/// and windows, a floor, a hip roof picked off the walls with them attached, and a dormer with
/// a window - through an undo stack that does what the app does after every change: roofs put
/// back on their walls, dormers on their roofs.
///
/// Every edit is done, undone and redone, and must leave the project as it was each time -
/// to a millionth of a millimetre, since working geometry out afresh differs from it in the
/// last decimal place - and leave a model that builds, with no door or window off its wall
/// and nothing pointing at what is gone. And the edits that change a wall must not move the
/// windows in it.
/// </summary>
public class HouseEditAuditTests
{
    private static string State(BimDocument document) =>
        System.Text.RegularExpressions.Regex.Replace(
            string.Join('\n', ProjectFile.ToJson(document).Split('\n').Where(line => !line.Contains("\"savedUtc\""))),
            @"-?\d+\.\d+(E-?\d+)?",
            match => Math.Round(double.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture), 6)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A house as the app makes one: clockwise walls, a partition, doors, windows, a floor and ceiling, a hip roof picked off the walls with them attached, and a dormer with a window.</summary>
    internal sealed class House
    {
        public BimDocument Document = BimDocument.CreateDefault();
        public List<Wall> Outer = new();
        public Wall Partition = null!;
        public Roof Roof = null!;
        public Roof? Dormer;
        public BimWindow FrontWindow = null!;
        public WallType Exterior = null!;

        public House(bool dormer = true)
        {
            var ground = Document.Levels[0];
            var first = Document.Levels[1];
            Exterior = Document.TypesOf<WallType>().First(t => t.Name.StartsWith("Exterior"));
            var interior = Document.TypesOf<WallType>().FirstOrDefault(t => t.Name.StartsWith("Interior")) ?? Exterior;

            // Clockwise, so their outsides face out.
            var corners = new[] { new Point2D(0, 0), new Point2D(0, 7000), new Point2D(10000, 7000), new Point2D(10000, 0) };
            for (var i = 0; i < 4; i++)
            {
                var wall = new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = Exterior.Id, LevelId = ground.Id, TopLevelId = first.Id };
                Document.Add(wall);
                Outer.Add(wall);
            }

            Partition = new Wall { Start = new Point2D(4000, 0), End = new Point2D(4000, 7000), TypeId = interior.Id, LevelId = ground.Id, TopLevelId = first.Id };
            Document.Add(Partition);

            var door = Document.TypesOf<DoorType>().First(t => !t.CurtainPanel);
            var window = Document.TypesOf<WindowType>().First(t => t.Height == 1200);
            Document.Add(new Door { HostWallId = Outer[3].Id, LevelId = ground.Id, TypeId = door.Id, DistanceAlongWall = 3000, Mark = "1" });
            Document.Add(new Door { HostWallId = Partition.Id, LevelId = ground.Id, TypeId = door.Id, DistanceAlongWall = 3500, Mark = "2" });
            FrontWindow = new BimWindow { HostWallId = Outer[3].Id, LevelId = ground.Id, TypeId = window.Id, DistanceAlongWall = 7500, SillHeight = 900, Mark = "1" };
            Document.Add(FrontWindow);
            Document.Add(new BimWindow { HostWallId = Outer[0].Id, LevelId = ground.Id, TypeId = window.Id, DistanceAlongWall = 3500, SillHeight = 900, Mark = "2" });
            Document.Add(new BimWindow { HostWallId = Outer[1].Id, LevelId = ground.Id, TypeId = window.Id, DistanceAlongWall = 5000, SillHeight = 900, Mark = "3" });

            var floor = new Floor { LevelId = ground.Id, TypeId = Document.TypesOf<FloorType>().First().Id };
            floor.SetBoundary(corners);
            Document.Add(floor);

            // Picked off the walls' outsides, as Pick Walls does, corners closed.
            var sketch = new List<RoofSketchLine>();
            foreach (var wall in Outer)
            {
                var outside = wall.Start.MidpointTo(wall.End) + wall.Direction.PerpendicularLeft() * 1000;
                var line = RoofSketch.PickWall(Document, wall, outside, 400, false, true, 42)!;
                sketch.Add(line);
                RoofSketch.CloseCorners(sketch, line, 2 * (Exterior.Structure.TotalWidth + 400) + 200);
            }

            var check = RoofSketch.Check(sketch);
            if (!check.IsValid) throw new InvalidOperationException(check.Problem);
            Roof = new Roof { TypeId = Document.TypesOf<RoofType>().First().Id, LevelId = ground.Id, HeightOffset = first.Elevation };
            Roof.SetBoundary(check.Boundary);
            Roof.SetEdges(check.Edges);
            Document.Add(Roof);
            foreach (var wall in Outer) wall.TopAttachedTo = Roof.Id;

            if (!dormer) return;
            if (Dormers.Add(Document, Roof, new Point2D(5000, 800), DormerSettings.Default, Exterior.Id, out var problem, out _) is null)
                throw new InvalidOperationException(problem);
            Dormer = Document.Elements.OfType<Roof>().Single(roof => !ReferenceEquals(roof, Roof));
            var front = Dormers.Parts(Document, Dormer).OfType<Wall>().ElementAt(1);
            Document.Add(new BimWindow { HostWallId = front.Id, LevelId = ground.Id, TypeId = window.Id, DistanceAlongWall = front.Length / 2, SillHeight = 100, WidthOverride = 900, HeightOverride = 700 });
        }
    }

    /// <summary>What is wrong with a model, if anything: things that do not build, openings outside their walls, references to what is gone.</summary>
    private static List<string> Problems(BimDocument document)
    {
        var problems = new List<string>();
        try { ModelMeshBuilder.Build(document); } catch (Exception e) { problems.Add("3D threw " + e.Message); }
        try
        {
            var marker = new SectionMarker { Start = new Point2D(-2000, 3500), End = new Point2D(12000, 3500), LevelId = document.Levels[0].Id };
            SectionProjection.Build(document, marker);
        }
        catch (Exception e) { problems.Add("section threw " + e.Message); }

        var ids = document.Elements.Select(e => e.Id).ToHashSet();
        foreach (var opening in document.Elements.OfType<Opening>())
        {
            var wall = document.Walls.FirstOrDefault(w => w.Id == opening.HostWallId);
            var type = document.FindType<OpeningType>(opening.TypeId);
            if (wall is null) { problems.Add($"{opening.GetType().Name} {opening.Mark} has no wall"); continue; }
            if (type is not null && !opening.FitsWithin(wall, type))
                problems.Add($"{opening.GetType().Name} {opening.Mark} runs off its wall ({opening.GetSpan(type).From:0}..{opening.GetSpan(type).To:0} of {wall.Length:0})");
        }

        foreach (var wall in document.Walls)
        {
            if (wall.TopAttachedTo is { } top && !ids.Contains(top)) problems.Add("a wall is attached to a missing top");
            if (wall.BaseAttachedTo is { } foot && !ids.Contains(foot)) problems.Add("a wall stands on a missing base");
            if (wall.Length < 1) problems.Add("a wall has no length");
        }

        foreach (var roof in document.Elements.OfType<Roof>())
        {
            if (roof.JoinedTo is { } joined && !ids.Contains(joined)) problems.Add("a roof is joined to a missing roof");
            if (!roof.IsExtrusion && Polygon2D.SignedArea(roof.Boundary) <= 0) problems.Add("a roof outline is inside out or empty");
        }

        return problems;
    }

    private static void Check(System.Text.StringBuilder log, string what, Func<House, IUndoableCommand?> edit, Action<House, System.Text.StringBuilder>? inspect = null, bool alreadyDone = false)
    {
        try
        {
            var house = new House();
            var document = house.Document;
            var history = new UndoStack();
            history.Changed += (_, _) => { RoofSketch.FollowWalls(document); Dormers.FollowRoofs(document); };
            RoofSketch.FollowWalls(document);
            Dormers.FollowRoofs(document);

            var before = State(document);
            var command = edit(house);
            if (command is null) { log.AppendLine($"{what}: NO COMMAND"); return; }
            if (alreadyDone) history.Record(command); else history.Execute(command);
            var after = State(document);

            var notes = new List<string>();
            if (before == after) notes.Add("changed nothing");
            notes.AddRange(Problems(document).Select(p => "after: " + p));
            var extra = new System.Text.StringBuilder();
            inspect?.Invoke(house, extra);

            history.Undo();
            if (State(document) != before) notes.Add("UNDO DIFFERS: " + FirstDifference(before, State(document)));
            history.Redo();
            if (State(document) != after) notes.Add("REDO DIFFERS: " + FirstDifference(after, State(document)));

            log.AppendLine($"{what}: {(notes.Count == 0 ? "ok" : string.Join(" | ", notes))}{(extra.Length > 0 ? " -- " + extra : "")}");
        }
        catch (Exception e) { log.AppendLine($"{what}: THREW {e.GetType().Name}: {e.Message}\n{e.StackTrace?.Split('\n').FirstOrDefault()}"); }
    }

    private static string FirstDifference(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        for (var i = 0; i < Math.Min(la.Length, lb.Length); i++)
            if (la[i] != lb[i])
                return $"line {i}: '{la[i].Trim()}' vs '{lb[i].Trim()}' (context: {string.Join(" ", la.Skip(Math.Max(0, i - 6)).Take(6).Select(l => l.Trim()))})";
        return $"lengths {la.Length} vs {lb.Length}";
    }

    [Fact]
    public void Audit()
    {
        var log = new System.Text.StringBuilder();
        log.AppendLine("house healthy: " + string.Join("; ", Problems(new House().Document).DefaultIfEmpty("yes")));

        Check(log, "move whole house", h => new MoveElementsCommand(h.Document.Elements.Where(e => e is not Opening).ToList(), new Vector2D(3000, 2000), document: h.Document),
            (h, x) => x.Append($"roof corner {h.Roof.Boundary[0]}, dormer openings {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "lengthen front wall by Length", h => h.Outer[3].LengthChange(h.Document, 12000),
            (h, x) => x.Append($"walls {string.Join(",", h.Outer.Select(w => $"{w.Length:0}"))}, roof x max {h.Roof.Boundary.Max(p => p.X):0}, openings in main {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "shorten front wall past its window", h => h.Outer[3].LengthChange(h.Document, 7000),
            (h, x) => x.Append($"front window along {h.FrontWindow.DistanceAlongWall:0} of {h.Outer[3].Length:0}"));

        Check(log, "drag side wall out (move wall + corners)", h =>
        {
            var side = h.Outer[2];
            var shift = new Vector2D(1500, 0);
            var commands = new List<IUndoableCommand> { new MoveWallCommand(side, side.Start, side.End, side.Start + shift, side.End + shift, "Move") };
            commands.Add(new MoveWallCommand(h.Outer[1], h.Outer[1].Start, h.Outer[1].End, h.Outer[1].Start, h.Outer[1].End + shift, "Move").KeepingOpenings(h.Document));
            commands.Add(new MoveWallCommand(h.Outer[3], h.Outer[3].Start, h.Outer[3].End, h.Outer[3].Start + shift, h.Outer[3].End, "Move").KeepingOpenings(h.Document));
            return new CompositeCommand("Drag", commands);
        }, (h, x) => x.Append($"roof x max {h.Roof.Boundary.Max(p => p.X):0}"));

        Check(log, "change outer walls to a thinner type", h =>
        {
            var thinner = h.Document.TypesOf<WallType>().Where(t => t.Id != h.Exterior.Id && h.Document.FindType<CurtainWallType>(t.Id) is null && h.Document.FindType<StackedWallType>(t.Id) is null)
                .OrderBy(t => t.Structure.TotalWidth).First();
            return new SetElementsTypeCommand(h.Outer, thinner);
        }, (h, x) => x.Append($"roof x min {h.Roof.Boundary.Min(p => p.X):0}"));

        Check(log, "flip outer walls", h => new FlipWallsCommand(h.Outer), (h, x) => x.Append($"roof x min {h.Roof.Boundary.Min(p => p.X):0}"));

        Check(log, "mirror whole house", h => new MirrorElementsCommand(h.Document.Elements.Where(e => e is not Opening).ToList(), Line2D.Through(new Point2D(15000, 0), new Point2D(15000, 1000))),
            (h, x) => x.Append($"roof area {Polygon2D.SignedArea(h.Roof.Boundary) / 1e6:0.0} m2, dormer openings {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "split front wall at 5 m", h => new SplitWallCommand(h.Document, h.Outer[3], h.Outer[3].LocationCurve.PointAt(5000)),
            (h, x) => x.Append($"front pieces {h.Document.Walls.Count(w => Math.Abs(w.Start.Y) < 1 && Math.Abs(w.End.Y) < 1)}, attached {string.Join(",", h.Document.Walls.Where(w => Math.Abs(w.Start.Y) < 1 && Math.Abs(w.End.Y) < 1).Select(w => w.TopAttachedTo == h.Roof.Id))}, roof edges {h.Roof.Edges.Count}"));

        Check(log, "make roof a gable (sketch)", h =>
        {
            var edges = h.Roof.Edges.Select(e => e.Copy()).ToList();
            for (var i = 0; i < edges.Count; i++)
                if (Math.Abs(h.Roof.Boundary[i].X - h.Roof.Boundary[(i + 1) % edges.Count].X) < 1) edges[i].DefinesSlope = false;
            return new SetRoofSketchCommand(h.Roof, h.Roof.Boundary, edges);
        }, (h, x) => x.Append($"form {h.Roof.Form}, openings {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "roof pitch 35 -> 45 (parameter)", h =>
        {
            var pitch = h.Roof.GetInstanceParameters(h.Document).FirstOrDefault(p => p.Name.Contains("Slope") && !p.IsReadOnly);
            if (pitch is null) return null;
            var old = pitch.Value;
            return new ParameterChangeCommand(pitch, old, old is double d ? d + 10 : old);
        });

        Check(log, "delete main roof", h => new DeleteElementsCommand(h.Document, new[] { h.Roof }),
            (h, x) => x.Append($"walls left {h.Document.Walls.Count()}"));

        Check(log, "delete dormer (all parts)", h => new DeleteElementsCommand(h.Document, Dormers.Parts(h.Document, h.Dormer!)),
            (h, x) => x.Append($"openings in main {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "delete partition with its door", h => new DeleteElementsCommand(h.Document, new[] { h.Partition }));

        Check(log, "paste dormer along", h =>
        {
            var copies = ElementCopy.Duplicate(h.Document, Dormers.Parts(h.Document, h.Dormer!));
            foreach (var copy in copies) if (ElementTransforms.CanMove(copy)) ElementTransforms.Move(copy, new Vector2D(-3000, 0));
            return Dormers.AddCopies(h.Document, copies, "Paste");
        }, (h, x) => x.Append($"openings in main {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "move dormer along", h => new MoveElementsCommand(Dormers.Parts(h.Document, h.Dormer!), new Vector2D(2000, 0), document: h.Document),
            (h, x) => x.Append($"openings in main {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "move dormer up the slope", h => new MoveElementsCommand(Dormers.Parts(h.Document, h.Dormer!), new Vector2D(0, 800), document: h.Document),
            (h, x) => x.Append($"openings in main {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "add dormer (tool)", h => Dormers.Add(h.Document, h.Roof, new Point2D(5000, 6200), DormerSettings.Default with { Shape = DormerShape.Hip }, h.Exterior.Id, out _, out _), alreadyDone: true);

        Check(log, "unjoin dormer roof", h => new SetRoofJoinCommand(h.Dormer!, null));
        Check(log, "close dormer opening", h => new SetDormerOpeningCommand(h.Roof, h.Dormer!.Id, open: false));

        Check(log, "detach outer wall tops", h => new AttachWallsCommand(h.Outer.Select(w => (w, true, (Guid?)null)), "Detach"),
            (h, x) => x.Append($"wall heights {string.Join(",", h.Outer.Select(w => $"{w.GetHeight(h.Document):0}"))}"));

        Check(log, "wall top to unconnected 2500", h => new SetWallTopCommand(h.Partition, null, 0, 2500));

        Check(log, "location line of front wall to finish face exterior", h => new ChangeLocationLineCommand(h.Document, h.Outer[3], WallLocationLine.FinishFaceExterior),
            (h, x) => x.Append($"front wall start {h.Outer[3].Start}, roof y min {h.Roof.Boundary.Min(p => p.Y):0}"));

        Check(log, "bend front wall (with door and window)", h => new BendWallCommand(h.Outer[3], 0, 0.2),
            (h, x) => x.Append($"roof edges {h.Roof.Edges.Count}"));

        Check(log, "gable profile on partition", h => new SetWallProfileCommand(h.Partition, WallProfile.Rectangle(7000, 2400)));

        Check(log, "move first floor level up 500", h => new MoveLevelCommand(h.Document, h.Document.Levels[1], h.Document.Levels[1].Elevation + 500),
            (h, x) => x.Append($"roof offset {h.Roof.HeightOffset:0}, wall heights {string.Join(",", h.Outer.Select(w => $"{w.GetHeight(h.Document):0}"))}"));

        Check(log, "rehost front window to side wall", h => new RehostOpeningCommand(h.FrontWindow, h.Outer[0], 2000));

        Check(log, "flip front window", h => new FlipOpeningCommand(h.FrontWindow, facing: true));

        Check(log, "offset (align) front wall", h => new OffsetWallsCommand(new[] { (h.Outer[3], 100.0) }),
            (h, x) => x.Append($"roof y min {h.Roof.Boundary.Min(p => p.Y):0}"));

        Check(log, "join partition to front (join geometry)", h => new JoinWallsCommand(h.Partition, h.Outer[3], join: true));

        Check(log, "make rectangle of the outer walls 12 x 8", h =>
        {
            var loop = WallRectangle.Find(h.Document, h.Outer, out _);
            return loop?.Make(12000, 8000, RectangleMeasure.WallCentres, out _);
        }, (h, x) => x.Append($"walls {string.Join(",", h.Outer.Select(w => $"{w.Length:0}"))}, roof area {Polygon2D.SignedArea(h.Roof.Boundary) / 1e6:0.0} m2"));

        {
            var h = new House(dormer: false);
            var front = h.Outer[3];
            var before = h.FrontWindow.GetCentre(front);
            var shift = new Vector2D(1500, 0);
            new MoveWallCommand(h.Outer[2], h.Outer[2].Start, h.Outer[2].End, h.Outer[2].Start + shift, h.Outer[2].End + shift, "Move").KeepingOpenings(h.Document).Redo();
            new MoveWallCommand(front, front.Start, front.End, front.Start + shift, front.End, "Move").KeepingOpenings(h.Document).Redo();
            if (before.DistanceTo(h.FrontWindow.GetCentre(front)) > 1e-6)
                log.AppendLine($"FAIL drag east wall out 1.5 m: front window was at {before}, now at {h.FrontWindow.GetCentre(front)}");
        }
        Check(log, "dormer walls to a thinner type", h =>
        {
            var thinner = h.Document.TypesOf<WallType>().Where(t => t.Id != h.Exterior.Id && h.Document.FindType<CurtainWallType>(t.Id) is null && h.Document.FindType<StackedWallType>(t.Id) is null)
                .OrderBy(t => t.Structure.TotalWidth).First();
            return new SetElementsTypeCommand(Dormers.Parts(h.Document, h.Dormer!).OfType<Wall>(), thinner);
        }, (h, x) => x.Append($"dormer outline {string.Join(" ", h.Dormer!.Boundary.Select(p => $"({p.X:0},{p.Y:0})"))}, openings {RoofJoin.Openings(h.Document, h.Roof).Count}"));

        Check(log, "main roof pitch 42 -> 35 under the dormer", h =>
        {
            var edges = h.Roof.Edges.Select(e => { var c = e.Copy(); c.SlopeDegrees = 35; return c; }).ToList();
            return new SetRoofSketchCommand(h.Roof, h.Roof.Boundary, edges);
        }, (h, x) =>
        {
            var dormerTopAtBack = h.Dormer!.Boundary.Max(p => h.Dormer.TopAt(h.Document, p));
            x.Append($"openings {RoofJoin.Openings(h.Document, h.Roof).Count}, dormer walls {string.Join(",", Dormers.Parts(h.Document, h.Dormer).OfType<Wall>().Select(w => WallProfile.Of(h.Document, w) is { } p ? $"{WallProfile.Top(p):0}" : "none"))}");
        });

        // A long session, undone all the way back.
        {
            var h = new House();
            var history = new UndoStack();
            history.Changed += (_, _) => { RoofSketch.FollowWalls(h.Document); Dormers.FollowRoofs(h.Document); };
            var start = State(h.Document);
            var steps = new List<Func<IUndoableCommand?>>
            {
                () => new MoveElementsCommand(Dormers.Parts(h.Document, h.Dormer!), new Vector2D(1500, 0), document: h.Document),
                () =>
                {
                    var copies = ElementCopy.Duplicate(h.Document, Dormers.Parts(h.Document, h.Dormer!));
                    foreach (var copy in copies) if (ElementTransforms.CanMove(copy)) ElementTransforms.Move(copy, new Vector2D(-3500, 0));
                    return Dormers.AddCopies(h.Document, copies, "Paste");
                },
                () => h.Outer[3].LengthChange(h.Document, 11000),
                () => new FlipWallsCommand(new[] { h.Partition }),
                () => new SplitWallCommand(h.Document, h.Outer[0], h.Outer[0].LocationCurve.PointAt(3000)),
                () => new DeleteElementsCommand(h.Document, new[] { h.Roof }),
            };
            var done = 0;
            foreach (var step in steps)
            {
                if (step() is not { } command) continue;
                if (command is CompositeCommand && command.Name == "Add Dormer") history.Record(command); else history.Execute(command);
                done++;
            }
            for (var i = 0; i < done; i++) history.Undo();
            log.AppendLine($"session of {done} edits undone: {(State(h.Document) == start ? "back to the start" : "DIFFERS: " + FirstDifference(start, State(h.Document)))}");
        }

        // Where each window stands in the building, before and after edits that change a wall.
        string Places(House h) => string.Join(" ", h.Document.Elements.OfType<Opening>().Where(o => h.Document.Walls.First(w => w.Id == o.HostWallId).BaseAttachedTo is null)
            .Select(o => { var w = h.Document.Walls.First(x => x.Id == o.HostWallId); var c = o.GetCentre(w); return $"({c.X:0},{c.Y:0})"; }).OrderBy(s => s));
        foreach (var (what, edit) in new (string, Func<House, IUndoableCommand>)[]
                 {
                     ("flip outer walls", h => new FlipWallsCommand(h.Outer)),
                     ("split front wall", h => new SplitWallCommand(h.Document, h.Outer[3], h.Outer[3].LocationCurve.PointAt(5000))),
                     ("change front wall type", h => new SetElementsTypeCommand(new[] { h.Outer[3] }, h.Document.TypesOf<WallType>().Where(t => t.Id != h.Exterior.Id && h.Document.FindType<CurtainWallType>(t.Id) is null && h.Document.FindType<StackedWallType>(t.Id) is null).OrderBy(t => t.Structure.TotalWidth).First())),
                 })
        {
            var h = new House(dormer: false);
            var before = Places(h);
            edit(h).Redo();
            var after = Places(h);
            log.AppendLine($"{what}: windows {(before == after ? "stay put" : $"MOVED {before} -> {after}")}");
        }
        {
            var h = new House(dormer: false);
            var before = Places(h);
            new MirrorElementsCommand(h.Document.Elements.Where(e => e is not Opening).ToList(), Line2D.Through(new Point2D(5000, 0), new Point2D(5000, 1000))).Redo();
            var mirrored = string.Join(" ", before.Split(' ').Select(place =>
            {
                var xy = place.Trim('(', ')').Split(',').Select(double.Parse).ToArray();
                return $"({10000 - xy[0]:0},{xy[1]:0})";
            }).OrderBy(s => s));
            if (Places(h) != mirrored) log.AppendLine($"FAIL mirror about the middle: windows at {Places(h)}, not {mirrored}");
        }

        var failures = log.ToString().Split('\n')
            .Where(line => line.Contains("DIFFERS") || line.Contains("after:") || line.Contains("THREW") || line.Contains("NO COMMAND") ||
                           line.Contains("MOVED") || line.Contains("changed nothing") || line.StartsWith("FAIL") ||
                           line.StartsWith("house healthy:") && !line.Contains("yes"))
            .ToList();
        Assert.True(failures.Count == 0, string.Join("\n", failures));



    }
}
