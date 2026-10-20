using System.IO.Compression;
using System.Xml.Linq;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Infrastructure.Interoperability;
using Xbim.Ifc4.Interfaces;

namespace BIMDesigner.Tests;

/// <summary>The classification carried out to IFC and back in, and the project handed over as COBie.</summary>
public class CobieClassificationTests
{
    private static BimDocument CodedHouse()
    {
        var document = IfcImportTests.House();
        document.TypesOf<WallType>().First(t => t.Function == WallFunction.Exterior).ClassificationCode = "EF_25_10_25";
        document.TypesOf<DoorType>().First().ClassificationCode = "EF_25_30_25";
        document.Elements.OfType<Room>().Single().ClassificationCode = "SL_20_15_59";
        document.Elements.OfType<Room>().Single().Department = "Ground";
        return document;
    }

    [Fact]
    public void ClassificationGoesOutToIfcAndComesBackIn()
    {
        var document = CodedHouse();
        using (var model = IfcExport.Build(document))
        {
            var references = model.Instances.OfType<IIfcClassificationReference>().ToList();
            Assert.Contains(references, r => r.Identification == "EF_25_10_25" && r.Name == "External walls");
            Assert.Equal("Uniclass 2015", Assert.Single(model.Instances.OfType<IIfcClassification>()).Name);

            var back = IfcImport.Read(model).Document;
            var wall = back.Walls.First(w => w.Start.DistanceTo(new Core.Geometry.Point2D(0, 0)) < 1);
            Assert.Equal("EF_25_10_25", back.GetWallType(wall)!.ClassificationCode);
            Assert.Equal("EF_25_30_25", back.FindType<DoorType>(back.Elements.OfType<Door>().Single().TypeId)!.ClassificationCode);
            Assert.Equal("SL_20_15_59", back.Elements.OfType<Room>().Single().ClassificationCode);
        }

        var path = Path.Combine(Path.GetTempPath(), $"coded-{Guid.NewGuid():N}.ifc");
        try
        {
            Ifc2x3Export.Save(document, path);
            var back = IfcImport.Open(path).Document;
            Assert.Equal("EF_25_30_25", back.FindType<DoorType>(back.Elements.OfType<Door>().Single().TypeId)!.ClassificationCode);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A workbook's sheets, each a list of rows of its cells' text.</summary>
    private static Dictionary<string, List<List<string>>> Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open());
        var sheets = new Dictionary<string, List<List<string>>>();
        foreach (var sheet in workbook.Descendants(main + "sheet"))
        {
            var id = sheet.Attribute(r + "id")!.Value;
            var target = rels.Root!.Elements().Single(e => e.Attribute("Id")!.Value == id).Attribute("Target")!.Value;
            var xml = XDocument.Load(zip.GetEntry("xl/" + target)!.Open());
            sheets[sheet.Attribute("name")!.Value] = xml.Descendants(main + "row")
                .Select(row => row.Elements(main + "c").Select(c => c.Element(main + "is")?.Value ?? c.Element(main + "v")?.Value ?? string.Empty).ToList())
                .ToList();
        }

        return sheets;
    }

    [Fact]
    public void TheProjectIsHandedOverAsCobie()
    {
        var document = CodedHouse();
        var path = Path.Combine(Path.GetTempPath(), $"cobie-{Guid.NewGuid():N}.xlsx");
        try
        {
            CobieExport.Save(document, path, new CobieOptions { ContactEmail = "design@example.co.uk", Company = "Example Architects", CreatedOn = new DateTime(2026, 10, 20, 9, 30, 0) });
            var sheets = Read(path);

            Assert.Equal(new[] { "Instruction", "Contact", "Facility", "Floor", "Space", "Zone", "Type", "Component", "System", "Assembly", "Connection",
                "Spare", "Resource", "Job", "Impact", "Document", "Attribute", "Coordinate", "Issue", "PickLists" }, sheets.Keys);

            // Columns in COBie's order.
            Assert.Equal(new[] { "Name", "CreatedBy", "CreatedOn", "Category", "Description", "AssetType", "Manufacturer", "ModelNumber" }, sheets["Type"][0].Take(8));
            Assert.Equal(new[] { "Name", "CreatedBy", "CreatedOn", "Category", "TypeName", "Space", "Description" }, sheets["Component"][0].Take(7));
            Assert.Equal(new[] { "Email", "CreatedBy", "CreatedOn", "Category", "Company", "Phone" }, sheets["Contact"][0].Take(6));
            Assert.Equal("design@example.co.uk", sheets["Contact"][1][0]);
            Assert.Equal("2026-10-20T09:30:00", sheets["Contact"][1][2]);

            // Two storeys; the kitchen, in its department's zone.
            Assert.Equal(new[] { "Ground Floor", "First Floor" }, sheets["Floor"].Skip(1).Select(row => row[0]));
            var space = Assert.Single(sheets["Space"].Skip(1));
            Assert.Equal(("G01", "SL_20_15_59", "Ground Floor", "Kitchen"), (space[0], space[3], space[4], space[5]));
            Assert.Equal("G01", Assert.Single(sheets["Zone"].Skip(1))[4]);

            // The door, the window and the chair: their types, and each in the kitchen or beside it.
            var types = sheets["Type"].Skip(1).ToDictionary(row => row[0]);
            var components = sheets["Component"].Skip(1).ToList();
            Assert.Equal(3, components.Count);
            Assert.All(components, row => Assert.True(types.ContainsKey(row[4]), $"{row[4]} is not on the Type sheet"));
            var door = components.Single(row => row[0] == "D1");
            Assert.Equal("EF_25_30_25: Doors", types[door[4]][3]);
            Assert.Equal("Fixed", types[door[4]][5]);
            Assert.Equal("G01", door[5]);
            Assert.Contains(types.Values, row => row[5] == "Movable");

            // Every required field filled - with n/a where there is nothing to say.
            foreach (var name in new[] { "Facility", "Floor", "Space", "Type", "Component" })
                Assert.All(sheets[name].Skip(1), row => Assert.All(row.Take(4), cell => Assert.False(string.IsNullOrWhiteSpace(cell), $"{name} has an empty required cell")));

            // Their parameters as attributes, and where each component is.
            Assert.Contains(sheets["Attribute"].Skip(1), row => row[4] == "Component" && row[5] == "D1");
            Assert.Equal(3, sheets["Coordinate"].Count - 1);
        }
        finally { File.Delete(path); }
    }
}
