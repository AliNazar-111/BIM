using BIMDesigner.Core;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Parameters;
using BIMDesigner.Infrastructure.Serialization;

namespace BIMDesigner.Tests;

/// <summary>
/// A US project shown and typed in feet and inches: lengths to the nearest sixteenth as a US
/// drawing writes them, areas in square feet, and whatever is typed - 12'-6 1/2", 12' 6", 150",
/// a bare number of feet, or metres with their unit - read back; the project keeps its units in
/// its file, and one written before keeps feet and inches if it was a US one.
/// </summary>
public class FeetAndInchesTests
{
    [Theory]
    [InlineData(3810, "12'-6\"")]
    [InlineData(3822.7, "12'-6 1/2\"")]
    [InlineData(3657.6, "12'-0\"")]
    [InlineData(152.4, "6\"")]
    [InlineData(165.1, "6 1/2\"")]
    [InlineData(6.35, "1/4\"")]
    [InlineData(304.8 + 3.175, "1'-0 1/8\"")]
    [InlineData(-457.2, "-1'-6\"")]
    [InlineData(0, "0\"")]
    public void LengthsAreWrittenAsAUsDrawingWritesThem(double mm, string written) =>
        Assert.Equal(written, Units.FeetAndInches(mm));

    [Theory]
    [InlineData("12'-6 1/2\"", 3822.7)]
    [InlineData("12' 6\"", 3810)]
    [InlineData("12'6\"", 3810)]
    [InlineData("12'", 3657.6)]
    [InlineData("12.5'", 3810)]
    [InlineData("150\"", 3810)]
    [InlineData("6 1/2\"", 165.1)]
    [InlineData("1/2\"", 12.7)]
    [InlineData("12 ft 6 in", 3810)]
    [InlineData("-1'-6\"", -457.2)]
    [InlineData("12", 3657.6)]
    [InlineData("3.81 m", 3810)]
    [InlineData("381 cm", 3810)]
    [InlineData("3810 mm", 3810)]
    public void WhatIsTypedInAUsProjectIsRead(string typed, double mm)
    {
        using var units = Units.Use(UnitSystem.Imperial);
        Assert.True(Units.TryParseLength(typed, out var read), typed);
        Assert.Equal(mm, read, 3);
        Assert.True(ParameterFormatter.TryParse(ParameterDataType.Length, typed, out var value), typed);
        Assert.Equal(mm, (double)value!, 3);
    }

    [Fact]
    public void AMetricProjectReadsABareNumberAsMillimetresAndStillTakesFeetAndInches()
    {
        using var units = Units.Use(UnitSystem.Metric);
        Assert.True(Units.TryParseLength("2400", out var bare));
        Assert.Equal(2400, bare, 6);
        Assert.True(Units.TryParseLength("8'", out var feet));
        Assert.Equal(2438.4, feet, 3);
        Assert.Equal("5.25 m", Units.FormatLength(5250));
        Assert.False(Units.TryParseLength("twelve feet", out _));
    }

    [Fact]
    public void AreasAndVolumesAreInSquareAndCubicFeet()
    {
        using var units = Units.Use(UnitSystem.Imperial);
        Assert.Equal("100 sq ft", Units.FormatArea(100 * Units.SquareMillimetresPerSquareFoot));
        Assert.Equal("1,250.5 sq ft", Units.FormatArea(1250.5 * Units.SquareMillimetresPerSquareFoot));
        Assert.Equal("27 cu ft", Units.FormatVolume(27 * Units.CubicMillimetresPerCubicFoot));
        Assert.Equal("12'-6\"", ParameterFormatter.Format(ParameterDataType.Length, 3810.0));

        Assert.True(Units.TryParseArea("1200 sq ft", out var area));
        Assert.Equal(1200 * Units.SquareMillimetresPerSquareFoot, area, 0);
        Assert.True(Units.TryParseArea("12 m²", out var metric));
        Assert.Equal(12e6, metric, 0);
        Assert.True(Units.TryParseVolume("1 cu yd", out var yard));
        Assert.Equal(27 * Units.CubicMillimetresPerCubicFoot, yard, 0);
    }

    [Fact]
    public void MessagesKeepTheirMetricWordsAndAreInFeetAndInchesInAUsProject()
    {
        using (Units.Use(UnitSystem.Metric))
        {
            Assert.Equal("2.40 m", Units.Metres(2400, "0.00"));
            Assert.Equal("15 m", Units.Metres(15000, "0"));
            Assert.Equal("1100 mm", Units.Millimetres(1100, "0"));
            Assert.Equal("1100 mm", Units.FormatMm(1100));
            Assert.Equal("65.2 m²", Units.SquareMetres(65.2e6, "0.0"));
            Assert.Equal("4.5 m²", Units.OfSquareMetres(4.5, "0.0"));
            Assert.Equal("16.9 m³", Units.CubicMetres(16.9e9, "0.0"));
            Assert.Equal("150", Units.Bare(150));
            Assert.Equal("187.5", Units.Bare(187.5, "0.#"));
            Assert.Equal("196 (7¾ in)", Units.Figure("196", "7¾ in"));
            Assert.Equal(" (152)", Units.MetricAside("152"));
            Assert.Equal("-0.450", Units.Elevation(-450));
            Assert.Equal("+0.000", Units.Elevation(0));
            Assert.Equal("20 mm a metre", Units.FormatFall(20));
            Assert.True(Units.TryParseFall("12.5", out var flat));
            Assert.Equal(12.5, flat, 6);
        }

        using (Units.Use(UnitSystem.Imperial))
        {
            Assert.Equal("7'-10 1/2\"", Units.Metres(2400, "0.00"));
            Assert.Equal("50'-0\"", Units.Metres(15240, "0"));
            Assert.Equal("3'-7 5/16\"", Units.FormatMm(1100));
            Assert.Equal("100 sq ft", Units.SquareMetres(100 * Units.SquareMillimetresPerSquareFoot, "0.0"));
            Assert.Equal("27 cu ft", Units.CubicMetres(27 * Units.CubicMillimetresPerCubicFoot, "0.0"));
            Assert.Equal("6\"", Units.Bare(152.4));
            Assert.Equal("7¾ in", Units.Figure("196", "7¾ in"));
            Assert.Equal(string.Empty, Units.MetricAside("152"));

            // Levels as a US drawing writes them: feet and inches, signed, always with the feet.
            Assert.Equal("-0'-6\"", Units.Elevation(-152.4));
            Assert.Equal("+0'-0\"", Units.Elevation(0));
            Assert.Equal("+10'-0\"", Units.Elevation(3048));

            // A drain's fall in inches a foot: 1/4" for the usual 20 mm a metre, and typed back.
            Assert.Equal("1/4\" a foot", Units.FormatFall(20));
            Assert.True(Units.TryParseFall("1/8", out var eighth));
            Assert.Equal(1000.0 / 96, eighth, 3);
            Assert.True(Units.TryParseFall("1/4\"", out var quarter));
            Assert.Equal(1000.0 / 48, quarter, 3);
            Assert.False(Units.TryParseFall("steep", out _));
        }
    }

    [Fact]
    public void AUsProjectIsOfferedItsOwnFittingsAndAnEnglishOneItsOwn()
    {
        var us = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse });
        var england = ProjectTemplates.Create(new NewProjectSpec());
        Core.Architecture.ComponentType Named(BimDocument document, string name) => document.TypesOf<Core.Architecture.ComponentType>().Single(type => type.Name == name);

        // The US's wiring devices and water heater, at the heights its rules put them.
        var receptacle = Named(us, "Receptacle - Duplex 15 A");
        Assert.Equal(Core.Architecture.ComponentForm.Socket, receptacle.Form);
        Assert.Equal(381, receptacle.DefaultElevation, 1);
        Assert.Equal(Core.Architecture.ComponentForm.Switch, Named(us, "Switch - Single Pole").Form);
        var heater = Named(us, "Water Heater - Electric 50 gal");
        Assert.Equal(Core.Architecture.ComponentForm.HotWaterCylinder, heater.Form);
        Assert.Equal(4500, Core.Mep.Circuits.LoadOf(heater), 1);

        // Each country's fittings offered to its own projects only; one made by hand to both.
        Assert.True(ProjectTemplates.Fits(us, receptacle));
        Assert.False(ProjectTemplates.Fits(england, receptacle));
        Assert.False(ProjectTemplates.Fits(us, Named(us, "Shaver Socket")));
        Assert.True(ProjectTemplates.Fits(england, Named(england, "Consumer Unit - 12 Way")));
        Assert.False(ProjectTemplates.Fits(england, Named(england, "Load Center - 200 A 40 Space")));
        Assert.True(ProjectTemplates.Fits(us, Named(us, "Radiator - 600 x 1000")));
    }

    [Fact]
    public void AUsProjectIsInFeetAndInchesAndKeepsThemInItsFile()
    {
        var us = ProjectTemplates.Create(new NewProjectSpec { Template = ProjectTemplate.UsHouse });
        Assert.Equal(UnitSystem.Imperial, us.ProjectInformation.Units);
        Assert.Equal(UnitSystem.Metric, ProjectTemplates.Create(new NewProjectSpec()).ProjectInformation.Units);

        // Its dimensions start in feet and inches.
        var style = DimensionTypes.Starting(us)!;
        Assert.Equal(DimensionUnits.FeetAndInches, style.Units);
        Assert.Equal("12'-6\"", style.Format(3810));

        // Kept in its file; and a US project written before projects had units opens in feet and inches.
        Assert.Equal(UnitSystem.Imperial, ProjectFile.FromJson(ProjectFile.ToJson(us)).ProjectInformation.Units);
        var older = ProjectFile.ToJson(us).Replace("\"Units\":\"Imperial\",", string.Empty).Replace("\"Units\": \"Imperial\",", string.Empty);
        Assert.DoesNotContain("\"Units\"", older.Substring(0, Math.Min(older.Length, 2000)));
        Assert.Equal(UnitSystem.Imperial, ProjectFile.FromJson(older).ProjectInformation.Units);
    }
}
