using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BIMDesigner.Core.Annotation;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using BIMDesigner.UI.Controls;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.SharedBldgElements;
using static BIMDesigner.Tests.StairTests;

namespace BIMDesigner.Tests;

/// <summary>
/// A stair labelled and handed on: a tag reading what it is in a line, its risers numbered and
/// its flights dimensioned in plan, and in IFC a stair made up of its flights, landings,
/// stringers and railings, drawn by the line walked up it.
/// </summary>
[Collection("Wpf")]
public class StairAnnotationTests
{
    [Fact]
    public void ATagReadsWhatAStairIsInALine()
    {
        var (document, _) = House();
        var stair = Put(document, 3000, 600);
        Assert.Equal("ST1: 15 R @ 200, 14 T @ 225", Stairs.Summary(document, stair));

        var tag = new Tag { TargetId = stair.Id, Field = StairParameters.Summary.Name, LevelId = stair.LevelId };
        document.Add(tag);
        Assert.Equal("ST1: 15 R @ 200, 14 T @ 225", tag.Read(document));

        // A landing is a step, not a tread.
        stair.Shape = StairShape.QuarterTurn;
        Assert.Equal("ST1: 15 R @ 200, 13 T @ 225", tag.Read(document));
    }

    [Fact]
    public void ItsRisersAreNumberedAndItsFlightsDimensionedInPlanWhenItSaysSo()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var (document, _) = House();
                var stair = Put(document, 1500, 600, shape: StairShape.QuarterTurn, corner: StairCorner.Winders);
                (stair.ShowRiserNumbers, stair.ShowFlightDimensions) = (true, true);
                Put(document, 4500, 600, shape: StairShape.Spiral, width: 800).ShowRiserNumbers = true;

                var plan = new PlanView { Document = document, ActiveLevelId = document.Levels[0].Id };
                var host = new Border { Width = 600, Height = 500, Child = plan };
                host.Measure(new Size(600, 500));
                host.Arrange(new Rect(0, 0, 600, 500));
                plan.ZoomToFit();
                new RenderTargetBitmap(600, 500, 96, 96, PixelFormats.Pbgra32).Render(host);

                var back = ProjectFile.FromJson(ProjectFile.ToJson(document)).Elements.OfType<Stair>().Single(other => other.Shape == StairShape.QuarterTurn);
                Assert.Equal((true, true), (back.ShowRiserNumbers, back.ShowFlightDimensions));
                Assert.True(((Stair)Core.Documents.ElementCopy.Clone(stair)!).ShowFlightDimensions);
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [Fact]
    public void InIfcItIsMadeUpOfItsFlightsLandingsStringersAndRailings()
    {
        var (document, _) = House();
        var stair = Put(document, 1500, 600, shape: StairShape.QuarterTurn);
        stair.Mark = "S1";
        Put(document, 4500, 2000, shape: StairShape.QuarterTurn, corner: StairCorner.Winders).Mark = "S2";

        using var model = IfcExport.Build(document);
        var exported = model.Instances.OfType<IfcStair>().Single(product => $"{product.Tag}" == "S1");
        var parts = model.Instances.OfType<IfcRelAggregates>().Where(relation => relation.RelatingObject == exported)
            .SelectMany(relation => relation.RelatedObjects).ToList();

        // Two flights, the landing between, its strings, and its balustrades and guarding.
        var flights = parts.OfType<IfcStairFlight>().ToList();
        Assert.Equal(2, flights.Count);
        Assert.All(flights, flight => Assert.Equal(IfcStairFlightTypeEnum.STRAIGHT, flight.PredefinedType));
        Assert.Equal(IfcSlabTypeEnum.LANDING, Assert.Single(parts.OfType<IfcSlab>()).PredefinedType);
        Assert.Equal(IfcMemberTypeEnum.STRINGER, Assert.Single(parts.OfType<IfcMember>()).PredefinedType);
        Assert.Contains(parts.OfType<IfcRailing>(), railing => railing.PredefinedType == IfcRailingTypeEnum.GUARDRAIL);

        // Each flight's risers and treads: 13 treads between them, the landing a step of its own.
        long Count(IfcStairFlight flight, string name) => (long)(Xbim.Ifc4.MeasureResource.IfcCountMeasure)model.Instances.OfType<IfcRelDefinesByProperties>()
            .Where(relation => relation.RelatedObjects.Contains(flight))
            .Select(relation => relation.RelatingPropertyDefinition).OfType<IfcPropertySet>()
            .Single(set => set.Name == "Pset_StairFlightCommon")
            .HasProperties.OfType<IfcPropertySingleValue>().Single(property => property.Name == name).NominalValue!;
        Assert.Equal(13, flights.Sum(flight => Count(flight, "NumberOfTreads")));

        // The stair itself drawn by the line walked up it.
        var representation = Assert.Single(exported.Representation!.Representations);
        Assert.Equal(("Axis", "Curve3D"), (representation.RepresentationIdentifier?.ToString(), representation.RepresentationType?.ToString()));

        // Its parts belong to it, not to the storey on their own.
        var contained = model.Instances.OfType<IfcRelContainedInSpatialStructure>().SelectMany(relation => relation.RelatedElements).ToList();
        Assert.Contains(exported, contained);
        Assert.DoesNotContain(contained, element => flights.Contains(element));

        // On winders, a flight that winds.
        var winding = model.Instances.OfType<IfcStair>().Single(product => $"{product.Tag}" == "S2");
        Assert.Contains(model.Instances.OfType<IfcRelAggregates>().Where(relation => relation.RelatingObject == winding).SelectMany(relation => relation.RelatedObjects).OfType<IfcStairFlight>(),
            flight => flight.PredefinedType == IfcStairFlightTypeEnum.WINDER);
    }
}
