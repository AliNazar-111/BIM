using BIMDesigner.Core;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Parameters;

namespace BIMDesigner.Tests;

public class ParameterTests
{
    private static (BimDocument Document, Wall Wall, WallType Type) Scenario()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First();
        var wall = new Wall
        {
            Start = new Point2D(0, 0),
            End = new Point2D(5000, 0),
            TypeId = type.Id,
            LevelId = document.Levels.First().Id
        };
        document.Add(wall);
        return (document, wall, type);
    }

    private static ParameterValue Parameter(IEnumerable<ParameterValue> parameters, string name) =>
        parameters.Single(parameter => parameter.Name == name);

    [Fact]
    public void InstanceParameters_ReadThroughToTheElement()
    {
        var (document, wall, _) = Scenario();
        wall.UnconnectedHeight = 2700;

        var height = Parameter(wall.GetInstanceParameters(document), "Unconnected Height");

        Assert.Equal(2700d, height.Value);
        Assert.Equal("2.70 m", height.DisplayValue);
    }

    [Fact]
    public void InstanceParameters_WriteThroughToTheElement()
    {
        var (document, wall, _) = Scenario();

        var height = Parameter(wall.GetInstanceParameters(document), "Unconnected Height");
        Assert.True(height.TrySetFromText("3.20 m"));

        Assert.Equal(3200, wall.UnconnectedHeight, precision: 6);
    }

    [Fact]
    public void LengthParameters_AcceptMillimetresMetresAndBareNumbers()
    {
        var (document, wall, _) = Scenario();

        foreach (var (text, expected) in new[] { ("2400", 2400d), ("2400 mm", 2400d), ("2.4 m", 2400d), ("240cm", 2400d) })
        {
            var height = Parameter(wall.GetInstanceParameters(document), "Unconnected Height");
            Assert.True(height.TrySetFromText(text), $"'{text}' should parse");
            Assert.Equal(expected, wall.UnconnectedHeight, precision: 6);

            wall.UnconnectedHeight = 3000;
        }
    }

    [Fact]
    public void BadInput_LeavesTheModelUnchanged()
    {
        var (document, wall, _) = Scenario();
        wall.UnconnectedHeight = 3000;

        var height = Parameter(wall.GetInstanceParameters(document), "Unconnected Height");

        Assert.False(height.TrySetFromText("not a number"));
        Assert.False(height.TrySetFromText("-500"));   // rejected by the wall's own guard
        Assert.Equal(3000, wall.UnconnectedHeight, precision: 6);
    }

    [Fact]
    public void ComputedParameters_AreReadOnly()
    {
        var (document, wall, _) = Scenario();

        foreach (var name in new[] { "Area", "Volume" })
        {
            var parameter = Parameter(wall.GetInstanceParameters(document), name);
            Assert.True(parameter.IsReadOnly, $"{name} must be computed, not stored");
            Assert.False(parameter.TrySet(1234d));
        }

        // Length and height are computed too, never stored - but typing one moves the wall's
        // end or top to make it so, as Revit's do.
        foreach (var name in new[] { "Length", "Height" })
        {
            var parameter = Parameter(wall.GetInstanceParameters(document), name);
            Assert.False(parameter.IsReadOnly, $"{name} should be typed into, as in Revit");
            Assert.True(parameter.TrySet(1234d));
            Assert.Equal(1234d, (double)parameter.Value!, precision: 6);
        }
    }

    [Fact]
    public void ChoiceParameters_RejectValuesOutsideTheAllowedSet()
    {
        var (document, wall, _) = Scenario();
        var locationLine = Parameter(wall.GetInstanceParameters(document), "Location Line");

        Assert.NotNull(locationLine.AllowedValues);
        Assert.Contains("Core Face Exterior", locationLine.AllowedValues!);

        Assert.True(locationLine.TrySet("Core Face Exterior"));
        Assert.Equal(WallLocationLine.CoreFaceExterior, wall.LocationLine);

        Assert.False(locationLine.TrySet("Somewhere Else"));
        Assert.Equal(WallLocationLine.CoreFaceExterior, wall.LocationLine);
    }

    [Fact]
    public void BaseConstraint_OffersTheProjectLevels()
    {
        var (document, wall, _) = Scenario();
        var baseConstraint = Parameter(wall.GetInstanceParameters(document), "Base Constraint");

        Assert.Equal(new[] { "Ground Floor", "First Floor" }, baseConstraint.AllowedValues);

        Assert.True(baseConstraint.TrySet("First Floor"));
        Assert.Equal(document.Levels.Single(l => l.Name == "First Floor").Id, wall.LevelId);
    }

    [Fact]
    public void TypeParameters_AreSharedByEveryInstanceOfTheType()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First();
        var level = document.Levels.First();

        var first = new Wall { Start = new Point2D(0, 0), End = new Point2D(3000, 0), TypeId = type.Id, LevelId = level.Id };
        var second = new Wall { Start = new Point2D(0, 2000), End = new Point2D(3000, 2000), TypeId = type.Id, LevelId = level.Id };
        document.Add(first);
        document.Add(second);

        var fireRating = Parameter(type.GetTypeParameters(), "Fire Rating");
        Assert.True(fireRating.TrySet("240 min"));

        // One edit, both walls - that is the whole point of the type/instance split.
        Assert.Equal("240 min", document.GetWallType(first)!.FireRating);
        Assert.Equal("240 min", document.GetWallType(second)!.FireRating);
    }

    [Fact]
    public void InstanceParameters_AreIndependentBetweenWallsOfTheSameType()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().First();
        var level = document.Levels.First();

        var first = new Wall { Start = new Point2D(0, 0), End = new Point2D(3000, 0), TypeId = type.Id, LevelId = level.Id };
        var second = new Wall { Start = new Point2D(0, 2000), End = new Point2D(3000, 2000), TypeId = type.Id, LevelId = level.Id };
        document.Add(first);
        document.Add(second);

        Parameter(first.GetInstanceParameters(document), "Unconnected Height").TrySetFromText("4.00 m");

        Assert.Equal(4000, first.UnconnectedHeight, precision: 6);
        Assert.Equal(3000, second.UnconnectedHeight, precision: 6);
    }

    [Fact]
    public void TypeWidth_TracksTheAssembly()
    {
        var document = BimDocument.CreateDefault();
        var type = document.TypesOf<WallType>().Single(t => t.Name.Contains("Brick on Block"));

        var width = Parameter(type.GetTypeParameters(), "Width");

        Assert.True(width.IsReadOnly);
        Assert.Equal(type.Structure.TotalWidth, width.Value);
        Assert.Equal(330d, width.Value);
    }
}

public class UnitsTests
{
    [Theory]
    [InlineData(5250, "5.25 m")]
    [InlineData(3000, "3.00 m")]
    [InlineData(200, "200 mm")]
    [InlineData(12.5, "12.5 mm")]
    public void FormatLength_SwitchesToMetresAtOneMetre(double mm, string expected)
    {
        Assert.Equal(expected, Units.FormatLength(mm));
    }

    [Fact]
    public void FormatArea_ReportsSquareMetres()
    {
        Assert.Equal("15.75 m²", Units.FormatArea(5250d * 3000));
    }

    [Fact]
    public void FormatVolume_ReportsCubicMetres()
    {
        Assert.Equal("3.150 m³", Units.FormatVolume(5250d * 3000 * 200));
    }

    [Theory]
    [InlineData(1249, 100, 1200)]
    [InlineData(1251, 100, 1300)]
    [InlineData(1251, 0, 1251)]
    public void SnapToGrid_RoundsToNearestStep(double value, double step, double expected)
    {
        Assert.Equal(expected, Units.SnapToGrid(value, step), precision: 6);
    }

    [Theory]
    [InlineData("CoreFaceExterior", "Core Face Exterior")]
    [InlineData("NonBearing", "Non Bearing")]
    [InlineData("Walls", "Walls")]
    public void EnumText_RoundTripsThroughDisplayText(string member, string display)
    {
        Assert.Equal(display, EnumText.Humanise(member));
        Assert.Equal(member.Replace(" ", ""), display.Replace(" ", ""));
    }
}
