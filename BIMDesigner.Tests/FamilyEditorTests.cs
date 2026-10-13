using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Elements;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;
using BIMDesigner.Infrastructure.Interoperability;
using BIMDesigner.Infrastructure.Serialization;
using BIMDesigner.UI;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.SharedFacilitiesElements;
using PartFinish = BIMDesigner.Core.Views.ComponentModel.PartFinish;

namespace BIMDesigner.Tests;

/// <summary>
/// The family editor: families of your own drawn from simple solids that stretch with their
/// size, or loaded from an OBJ or STL model - brought to millimetres, stood up, welded, thinned,
/// its creases found - placed, mirrored, cut, saved and exported like any other.
/// </summary>
[Collection("Wpf")]
public class FamilyEditorTests
{
    private static Component Place(BimDocument document, ComponentType type, double x = 0, double rotation = 0, bool flip = false)
    {
        var component = new Component { TypeId = type.Id, TypeKind = type.Kind, LevelId = document.Levels[0].Id, Location = new Point2D(x, 0), Rotation = rotation, FlipHand = flip };
        document.Add(component);
        return component;
    }

    /// <summary>A table of your own: a top, and a leg at one corner.</summary>
    private static ComponentType Table(BimDocument document)
    {
        var family = new FamilyDefinition
        {
            Name = "My Table", Width = 1200, Depth = 800, Height = 750,
            Solids =
            {
                new ComponentSolid { Name = "Top", From = -0.5, To = 0.5, Back = -0.5, Front = 0.5, Bottom = 0.95, Top = 1 },
                new ComponentSolid { Name = "Leg", Finish = PartFinish.Metal, From = -0.5, To = -0.45, Back = -0.5, Front = -0.425, Bottom = 0, Top = 0.95 }
            }
        };
        var type = family.NewType(document);
        document.AddType(type);
        return type;
    }

    /// <summary>A unit cube as a Y-up OBJ in metres, its six faces as quads.</summary>
    private const string CubeObj = """
        v 0 0 0
        v 1 0 0
        v 1 1 0
        v 0 1 0
        v 0 0 1
        v 1 0 1
        v 1 1 1
        v 0 1 1
        f 1 4 3 2
        f 5 6 7 8
        f 1 2 6 5
        f 3 4 8 7
        f 2 3 7 6
        f 4 1 5 8
        """;

    private static (ComponentMesh Mesh, double Width, double Depth, double Height) Cube(ModelUnits units = ModelUnits.Auto)
    {
        var corners = ModelFiles.ReadObj(CubeObj.Split('\n'), out var triangles);
        return ComponentMeshes.From(corners, triangles, units, yUp: true, "cube.obj")!.Value;
    }

    [Fact]
    public void ASolidFamilyStretchesWithItsSize()
    {
        var document = BimDocument.CreateDefault();
        var table = Table(document);

        var leg = ComponentModel.Parts(table).Single(part => part.Finish == PartFinish.Metal);
        Assert.Equal((-600.0, -540.0), (leg.From, leg.To));

        // Made 1800 long, its leg is still at its corner.
        table.Width = 1800;
        leg = ComponentModel.Parts(table).Single(part => part.Finish == PartFinish.Metal);
        Assert.Equal(-900, leg.From, 6);

        // In plan: its footprint and each solid seen from above.
        var outlines = ComponentModel.PlanOutlines(table, new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0));
        Assert.Equal(1 + 2, outlines.Count);
    }

    [Fact]
    public void AFamilyTheProgramDrawsStartsAsTheSolidsItIsDrawnWith()
    {
        var document = BimDocument.CreateDefault();
        var chair = document.TypesOf<ComponentType>().First(type => type.Form == ComponentForm.Chair);
        var parts = ComponentModel.Parts(chair);

        var family = FamilyDefinition.Of(chair);
        family.Form = ComponentForm.Custom;
        family.Solids = Families.SolidsOf(chair);
        var mine = family.NewType(document);

        var drawn = ComponentModel.Parts(mine);
        Assert.Equal(parts.Count, drawn.Count);
        foreach (var (was, now) in parts.Zip(drawn))
        {
            Assert.Equal(was.From, now.From, 6);
            Assert.Equal(was.Top, now.Top, 6);
            Assert.Equal(was.Shape, now.Shape);
        }
    }

    [Fact]
    public void ASphereAConeAndAWedgeFillTheirBoxes()
    {
        var document = BimDocument.CreateDefault();
        var family = new FamilyDefinition { Name = "Shapes", Width = 400, Depth = 400, Height = 400 };
        foreach (var shape in new[] { SolidShape.Sphere, SolidShape.Cone, SolidShape.Wedge })
            family.Solids.Add(new ComponentSolid { Shape = shape, From = -0.5, To = 0.5, Back = -0.5, Front = 0.5, Bottom = 0, Top = 1 });

        foreach (var (shape, i) in family.Solids.Select((solid, i) => (solid.Shape, i)).ToList())
        {
            var one = new FamilyDefinition { Name = $"Only {shape}", Width = 400, Depth = 400, Height = 400, Solids = { family.Solids[i].Copy() } };
            var type = one.NewType(document);
            document.AddType(type);
            var mesh = Assert.Single(ModelMeshBuilder.BuildComponent(document, Place(document, type, 2000 * i)));
            var (min, max) = mesh.Bounds()!.Value;
            Assert.Equal(400, max.Z - min.Z, 1);
            Assert.InRange(max.X - min.X, 395, 400.01);

            // Each face facing out from its middle.
            var middle = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            for (var t = 0; t < mesh.Indices.Count; t += 3)
            {
                var (a, b, c) = (mesh.Positions[mesh.Indices[t]], mesh.Positions[mesh.Indices[t + 1]], mesh.Positions[mesh.Indices[t + 2]]);
                var normal = Cross(a, b, c);
                var centroid = new Point3D((a.X + b.X + c.X) / 3 - middle.X, (a.Y + b.Y + c.Y) / 3 - middle.Y, (a.Z + b.Z + c.Z) / 3 - middle.Z);
                Assert.True(normal.X * centroid.X + normal.Y * centroid.Y + normal.Z * centroid.Z >= -1e-6, $"{shape}: a face turned inward");
            }
        }

        // The wedge is its full height at the back and slopes to nothing at the front.
        var wedge = document.TypesOf<ComponentType>().Single(type => type.Name == "Only Wedge");
        var wedgeMesh = ModelMeshBuilder.BuildComponent(document, document.Elements.OfType<Component>().Single(c => c.TypeId == wedge.Id)).Single();
        Assert.Equal(400, wedgeMesh.Positions.Where(p => p.Y > 199).Max(p => p.Z), 1);
        Assert.True(wedgeMesh.Positions.Where(p => p.Y < -199).Max(p => p.Z) < 1);
    }

    private static Point3D Cross(Point3D a, Point3D b, Point3D c)
    {
        var (ux, uy, uz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var (vx, vy, vz) = (c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        return new Point3D(uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx);
    }

    [Fact]
    public void AnObjModelIsBroughtToMillimetresStoodUpAndItsCreasesFound()
    {
        // A one-unit cube: a metre, by its size.
        var (mesh, width, depth, height) = Cube();
        Assert.Equal((1000.0, 1000.0, 1000.0), (width, depth, height));
        Assert.Equal(8, mesh.Positions.Count);
        Assert.Equal(12, mesh.TriangleCount);

        // Its twelve edges are creases; the diagonals across its faces are not.
        Assert.Equal(12, mesh.Edges.Count / 2);

        // Said to be in inches, it is 25.4 across.
        Assert.Equal(25.4, Cube(ModelUnits.Inches).Width, 6);

        // In the family's box: across and front to back about its middle, up from its base.
        Assert.Equal((-0.5, 0.5), (mesh.Positions.Min(p => p.X), mesh.Positions.Max(p => p.X)));
        Assert.Equal((0.0, 1.0), (mesh.Positions.Min(p => p.Z), mesh.Positions.Max(p => p.Z)));
    }

    [Fact]
    public void AnStlModelTextOrBinaryIsWeldedAndAFlatOneStandsUp()
    {
        // A box 300 x 200 x 100 as binary STL, its triangles not sharing corners.
        var triangles = BoxTriangles(300, 200, 100);
        var binary = new byte[84 + 50 * triangles.Count];
        BitConverter.GetBytes((uint)triangles.Count).CopyTo(binary, 80);
        for (var t = 0; t < triangles.Count; t++)
        for (var k = 0; k < 3; k++)
        {
            var at = 84 + 50 * t + 12 + 12 * k;
            BitConverter.GetBytes((float)triangles[t][k].X).CopyTo(binary, at);
            BitConverter.GetBytes((float)triangles[t][k].Y).CopyTo(binary, at + 4);
            BitConverter.GetBytes((float)triangles[t][k].Z).CopyTo(binary, at + 8);
        }

        var (corners, faces) = ModelFiles.ReadStl(binary);
        Assert.Equal(36, corners.Count);
        var (mesh, width, depth, height) = ComponentMeshes.From(corners, faces, ModelUnits.Millimetres, yUp: false, "box.stl")!.Value;
        Assert.Equal(8, mesh.Positions.Count);
        Assert.Equal((300.0, 200.0, 100.0), (width, depth, height));

        // As text, the same.
        var text = new StringBuilder("solid box\n");
        foreach (var triangle in triangles)
        {
            text.AppendLine("facet normal 0 0 0\nouter loop");
            foreach (var p in triangle) text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"vertex {p.X} {p.Y} {p.Z}"));
            text.AppendLine("endloop\nendfacet");
        }

        var (textCorners, _) = ModelFiles.ReadStl(Encoding.ASCII.GetBytes(text.Append("endsolid box").ToString()));
        Assert.Equal(36, textCorners.Count);

        // Stood up about its width, its depth and height change places; turned round, its width and depth.
        var (_, w1, d1, h1) = ComponentMeshes.Turned(mesh, width, depth, height, standUp: true);
        Assert.Equal((300.0, 100.0, 200.0), (Math.Round(w1, 6), Math.Round(d1, 6), Math.Round(h1, 6)));
        var (_, w2, d2, h2) = ComponentMeshes.Turned(mesh, width, depth, height, standUp: false);
        Assert.Equal((200.0, 300.0, 100.0), (Math.Round(w2, 6), Math.Round(d2, 6), Math.Round(h2, 6)));
    }

    /// <summary>A box's twelve triangles, wound to face out, each with corners of its own.</summary>
    private static List<Point3D[]> BoxTriangles(double x, double y, double z)
    {
        Point3D P(int i) => new((i & 1) * x, (i >> 1 & 1) * y, (i >> 2 & 1) * z);
        var quads = new[] { (0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (1, 3, 7, 5), (0, 4, 6, 2) };
        return quads.SelectMany(q => new[] { new[] { P(q.Item1), P(q.Item2), P(q.Item3) }, new[] { P(q.Item1), P(q.Item3), P(q.Item4) } }).ToList();
    }

    [Fact]
    public void AModelWithFarTooManyTrianglesIsThinned()
    {
        // A wavy sheet of 300 x 300 squares: 180,000 triangles.
        const int n = 300;
        var corners = new List<Point3D>();
        for (var i = 0; i <= n; i++)
        for (var j = 0; j <= n; j++)
            corners.Add(new Point3D(i * 10, j * 10, 50 * Math.Sin(i * 0.1) * Math.Cos(j * 0.1) + 100));
        var triangles = new List<int>();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            int At(int a, int b) => a * (n + 1) + b;
            triangles.AddRange(new[] { At(i, j), At(i + 1, j), At(i + 1, j + 1), At(i, j), At(i + 1, j + 1), At(i, j + 1) });
        }

        var (mesh, width, _, _) = ComponentMeshes.From(corners, triangles, ModelUnits.Millimetres, yUp: false, "sheet.stl")!.Value;
        Assert.InRange(mesh.TriangleCount, 1000, ComponentMeshes.Budget);
        Assert.Equal(3000, width, 0);
    }

    [Fact]
    public void AModelFamilyIsDrawnTheWayItFacesAndMirroredStillFacesOut()
    {
        var document = BimDocument.CreateDefault();
        var (mesh, width, depth, height) = Cube(ModelUnits.Millimetres);
        var type = new FamilyDefinition { Name = "Cube", Width = width * 1000, Depth = depth * 1000, Height = height * 1000, Mesh = mesh }.NewType(document);
        document.AddType(type);

        foreach (var flip in new[] { false, true })
        {
            var built = Assert.Single(ModelMeshBuilder.BuildComponent(document, Place(document, type, 5000, rotation: 30, flip: flip)));
            Assert.Equal(12, built.TriangleCount);
            Assert.Equal(12, built.Edges.Count);

            var (min, max) = built.Bounds()!.Value;
            var middle = new Point3D((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            for (var t = 0; t < built.Indices.Count; t += 3)
            {
                var (a, b, c) = (built.Positions[built.Indices[t]], built.Positions[built.Indices[t + 1]], built.Positions[built.Indices[t + 2]]);
                var normal = Cross(a, b, c);
                var out_ = new Point3D((a.X + b.X + c.X) / 3 - middle.X, (a.Y + b.Y + c.Y) / 3 - middle.Y, (a.Z + b.Z + c.Z) / 3 - middle.Z);
                Assert.True(normal.X * out_.X + normal.Y * out_.Y + normal.Z * out_.Z > 0, flip ? "Mirrored, a face turned inward" : "A face turned inward");
            }
        }

        // In plan, its outline from above with its footprint.
        var outlines = ComponentModel.PlanOutlines(type, new ComponentModel.Frame(new Point2D(0, 0), Vector2D.UnitX, 0));
        Assert.Equal(2, outlines.Count);
        Assert.Equal(4, outlines[1].Count);
    }

    [Fact]
    public void AFamilyFittedToItsSolidsKeepsThemWhereTheyAre()
    {
        var family = new FamilyDefinition
        {
            Name = "Fit", Width = 1000, Depth = 1000, Height = 1000,
            Solids = { new ComponentSolid { From = 0, To = 0.2, Back = -0.1, Front = 0.1, Bottom = 0.5, Top = 1.5 } }
        };

        Families.FitSize(family);
        Assert.Equal((200.0, 200.0, 1500.0), (family.Width, family.Depth, family.Height));

        var part = family.Solids[0].Part(family.Width, family.Depth, family.Height);
        Assert.Equal((-100.0, 100.0), (part.From, part.To));
        Assert.Equal((500.0, 1500.0), (part.Bottom, part.Top));
    }

    [Fact]
    public void AFamilyOfYourOwnIsSavedWithItsSolidsAndModel()
    {
        var document = BimDocument.CreateDefault();
        var table = Table(document);
        var (mesh, width, depth, height) = Cube();
        var model = new FamilyDefinition { Name = "Cube Model", Width = width, Depth = depth, Height = height, Mesh = mesh }.NewType(document);
        document.AddType(model);

        var loaded = ProjectFile.FromJson(ProjectFile.ToJson(document));
        var tableBack = loaded.TypesOf<ComponentType>().Single(type => type.Id == table.Id);
        Assert.Equal(ComponentForm.Custom, tableBack.Form);
        Assert.Equal(2, tableBack.Solids.Count);
        Assert.Equal(PartFinish.Metal, tableBack.Solids[1].Finish);
        Assert.Equal(-0.45, tableBack.Solids[1].To, 9);

        var modelBack = loaded.TypesOf<ComponentType>().Single(type => type.Id == model.Id).Mesh!;
        Assert.Equal((mesh.Positions.Count, mesh.TriangleCount, mesh.Edges.Count, "cube.obj"), (modelBack.Positions.Count, modelBack.TriangleCount, modelBack.Edges.Count, modelBack.Source));
        Assert.Equal(mesh.Positions[3].X, modelBack.Positions[3].X, 6);

        // A duplicate has solids of its own.
        var copy = tableBack.Duplicate("My Table 2");
        copy.Solids[0].Top = 0.5;
        Assert.Equal(1, tableBack.Solids[0].Top);
    }

    [Fact]
    public void EditingAFamilyIsOneStepAndItsPiecesFollowItsCategory()
    {
        var document = BimDocument.CreateDefault();
        var table = Table(document);
        var piece = Place(document, table);

        var edited = FamilyDefinition.Of(table);
        edited.Kind = BuiltInCategory.Casework;
        edited.Solids.RemoveAt(1);
        var command = new EditFamilyCommand(document, table, edited);

        command.Redo();
        Assert.Equal((BuiltInCategory.Casework, 1), (table.Kind, table.Solids.Count));
        Assert.Equal(BuiltInCategory.Casework, piece.Category);

        command.Undo();
        Assert.Equal((BuiltInCategory.Furniture, 2), (table.Kind, table.Solids.Count));
        Assert.Equal(BuiltInCategory.Furniture, piece.Category);
    }

    [Fact]
    public void AFamilyOfYourOwnIsExportedWithItsBody()
    {
        var document = BimDocument.CreateDefault();
        var table = Table(document);
        Place(document, table);

        using var model = IfcExport.Build(document);
        var furniture = Assert.Single(model.Instances.OfType<IfcFurniture>());
        Assert.Equal("My Table", furniture.Name);
        Assert.NotNull(furniture.Representation);
    }

    [Fact]
    public void TheEditorAddsAndSizesSolidsInMillimetres()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (Application.Current is null) new App().InitializeComponent();
                var document = BimDocument.CreateDefault();
                var editor = new FamilyEditorWindow(document, null);
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                T Field<T>(string name) => (T)typeof(FamilyEditorWindow).GetField(name, flags)!.GetValue(editor)!;
                void Call(string name, object sender) => typeof(FamilyEditorWindow).GetMethod(name, flags)!.Invoke(editor, new[] { sender, new RoutedEventArgs() });

                // A new family starts as a box filling its 600 cube.
                Assert.Single(editor.Family.Solids);

                Call("OnAddSolid", new Button { Tag = "Sphere" });
                Assert.Equal(2, editor.Family.Solids.Count);
                Assert.Equal(SolidShape.Sphere, editor.Family.Solids[1].Shape);

                // Typed in: 300 across, 150 up off the base, 100 to the left of the middle.
                Field<TextBox>("SolidWidthBox").Text = "300";
                Field<TextBox>("BottomBox").Text = "150";
                Field<TextBox>("AcrossBox").Text = "-100";
                Call("OnSolidCommitted", editor);
                var part = editor.Family.Solids[1].Part(600, 600, 600);
                Assert.Equal((-250.0, 50.0, 150.0), (part.From, part.To, part.Bottom));

                // The box made 1.2 m wide: the sphere stretches with it.
                Field<TextBox>("WidthBox").Text = "1.2 m";
                Call("OnSizeCommitted", editor);
                Assert.Equal(1200, editor.Family.Width);
                Assert.Equal(-500, editor.Family.Solids[1].Part(1200, 600, 600).From, 6);

                // Made into a type.
                var type = editor.Family.NewType(document);
                Assert.Equal((ComponentForm.Custom, 2), (type.Form, type.Solids.Count));
            }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw failure;
    }
}
