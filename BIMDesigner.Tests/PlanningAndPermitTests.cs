using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Documents.Commands;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Sheets;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI.Export;

namespace BIMDesigner.Tests;

/// <summary>
/// The drawings an application goes in with: the site edged in red and other land in blue; the
/// location plan and block plan made on sheets of their own at their scales, drawing only what such
/// a plan shows, with a north point, a scale bar and the map's credit, and what they are asked for
/// checked; and a drawing set as one PDF bookmarked by sheet and signed, the signature true to the
/// bytes and broken by any change.
/// </summary>
[Collection("Wpf")]
public class PlanningAndPermitTests
{
    /// <summary>A house of four walls, a room and a dimension in it, on a plot edged in red, and the garden beside it the applicant owns.</summary>
    private static (BimDocument Document, PropertyLine Site, PropertyLine Garden) Plot()
    {
        var document = BimDocument.CreateDefault();
        var level = document.Levels[0].Id;
        var exterior = document.TypesOf<WallType>().First(type => type.Function == WallFunction.Exterior);
        var corners = new[] { new Point2D(0, 0), new Point2D(10000, 0), new Point2D(10000, 8000), new Point2D(0, 8000) };
        for (var i = 0; i < 4; i++)
            document.Add(new Wall { Start = corners[i], End = corners[(i + 1) % 4], TypeId = exterior.Id, LevelId = level, UnconnectedHeight = 3000 });
        document.Add(new Room { LevelId = level, Location = new Point2D(5000, 4000), Name = "Living" });

        var site = new PropertyLine { LevelId = level, Planning = PlanningEdge.ApplicationSite };
        site.SetBoundary(new[] { new Point2D(-6000, -10000), new Point2D(16000, -10000), new Point2D(16000, 14000), new Point2D(-6000, 14000) });
        document.Add(site);
        var garden = new PropertyLine { LevelId = level, Planning = PlanningEdge.OtherLand };
        garden.SetBoundary(new[] { new Point2D(16000, -10000), new Point2D(30000, -10000), new Point2D(30000, 14000), new Point2D(16000, 14000) });
        document.Add(garden);
        return (document, site, garden);
    }

    [Fact]
    public void TheLocationAndBlockPlansAreMadeOnSheetsOfTheirOwnAtTheirScales()
    {
        var (document, site, _) = Plot();
        var before = PlanningDrawings.Checks(document);
        Assert.Contains(before, check => check.Met == true && check.Said.StartsWith("The application site is edged in red"));
        Assert.Contains(before, check => check.Met == false && check.Said.StartsWith("No location plan yet"));

        var history = new UndoStack();
        history.Execute(PlanningDrawings.Make(document, 1250, 500, "© Crown copyright and database rights 2026 OS AC0000000000"));

        var location = PlanningDrawings.Drawing(document, PlanningDrawing.Location)!;
        var block = PlanningDrawings.Drawing(document, PlanningDrawing.Block)!;
        Assert.Equal("Location Plan", location.Name);
        Assert.Equal(1250, document.ViewSettings.ScaleOf(Callouts.Reference(location)));
        Assert.Equal(500, document.ViewSettings.ScaleOf(Callouts.Reference(block)));
        Assert.Equal(DetailLevel.Coarse, document.ViewSettings.DetailLevelOf(Callouts.Reference(location)));

        // Round the whole site, filling an A4 sheet upright; the block plan an A3 across.
        var redBox = BoundingBox2D.Around(site.Boundary);
        Assert.True(location.Region.MinX <= redBox.MinX && location.Region.MaxX >= redBox.MaxX && location.Region.MinY <= redBox.MinY && location.Region.MaxY >= redBox.MaxY);
        var locationSheet = PlanningDrawings.SheetOf(document, PlanningDrawing.Location)!;
        Assert.Equal(("PL-001", PaperSize.A4, PaperOrientation.Landscape), (locationSheet.Number, locationSheet.PaperSize, locationSheet.Orientation));
        var placed = Assert.Single(locationSheet.Viewports);
        Assert.True(placed.ShowNorth && placed.ShowScaleBar);
        Assert.StartsWith("© Crown copyright", placed.Note);
        Assert.True(locationSheet.IsWellPlaced(document, placed));
        Assert.Equal(PaperSize.A3, PlanningDrawings.SheetOf(document, PlanningDrawing.Block)!.PaperSize);

        // No box on the ground floor plan; on the drawing only what such a plan shows.
        Assert.Empty(Callouts.On(document, ViewReference.FloorPlan(location.LevelId)));
        var shows = document.ViewSettings.FilterFor(document, Callouts.Reference(location));
        Assert.True(shows(document.Walls.First()));
        Assert.True(shows(site));
        Assert.False(shows(document.All<Room>().Single()));

        // Made again at another scale: the same drawings, brought up to date.
        history.Execute(PlanningDrawings.Make(document, 2500, 200, "© Crown copyright and database rights 2026 OS AC0000000000"));
        Assert.Single(document.All<Callout>(), callout => callout.Planning == PlanningDrawing.Location);
        Assert.Equal(2500, document.ViewSettings.ScaleOf(Callouts.Reference(location)));
        Assert.Equal(2, document.All<Sheet>().Count());

        // Without a map under it, said; and the blue line kept off the red.
        var checks = PlanningDrawings.Checks(document);
        Assert.Contains(checks, check => check.Met == false && check.Said.StartsWith("No map under the location plan"));
        Assert.DoesNotContain(checks, check => check.Said.Contains("overlaps the red line"));
        Assert.Contains(checks, check => check.Met == null);

        // One step each to undo.
        history.Undo();
        history.Undo();
        Assert.Empty(document.All<Callout>());
        Assert.Empty(document.All<Sheet>());
    }

    [Fact]
    public void RedAndBlueLinesAndThePlanningDrawingsAreKeptWithTheProject()
    {
        var (document, site, garden) = Plot();

        // The land beside the site has none of the building on it: not covered, not stood over.
        Assert.Equal("Within it", PropertyLines.Verdict(document, site));
        Assert.Equal("No building on it", PropertyLines.Verdict(document, garden));
        Assert.Equal(0, PropertyLines.Coverage(document, garden));
        Assert.True(PropertyLines.Coverage(document, site) > 0);

        garden.SetBoundary(new[] { new Point2D(10000, -10000), new Point2D(30000, -10000), new Point2D(30000, 14000), new Point2D(10000, 14000) });
        Assert.Contains(PlanningDrawings.Checks(document), check => check.Met == false && check.Said.Contains("overlaps the red line"));

        new UndoStack().Execute(PlanningDrawings.Make(document, 1250, 500, "© Crown copyright 2026 OS 100000000"));
        var reopened = ProjectFile.FromJson(ProjectFile.ToJson(document));
        Assert.Equal(PlanningEdge.ApplicationSite, reopened.All<PropertyLine>().Single(line => line.Id == site.Id).Planning);
        Assert.Equal(PlanningEdge.OtherLand, reopened.All<PropertyLine>().Single(line => line.Id == garden.Id).Planning);
        Assert.NotNull(PlanningDrawings.Drawing(reopened, PlanningDrawing.Block));
        Assert.Equal("© Crown copyright 2026 OS 100000000", reopened.ProjectInformation.PlanningMapNote);
        var viewport = PlanningDrawings.SheetOf(reopened, PlanningDrawing.Location)!.Viewports.Single();
        Assert.True(viewport.ShowNorth && viewport.ShowScaleBar);
        Assert.Equal("© Crown copyright 2026 OS 100000000", viewport.Note);

        // The red line is picked in Properties too.
        var edge = site.GetInstanceParameters(document).Single(parameter => parameter.Definition == PropertyLineParameters.Planning);
        Assert.Equal("Red line - the application site", edge.Value);
        Assert.Contains("Blue line - other land owned", edge.AllowedValues!);
    }

    [Fact]
    public void ASetIsBookmarkedBySheetAndItsSignatureIsTrueToItsBytes()
    {
        var (document, _, _) = Plot();
        new UndoStack().Execute(PlanningDrawings.Make(document, 1250, 500, "© Crown copyright 2026 OS 100000000"));
        var sheets = document.All<Sheet>().OrderBy(sheet => sheet.Number).ToList();

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Jane Architect, O=Studio", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

        var path = Path.Combine(Path.GetTempPath(), $"signed-{Guid.NewGuid():N}.pdf");
        try
        {
            SheetExport.ToPdf(document, sheets, path, new PdfSigner(certificate, "Issued for planning", "London"));
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.Latin1.GetString(bytes);

            // A bookmark for each sheet, by number and name; the set opens with them.
            Assert.Contains("/PageMode /UseOutlines", text);
            Assert.Contains("/Type /Outlines", text);
            Assert.Contains(PdfWriter.TextString("PL-001 - Location Plan"), text);
            Assert.Contains(PdfWriter.TextString("PL-002 - Block Plan"), text);
            Assert.Contains("/SigFlags 3", text);

            // The signature covers every byte but its own, and checks against them.
            var found = Regex.Match(text, @"/ByteRange \[ 0 (\d+) (\d+) (\d+) \] /Contents <([0-9A-F]+)>");
            Assert.True(found.Success);
            var (start, end, rest) = (int.Parse(found.Groups[1].Value), int.Parse(found.Groups[2].Value), int.Parse(found.Groups[3].Value));
            Assert.Equal(bytes.Length, end + rest);
            Assert.Equal((byte)'<', bytes[start]);
            Assert.Equal((byte)'>', bytes[end - 1]);

            byte[] Covered(byte[] file)
            {
                var covered = new byte[start + rest];
                Buffer.BlockCopy(file, 0, covered, 0, start);
                Buffer.BlockCopy(file, end, covered, start, rest);
                return covered;
            }

            var hex = Convert.FromHexString(found.Groups[4].Value);
            var der = hex[..(4 + (hex[2] << 8 | hex[3]))];
            var cms = new SignedCms(new ContentInfo(Covered(bytes)), detached: true);
            cms.Decode(der);
            cms.CheckSignature(verifySignatureOnly: true);
            Assert.Equal("Jane Architect", cms.SignerInfos[0].Certificate!.GetNameInfo(X509NameType.SimpleName, false));

            // A byte changed after signing, and it no longer checks.
            var changed = (byte[])bytes.Clone();
            changed[start / 2] ^= 0x20;
            var broken = new SignedCms(new ContentInfo(Covered(changed)), detached: true);
            broken.Decode(der);
            Assert.ThrowsAny<CryptographicException>(() => broken.CheckSignature(verifySignatureOnly: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OnlyDigitalIdsForSigningAreOfferedToSignWith()
    {
        var signs = typeof(UI.MainWindow).Assembly.GetType("BIMDesigner.UI.SignedSetWindow")!.GetMethod("Signs", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool Offered(X509KeyUsageFlags usage, params string[] purposes) => OfferedAs("CN=Jane Architect", usage, purposes);
        bool OfferedAs(string subject, X509KeyUsageFlags usage, params string[] purposes)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, critical: false));
            if (purposes.Length > 0)
            {
                var oids = new OidCollection();
                foreach (var purpose in purposes) oids.Add(new Oid(purpose));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, critical: false));
            }

            using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
            return (bool)signs.Invoke(null, new object[] { certificate })!;
        }

        Assert.True(Offered(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, "1.3.6.1.4.1.311.10.3.12"));
        Assert.True(Offered(X509KeyUsageFlags.DigitalSignature, "1.3.6.1.5.5.7.3.4"));
        Assert.True(Offered(X509KeyUsageFlags.DigitalSignature));
        Assert.False(Offered(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, "1.3.6.1.5.5.7.3.1"));
        Assert.False(Offered(X509KeyUsageFlags.DigitalSignature, "1.3.6.1.5.5.7.3.2"));
        Assert.False(Offered(X509KeyUsageFlags.KeyEncipherment, "1.3.6.1.4.1.311.10.3.12"));

        // One a program made for itself, under a random id and for nothing in particular, is not offered.
        Assert.False(OfferedAs("CN=trust_812bd65f-8029-4924-88e4-fff4fbdc9774", X509KeyUsageFlags.DigitalSignature));
        Assert.False(OfferedAs("CN=localhost", X509KeyUsageFlags.DigitalSignature));
    }

    [Fact]
    public void APlanningSheetDrawsItsNorthPointAndScaleBar()
    {
        var (document, _, _) = Plot();
        new UndoStack().Execute(PlanningDrawings.Make(document, 1250, 500, "© Crown copyright 2026 OS 100000000"));
        var page = SheetExport.Page(document, PlanningDrawings.SheetOf(document, PlanningDrawing.Location)!);
        var words = Words(page.Content!);
        Assert.Contains("N", words);
        Assert.Contains("LocationPlan", words);
        Assert.Contains("50m", words);
        Assert.Contains("©Crowncopyright", words);
    }

    private static string Words(System.Windows.Media.Drawing drawing)
    {
        var said = new StringBuilder();
        void Walk(System.Windows.Media.Drawing each)
        {
            switch (each)
            {
                case System.Windows.Media.DrawingGroup group:
                    foreach (var child in group.Children) Walk(child);
                    break;
                case System.Windows.Media.GlyphRunDrawing glyphs when glyphs.GlyphRun?.Characters is { } characters:
                    said.Append(characters.ToArray());
                    break;
            }
        }

        Walk(drawing);
        return Regex.Replace(said.ToString(), @"\s+", string.Empty);
    }
}
