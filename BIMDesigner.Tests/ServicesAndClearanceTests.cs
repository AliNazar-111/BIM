using System.Reflection;
using System.Windows;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.ElectricalDomain;
using Xbim.Ifc4.HvacDomain;
using Xbim.Ifc4.Interfaces;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;
using PartShape = BIMDesigner.Core.Views.ComponentModel.PartShape;

namespace BIMDesigner.Tests;

/// <summary>
/// The services - radiators, ceiling lights, sockets and switches - the outside - cars, people,
/// trees - and the clear space a WC, basin, bath, shower, bed or kitchen needs, as Approved
/// Document M (M4(2)) and the ADA ask, with what stands in it.
/// </summary>
[Collection("Wpf")]
public class ServicesAndClearanceTests
{
    private static ComponentType Type(BimDocument document, string name) =>
        document.TypesOf<ComponentType>().Single(type => type.Name == name);

    /// <summary>A room 4 m by 3 m, its back wall along y = 0 and a wall at either end.</summary>
    private static (BimDocument Document, Wall Back) Room()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var wallType = document.TypesOf<WallType>().First();
        Wall Wall(Point2D start, Point2D end)
        {
            var wall = new Wall { Start = start, End = end, TypeId = wallType.Id, LevelId = level, UnconnectedHeight = 2400 };
            document.Add(wall);
            return wall;
        }

        var back = Wall(new Point2D(0, 0), new Point2D(4000, 0));
        Wall(new Point2D(0, 0), new Point2D(0, 3000));
        Wall(new Point2D(4000, 0), new Point2D(4000, 3000));
        return (document, back);
    }

    private static Component Put(BimDocument document, string name, double x, double y, double rotation = 0, bool fit = true)
    {
        var type = Type(document, name);
        var piece = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, y), Rotation = rotation };
        if (fit) ComponentPlacing.FitToWalls(document, piece);
        document.Add(piece);
        return piece;
    }

    [Fact]
    public void TheServicesAreInTheLibraryAtTheHeightsTheyGoAt()
    {
        var document = BimDocument.CreateDefault();

        // A radiator on the wall, its panel 150 off the floor over its valves.
        var radiator = Type(document, "Radiator - 600 x 1000");
        Assert.Equal((BuiltInCategory.MechanicalEquipment, ComponentPlacement.FaceBased), (radiator.Kind, radiator.Placement));
        Assert.Equal(150, radiator.DefaultElevation + ComponentModel.Parts(radiator).Where(part => part.Finish == PartFinish.Body).Min(part => part.Bottom));
        Assert.Equal(2, ComponentModel.Parts(radiator).Count(part => part.Finish == PartFinish.Metal && part.Shape == PartShape.Upright));

        // Sockets and switches between 450 and 1200 off the floor, as Approved Document M sets out.
        foreach (var name in new[] { "Socket - Double", "Switch - One Gang" })
        {
            var fitting = Type(document, name);
            Assert.Equal(ComponentPlacement.FaceBased, fitting.Placement);
            Assert.InRange(fitting.DefaultElevation, 450, 1200 - fitting.Height);
        }

        Assert.Equal(BuiltInCategory.ElectricalFixtures, Type(document, "Socket - Double").Kind);
        Assert.Equal(BuiltInCategory.LightingDevices, Type(document, "Switch - One Gang").Kind);

        // Ceiling lights with their tops at 2400 where there is no ceiling to hang from.
        foreach (var name in new[] { "Ceiling Light - Flush", "Ceiling Light - Pendant" })
        {
            var light = Type(document, name);
            Assert.Equal(2400, light.DefaultElevation + light.Height);
            Assert.True(ComponentModel.HangsFromCeiling(light));
        }
    }

    [Fact]
    public void ACeilingLightPutDownHangsFromTheCeilingOverItAndNeverFromAWall()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, back) = Room();
                var level = document.Levels[0].Id;
                var ceiling = new Ceiling { LevelId = level, TypeId = document.TypesOf<CeilingType>().First().Id, HeightOffset = 2600 };
                ceiling.SetBoundary(new[] { new Point2D(0, 0), new Point2D(2000, 0), new Point2D(2000, 3000), new Point2D(0, 3000) });
                document.Add(ceiling);

                var light = Type(document, "Ceiling Light - Pendant");
                var plan = new PlanView { Document = document, ActiveLevelId = level, ActiveComponentTypeId = light.Id };
                var place = typeof(PlanView).GetMethod("PlaceComponent", BindingFlags.Instance | BindingFlags.NonPublic)!;

                // Under the ceiling, and right by the back wall: on the ceiling, not the wall.
                place.Invoke(plan, new object[] { new Point2D(1000, 150) });
                var hung = document.Elements.OfType<Component>().Last();
                Assert.Equal(ceiling.Id, hung.HostId);
                Assert.Equal(ceiling.GetBottomElevation(document), ComponentHosting.BaseElevation(document, hung, light) + light.Height, 6);

                // With no ceiling over it, its top at 2400.
                place.Invoke(plan, new object[] { new Point2D(3000, 1500) });
                var free = document.Elements.OfType<Component>().Last();
                Assert.False(free.IsHosted);
                Assert.Equal(2400, ComponentHosting.BaseElevation(document, free, light) + light.Height, 6);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void CarsPeopleAndTreesAreShapedAsTheyAre()
    {
        var document = BimDocument.CreateDefault();

        var car = Type(document, "Car - Hatchback");
        Assert.Equal(BuiltInCategory.Entourage, car.Kind);
        Assert.Equal(4, ComponentModel.Parts(car).Count(part => part.Finish == PartFinish.Dark && part.Shape == PartShape.Facing));

        var person = Type(document, "Person - Adult");
        Assert.Equal(1750, ComponentModel.Parts(person).Max(part => part.Top));
        Assert.Contains(ComponentModel.Parts(person), part => part.Finish == PartFinish.Skin && part.Top == 1750);

        // Trees round from above, with a trunk the colour of bark.
        foreach (var name in new[] { "Tree - Large", "Conifer - 7 m", "Shrub - 1200" })
        {
            var tree = Type(document, name);
            Assert.Equal(BuiltInCategory.Planting, tree.Kind);
            var outline = ComponentModel.PlanOutlines(tree, new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0))[0];
            Assert.All(outline, point => Assert.Equal(tree.Width / 2, Math.Sqrt(point.X * point.X + point.Y * point.Y), 1));
        }

        Assert.Contains(ComponentModel.Parts(Type(document, "Conifer - 7 m")), part => part.Finish == PartFinish.Bark);
    }

    [Fact]
    public void AWcHasItsZoneInFrontAndABasinPutInItBlocksIt()
    {
        var (document, _) = Room();
        var wc = Put(document, "WC - Close Coupled", 2000, 400);
        var type = Type(document, "WC - Close Coupled");

        var (zone, inTheWay) = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM));
        Assert.Empty(inTheWay);

        // 1100 deep from the front of the WC, from 350 on one side of its centreline to 1000 on the other.
        var front = wc.Location.Y + type.Depth / 2;
        Assert.Equal(1350, zone.Outline.Max(p => p.X) - zone.Outline.Min(p => p.X), 6);
        Assert.Equal(front, zone.Outline.Min(p => p.Y), 6);
        Assert.Equal(front + 1100, zone.Outline.Max(p => p.Y), 6);

        // A basin beside it, coming into the zone by no more than 300, is let be.
        var basin = Put(document, "Basin - Pedestal", 2000, 1200, fit: false);
        var edge = zone.Outline.Max(p => p.X);
        basin.Location = new Point2D(edge + 275 - 250, front + 500);
        Assert.Empty(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM).Single().In);

        // Put in the middle of it, it blocks it.
        basin.Location = new Point2D(2000, 1200);
        var blocked = Assert.Single(Clearances.Blocked(document, document.Levels[0].Id, ClearanceStandard.ApprovedDocumentM),
            item => item.Zone.Component == wc);
        Assert.Equal("Basin - Pedestal", blocked.By);
        Assert.Contains("from 350 on its wall side to 1000 on its open side", blocked.Message);
    }

    [Fact]
    public void ForAWheelchairUserTheWcsZoneRunsOut2200FromTheWallBehindIt()
    {
        var (document, _) = Room();
        var wc = Put(document, "WC - Close Coupled", 450, 400);

        // Beside the wall at x = 0: 350 toward it, 1200 away from it, 2200 out from the wall behind.
        var (zone, inTheWay) = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.ApprovedDocumentM3));
        Assert.Empty(inTheWay);
        Assert.Equal(wc.Location.X - 350, zone.Outline.Min(p => p.X), 0);
        Assert.Equal(wc.Location.X + 1200, zone.Outline.Max(p => p.X), 0);
        var behind = wc.Location.Y - Type(document, "WC - Close Coupled").Depth / 2;
        Assert.Equal(behind + 2200, zone.Outline.Max(p => p.Y), 0);

        // Under the IRC: 15 in either side of its centreline and 21 in in front.
        var (irc, _) = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.Irc));
        Assert.Equal(762, irc.Outline.Max(p => p.X) - irc.Outline.Min(p => p.X), 6);
        Assert.Contains("R307.1", irc.Rule);
    }

    [Fact]
    public void UnderTheAdaAWcsClearanceRunsFromTheSideWallItIsBeside()
    {
        var (document, _) = Room();
        var wc = Put(document, "WC - Close Coupled", 450, 400);

        var (zone, inTheWay) = Assert.Single(Clearances.Of(document, wc, ClearanceStandard.Ada));
        Assert.Empty(inTheWay);

        // 1525 wide, out from the face of the wall at x = 0 into the room, and 1420 deep from the wall behind.
        var face = document.TypesOf<WallType>().First().Structure.TotalWidth / 2;
        Assert.Equal(1525, zone.Outline.Max(p => p.X) - zone.Outline.Min(p => p.X), 6);
        Assert.Equal(face, zone.Outline.Min(p => p.X), 0);
        Assert.Equal(1420, zone.Outline.Max(p => p.Y) - zone.Outline.Min(p => p.Y), 6);
    }

    [Fact]
    public void ADoubleBedWantsBothSidesAndItsFootAndASingleOneSide()
    {
        var (document, _) = Room();
        var level = document.Levels[0].Id;

        // A double against the back wall, in the middle: both sides and the foot clear.
        var bed = Put(document, "Bed - King 1500 x 2000", 2000, 500);
        var zones = Clearances.Of(document, bed, ClearanceStandard.ApprovedDocumentM);
        Assert.Equal(3, zones.Count);
        Assert.All(zones, zone => Assert.Empty(zone.In));

        // Pushed into the corner its left side is in the wall: blocked, as M4(2) asks of a principal bedroom.
        document.Remove(bed);
        var cornered = Put(document, "Bed - King 1500 x 2000", 900, 500);
        Assert.Contains(Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM), item => item.Zone.Component == cornered && item.By == "the wall");

        // A single in the corner needs one side only, and takes the open one.
        document.Remove(cornered);
        var single = Put(document, "Bed - Single 900 x 1900", 1000, 500);
        var side = Assert.Single(Clearances.Of(document, single, ClearanceStandard.ApprovedDocumentM));
        Assert.Empty(side.In);
        Assert.True(side.Zone.Outline.Min(p => p.X) > single.Location.X);
    }

    [Fact]
    public void AKitchenRunDoesNotBlockItselfButAnIslandTooCloseDoes()
    {
        var (document, _) = Room();
        var level = document.Levels[0].Id;
        foreach (var x in new[] { 500.0, 1100, 1700 }) Put(document, "Base Unit - 600", x, 400);
        Put(document, "Sink Unit - 1000", 2500, 400);
        Assert.Empty(Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM));

        // An island 1100 in front of the run: too close for Approved Document M's 1200, enough for the ADA's 1015.
        var island = Type(document, "Counter - Kitchen 1800");
        Put(document, "Counter - Kitchen 1800", 1500, 600 + 1100 + island.Depth / 2, rotation: 180, fit: false);
        Assert.Contains(Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM), item => item.By.Contains("Counter - Kitchen 1800"));
        Assert.DoesNotContain(Clearances.Blocked(document, level, ClearanceStandard.Ada), item => item.By.Contains("Counter - Kitchen 1800"));

        // A wall light over a zone is not in the way: it is off the floor.
        var light = Type(document, "Wall Light");
        var high = new Component { TypeId = light.Id, TypeKind = light.Kind, LevelId = level, Location = new Point2D(500, 1200), Elevation = 2000 };
        document.Add(high);
        Assert.DoesNotContain(Clearances.Blocked(document, level, ClearanceStandard.ApprovedDocumentM), item => item.By.Contains("Wall Light"));
    }

    [Fact]
    public void TheStandardIsSavedWithTheProject()
    {
        var document = BimDocument.CreateDefault();
        Assert.Equal(ClearanceStandard.ApprovedDocumentM, document.ProjectInformation.ClearanceStandard);

        document.ProjectInformation.ClearanceStandard = ClearanceStandard.Ada;
        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(ClearanceStandard.Ada, loaded.ProjectInformation.ClearanceStandard);
    }

    [Fact]
    public void RadiatorsSocketsAndSwitchesAreExportedAsWhatTheyAre()
    {
        var (document, back) = Room();
        foreach (var (name, x) in new[] { ("Radiator - 600 x 1000", 1000.0), ("Socket - Double", 2500.0), ("Switch - One Gang", 3000.0) })
        {
            var type = Type(document, name);
            var fitting = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id };
            Assert.True(fitting.HostOn(document, back, new Point2D(x, 100)));
            fitting.Elevation = type.DefaultElevation;
            document.Add(fitting);
        }

        using var model = IfcExport.Build(document);
        Assert.Single(model.Instances.OfType<IfcSpaceHeater>(), item => item.PredefinedType == IfcSpaceHeaterTypeEnum.RADIATOR);
        Assert.Single(model.Instances.OfType<IfcOutlet>(), item => item.PredefinedType == IfcOutletTypeEnum.POWEROUTLET);
        Assert.Single(model.Instances.OfType<IfcSwitchingDevice>(), item => item.PredefinedType == IfcSwitchingDeviceTypeEnum.TOGGLESWITCH);
    }
}
