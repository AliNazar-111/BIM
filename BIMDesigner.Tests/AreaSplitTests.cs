using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// Area plans split by hand: area boundaries and the areas placed between them, each measured as
/// its plan measures the storey; and office space let by its rentable area - BOMA 2017's Methods A
/// and B, and IPMS's figures and component areas - from the uses its areas are put to.
/// </summary>
public class AreaSplitTests
{
    private static WallType Type(BimDocument document, string name) => document.TypesOf<WallType>().First(type => type.Name.StartsWith(name, StringComparison.Ordinal));

    /// <summary>A storey of four outside walls round a box, centrelines from the origin.</summary>
    private static double Box(BimDocument document, Guid level, double width, double depth)
    {
        var type = Type(document, "Exterior - Brick on Block");
        var corners = new[] { new Point2D(0, 0), new Point2D(width, 0), new Point2D(width, depth), new Point2D(0, depth) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = type.Id, LevelId = level, UnconnectedHeight = 2700 });
        return type.Width;
    }

    private static AreaBoundary Boundary(BimDocument document, Guid level, AreaScheme scheme, Point2D from, Point2D to)
    {
        var line = new AreaBoundary { LevelId = level, Scheme = scheme, Start = from, End = to };
        document.Add(line);
        return line;
    }

    private static Area Area(BimDocument document, Guid level, AreaScheme scheme, double x, double y, string name, AreaUse use = AreaUse.None)
    {
        var area = new Area { LevelId = level, Scheme = scheme, Location = new Point2D(x, y), Name = name, Number = Areas.NextNumber(document, level, scheme), Use = use };
        document.Add(area);
        return area;
    }

    [Fact]
    public void AGrossInternalPlanSplitByABoundaryMeasuresEachSideToTheInsideFacesAndTheLine()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var w = Box(document, level, 12000, 8000);
        Boundary(document, level, AreaScheme.GrossInternal, new Point2D(5000, -1000), new Point2D(5000, 9000));
        var west = Area(document, level, AreaScheme.GrossInternal, 2000, 4000, "Flat 1");
        var east = Area(document, level, AreaScheme.GrossInternal, 9000, 4000, "Flat 2");

        Assert.Equal((5000 - w / 2) * (8000 - w), Areas.Shape(document, west).Area, 0);
        Assert.Equal((7000 - w / 2) * (8000 - w), Areas.Shape(document, east).Area, 0);
        Assert.Equal(AreaPlans.Measure(document, level, AreaScheme.GrossInternal).Area, Areas.Shape(document, west).Area + Areas.Shape(document, east).Area, 0);

        // Drawn on the GIA plan, the boundary splits nothing on the GEA plan: an area there is the whole storey.
        var gross = Area(document, level, AreaScheme.GrossExternal, 2000, 4000, "Whole");
        Assert.Equal((12000 + w) * (8000 + w), Areas.Shape(document, gross).Area, 0);
        Assert.Equal("2", Areas.NextNumber(document, level, AreaScheme.GrossExternal));
    }

    [Fact]
    public void AnAreaOutsideTheStoreyOrSharingASpaceSaysSoAndCountsNothing()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        Box(document, level, 10000, 6000);
        var first = Area(document, level, AreaScheme.GrossInternal, 3000, 3000, "Office");
        var second = Area(document, level, AreaScheme.GrossInternal, 7000, 3000, "Office again");
        var outside = Area(document, level, AreaScheme.GrossInternal, 15000, 3000, "Garden");

        Assert.Null(Areas.Shape(document, first).Shared);
        Assert.Contains("Shares its space with Office (1)", Areas.Shape(document, second).Shared);
        Assert.Equal(0, Areas.Shape(document, second).Area);
        Assert.Null(Areas.Shape(document, outside).Polygon);

        var warnings = Warnings.Of(document).Select(warning => warning.Heading).ToList();
        Assert.Contains("Areas in the same space", warnings);
        Assert.Contains("Areas not enclosed", warnings);
    }

    [Fact]
    public void AreasAndTheirBoundariesAreKeptWithTheProjectAndCopied()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        Box(document, level, 10000, 6000);
        Boundary(document, level, AreaScheme.Rentable, new Point2D(4000, 0), new Point2D(4000, 6000));
        Area(document, level, AreaScheme.Rentable, 2000, 3000, "Acme Ltd", AreaUse.Office);

        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var area = Assert.Single(reopened.All<Area>());
        Assert.Equal(("Acme Ltd", "1", AreaUse.Office, AreaScheme.Rentable), (area.Name, area.Number, area.Use, area.Scheme));
        var line = Assert.Single(reopened.All<AreaBoundary>());
        Assert.Equal((AreaScheme.Rentable, 6000.0), (line.Scheme, line.Length));

        var copy = Assert.IsType<Area>(ElementCopy.Clone(area));
        Assert.Equal((area.Name, area.Use, area.Scheme, area.Location), (copy.Name, copy.Use, copy.Scheme, copy.Location));
    }

    /// <summary>
    /// Two storeys of a 20 by 12 m office, each split into two tenancies either side of a corridor,
    /// with toilets and a stair - the ground floor also a lobby - its boundaries on the walls' middles.
    /// </summary>
    private static (BimDocument Document, Guid Ground, Guid First) Office()
    {
        var document = BimDocument.CreateDefault();
        var ground = document.Levels[0].Id;
        var first = document.Levels[1].Id;
        foreach (var level in new[] { ground, first })
        {
            Box(document, level, 20000, 12000);
            var s = AreaScheme.Rentable;
            Boundary(document, level, s, new Point2D(0, 5000), new Point2D(20000, 5000));
            Boundary(document, level, s, new Point2D(0, 7000), new Point2D(20000, 7000));
            Boundary(document, level, s, new Point2D(10000, 0), new Point2D(10000, 5000));
            Boundary(document, level, s, new Point2D(16000, 7000), new Point2D(16000, 12000));
            Boundary(document, level, s, new Point2D(18000, 7000), new Point2D(18000, 12000));
            Area(document, level, s, 5000, 2500, "Tenant A", AreaUse.Office);
            Area(document, level, s, 15000, 2500, level == ground ? "Lobby" : "Tenant B", level == ground ? AreaUse.Lobby : AreaUse.Office);
            Area(document, level, s, 10000, 6000, "Corridor", AreaUse.Corridor);
            Area(document, level, s, 8000, 9500, "Tenant C", AreaUse.Office);
            Area(document, level, s, 17000, 9500, "Toilets", AreaUse.Toilets);
            Area(document, level, s, 19000, 9500, "Stair", AreaUse.Stair);
        }

        return (document, ground, first);
    }

    [Fact]
    public void BomaMethodAAllocatesEachFloorsServiceThenTheBuildingsAndEveryAreaIsLetOnce()
    {
        var (document, ground, first) = Office();
        var floors = RentableAreas.Floors(document);
        Assert.Equal(2, floors.Count);
        var (g, f) = (floors[0], floors[1]);

        // The stair is rentable where it starts, at the ground floor, and left out where it passes through above.
        Assert.Equal(0, g.Penetrations);
        Assert.True(f.Penetrations > 0);
        Assert.Equal(g.FloorRentable - f.Penetrations, f.FloorRentable, 0);

        // Floor service is what is left once the usable and the building's service are taken out.
        Assert.True(g.BuildingService > 0);
        Assert.Equal(g.FloorRentable - g.Usable - g.BuildingService, g.FloorService, 0);
        Assert.Equal((g.Usable + g.FloorService) / g.Usable, g.AllocationRatio!.Value, 9);

        // Every square metre of floor rentable area is let to someone, by either method.
        var shares = RentableAreas.Shares(document);
        Assert.Equal(5, shares.Count);
        var rentable = floors.Sum(floor => floor.FloorRentable);
        Assert.Equal(rentable, shares.Sum(share => share.RentableA), 0);
        Assert.Equal(rentable, shares.Sum(share => share.RentableB), 0);

        // Method B is one load factor for all; Method A's differ by floor, the ground floor's lobby falling on all.
        Assert.Equal(rentable / shares.Sum(share => share.Occupant), RentableAreas.LoadFactorB(document)!.Value, 9);
        Assert.All(shares, share => Assert.Equal(RentableAreas.LoadFactorB(document)!.Value, share.LoadFactorB, 9));
        Assert.True(RentableAreas.BuildingRatio(document) > 1);
        Assert.All(shares, share => Assert.True(share.LoadFactorA > 1));
    }

    [Fact]
    public void IpmsComponentAreasAddUpToIpms1AndEachTenancyIsItsIpms32()
    {
        var (document, ground, _) = Office();
        foreach (var floor in RentableAreas.Ipms(document))
        {
            Assert.Equal(floor.Ipms1, floor.Components.Values.Sum(), 0);
            Assert.Equal(floor.Ipms1 - floor.Ipms2, floor.Components['A'], 0);
            Assert.True(floor.Components['B'] > 0 && floor.Components['D'] > 0 && floor.Components['E'] > 0 && floor.Components['F'] > 0);
        }

        var ground1 = RentableAreas.Ipms(document)[0];
        Assert.Equal(AreaPlans.Measure(document, ground, AreaScheme.GrossExternal).Area, ground1.Ipms1, 0);
        Assert.Equal(Areas.On(document, ground, AreaScheme.Rentable).Where(area => area.Use == AreaUse.Office).Sum(area => Areas.Shape(document, area).Area), ground1.Exclusive, 0);
    }

    [Fact]
    public void TheRentableAndIpmsSchedulesSayTheirFiguresAndTheCodes()
    {
        var (document, _, _) = Office();
        var rentable = AreaPlans.Table(document, AreaPlans.Schedule(AreaScheme.Rentable));
        Assert.Equal("Rentable Areas - BOMA 2017 Office", rentable.Title);
        Assert.Contains(rentable.Lines, line => line.Cells[0].Contains("Tenant A"));
        Assert.Contains(rentable.Lines, line => line.Cells[0].Contains("Method A building allocation ratio"));
        Assert.Contains(rentable.Lines, line => line.Cells[0].Contains("ANSI/BOMA Z65.1-2017"));

        var ipms = AreaPlans.Table(document, AreaPlans.IpmsSchedule());
        Assert.Equal("Component Areas - IPMS", ipms.Title);
        Assert.Equal(12, ipms.ColumnWidths.Count);
        Assert.Contains(ipms.Lines, line => line.Cells[0].Contains("IPMS: All Buildings"));

        // A GIA plan's schedule lists its areas under their storey.
        var level = document.Levels[0].Id;
        Area(document, level, AreaScheme.GrossInternal, 5000, 6000, "Whole floor");
        Assert.Contains(AreaPlans.Table(document, AreaPlans.Schedule(AreaScheme.GrossInternal)).Lines, line => line.Cells[0].Trim() == "Whole floor (1)");
    }
}
