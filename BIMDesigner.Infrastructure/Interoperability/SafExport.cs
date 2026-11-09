using System.Globalization;
using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// The analytical model written as SAF, the Structural Analysis Format (saf.guide, version 2.2.0):
/// an .xlsx workbook analysis programs read, one sheet to each kind of object, each named as the
/// format names it - its Model and Project as a property to a row, beside its value; then its
/// materials, sections, nodes, members, plates and their openings, supports along edges and at
/// nodes, the ground under its rafts, the hinges at its beams' pinned ends, its load groups, cases
/// and combinations, and its loads on its plates and members. Metric throughout - coordinates in
/// metres, sizes and thicknesses in millimetres, loads in kN/m and kN/m² - Z up, its national code
/// the UK's or the IBC's. A raft's ground is as stiff across as down, C1x and C1y taken as C1z.
/// </summary>
public static class SafExport
{
    /// <summary>The version of the format written.</summary>
    public const string Version = "2.2.0";

    public static void Save(BimDocument document, string path)
    {
        using var stream = File.Create(path);
        XlsxWriter.Write(stream, Build(document));
    }

    public static IReadOnlyList<XlsxSheet> Build(BimDocument document)
    {
        var model = AnalyticalModel.Of(document);
        var loading = StructuralLoading.Of(document, model);
        var us = document.ProjectInformation.ClearanceStandard.IsUs();
        var sheets = new List<XlsxSheet>();
        static object? Number(double value) => double.IsFinite(value) ? Math.Round(value, 6) : null;
        static double Metres(double mm) => Math.Round(mm / 1000, 6);
        static XlsxSheet Sheet(string name, params string[] columns) => new(name, columns.Select(column => (column, HeadingKind.Plain)).ToList());

        // The model and the project: a property to a row.
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var info = document.ProjectInformation;
        var head = new XlsxSheet("Model", new[] { ("Name", HeadingKind.Plain), ($"{info.Name} - analytical model", HeadingKind.Plain) });
        head.Add("Description", us ? "Loads by ASCE 7-16" : "Loads by BS EN 1990 and BS EN 1991 with their UK National Annexes");
        head.Add("Discipline", "Structural");
        head.Add("Status", "Draft");
        head.Add("Created", now);
        head.Add("Last update", now);
        head.Add("Source type", "BIM");
        head.Add("Source application", "BIMDesigner");
        head.Add("SAF Version", Version);
        head.Add("Global coordinate system", "Z vertical");
        head.Add("LCS of cross-section", "ZYX");
        head.Add("System of units", "Metric");
        head.Add("National code", us ? "IBC" : "EC-BS-EN (British NA)");
        sheets.Add(head);

        var project = new XlsxSheet("Project", new[] { ("Name", HeadingKind.Plain), (info.Name, HeadingKind.Plain) });
        project.Add("Description", info.BuildingType);
        project.Add("Project nr", info.Number);
        project.Add("Created", now);
        project.Add("Last update", now);
        project.Add("Location", info.Address);
        sheets.Add(project);

        var storeys = Sheet("StructuralStorey", "Name", "Height level [m]");
        foreach (var level in document.Levels.OrderBy(level => level.Elevation)) storeys.Add(level.Name, Metres(level.Elevation));
        sheets.Add(storeys);

        var materials = Sheet("StructuralMaterial", "Name", "Type", "Subtype", "Quality", "Unit mass [kg/m3]", "E modulus [MPa]", "G modulus [MPa]", "Poisson Coefficient", "Thermal expansion [1/K]");
        foreach (var material in model.Materials.GroupBy(material => material.Name).Select(group => group.First()))
            materials.Add(material.Name, material.Kind.ToString(), material.Said, material.Quality, Number(material.Density), Number(material.Modulus), Number(material.ShearModulus), Number(material.Poisson), Number(material.Expansion));
        sheets.Add(materials);

        var sections = Sheet("StructuralCrossSection", "Name", "Material", "Cross-section type", "Shape", "Parameters [mm]", "Profile", "Form code", "Description ID of the profile", "A [m2]", "Iy [m4]", "Iz [m4]");
        foreach (var section in model.Sections.GroupBy(section => section.Name).Select(group => group.First()))
            sections.Add(section.Name, section.Material.Name, "Parametric", section.Shape, string.Join("; ", section.Dimensions.Select(size => Math.Round(size, 2).ToString(CultureInfo.InvariantCulture))), null, null, null,
                Number(section.Area / 1e6), Number(section.Iy / 1e12), Number(section.Iz / 1e12));
        sheets.Add(sections);

        var nodes = Sheet("StructuralPointConnection", "Name", "Coordinate X [m]", "Coordinate Y [m]", "Coordinate Z [m]");
        foreach (var node in model.Nodes) nodes.Add(node.Name, Metres(node.At.X), Metres(node.At.Y), Metres(node.At.Z));
        sheets.Add(nodes);

        var members = Sheet("StructuralCurveMember", "Name", "Type", "Cross section", "Arbitrary definition", "Nodes", "Segments", "Internal nodes", "Length [m]", "Geometrical shape", "LCS", "LCS Rotation [deg]",
            "Coordinate X [m]", "Coordinate Y [m]", "Coordinate Z [m]", "System line",
            "Analysis Y Eccentricity of Beg Node [mm]", "Analysis Z Eccentricity of Beg Node [mm]", "Analysis Y Eccentricity of End Node [mm]", "Analysis Z Eccentricity of End Node [mm]",
            "Layer", "Behaviour in analysis");
        foreach (var member in model.Members)
        {
            var column = member.Kind == AnalyticalMemberKind.Column;
            var (lcs, x, y, z) = column ? ("y by vector", member.LocalY.X, member.LocalY.Y, member.LocalY.Z) : ("z by vector", 0.0, 0.0, 1.0);
            members.Add(member.Name, member.Kind switch
                {
                    AnalyticalMemberKind.Column => "Column",
                    AnalyticalMemberKind.Joist => "Beam",
                    AnalyticalMemberKind.GroundBeam => "Beam",
                    _ => "Beam"
                }, member.Section.Name, null, $"{member.Start.Name}; {member.End.Name}", "Line",
                member.Nodes.Count > 2 ? string.Join("; ", member.Nodes.Skip(1).SkipLast(1).Select(node => node.Name)) : null,
                Metres(member.Length), "Line", lcs, 0.0, Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6), member.SystemLine, 0.0, 0.0, 0.0, 0.0,
                member.Kind.ToString(), "Standard");
        }

        sheets.Add(members);

        var hinges = Sheet("RelConnectsStructuralMember", "Name", "Member", "Position", "ux", "uy", "uz", "fix", "fiy", "fiz");
        var hingeCount = 0;
        foreach (var member in model.Members.Where(member => member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist))
        {
            var position = (member.StartRelease, member.EndRelease) switch
            {
                (MemberRelease.Pinned, MemberRelease.Pinned) => "Both",
                (MemberRelease.Pinned, _) => "Begin",
                (_, MemberRelease.Pinned) => "End",
                _ => null
            };
            if (position is null) continue;
            hinges.Add($"H{++hingeCount}", member.Name, position, "Rigid", "Rigid", "Rigid", "Rigid", "Free", "Free");
        }

        sheets.Add(hinges);

        var panels = Sheet("StructuralSurfaceMember", "Name", "Type", "Material", "Thickness type", "Thickness [mm]", "System plane at", "Nodes", "Internal nodes", "Edges", "Area [m2]", "Layer",
            "LCS Type", "Coordinate X [m]", "Coordinate Y [m]", "Coordinate Z [m]", "LCS Rotation [deg]", "Analysis Z Eccentricity [mm]", "Shape", "Behavior in analysis");
        var openings = Sheet("StructuralSurfaceMemberOpening", "Name", "2D Member", "Nodes", "Edges", "Area [m2]");
        var openingCount = 0;
        foreach (var panel in model.Panels)
        {
            panels.Add(panel.Name, panel.Kind == AnalyticalPanelKind.Wall ? "Wall" : "Plate", panel.Material.Name, "Constant",
                Math.Round(panel.Thickness, 1).ToString(CultureInfo.InvariantCulture), panel.SystemPlane,
                string.Join("; ", panel.Outline.Select(node => node.Name)),
                panel.Inside.Count > 0 ? string.Join("; ", panel.Inside.Select(node => node.Name)) : null,
                string.Join("; ", panel.Outline.Select(_ => "Line")), Number(panel.Area / 1e6), panel.Kind.ToString(),
                "x by vector", Math.Round(panel.U.X, 6), Math.Round(panel.U.Y, 6), Math.Round(panel.U.Z, 6), 0.0, 0.0, "Flat", "Isotropic");
        }

        sheets.Add(panels);

        // An opening's corners as nodes of their own: SAF names an opening by its nodes.
        var cornerNodes = new List<(string Name, Point3D At)>();
        foreach (var panel in model.Panels)
        foreach (var opening in panel.Openings)
        {
            var names = new List<string>();
            foreach (var corner in opening)
            {
                var name = $"NO{cornerNodes.Count + 1}";
                cornerNodes.Add((name, corner));
                names.Add(name);
            }

            openings.Add($"O{++openingCount}", panel.Name, string.Join("; ", names), string.Join("; ", names.Select(_ => "Line")),
                Number(Polygon2D.Area(opening.Select(panel.Flat).ToList()) / 1e6));
        }

        foreach (var (name, at) in cornerNodes) nodes.Add(name, Metres(at.X), Metres(at.Y), Metres(at.Z));
        sheets.Add(openings);

        var pointSupports = Sheet("StructuralPointSupport", "Name", "Type", "Boundary condition", "Node", "ux", "uy", "uz", "fix", "fiy", "fiz");
        foreach (var support in model.Supports.Where(support => support.Node is not null))
        {
            var turning = support.Fixity == SupportFixity.Fixed ? "Rigid" : "Free";
            pointSupports.Add(support.Name, support.Fixity == SupportFixity.Fixed ? "Fixed" : "Hinged", "In node", support.Node!.Name, "Rigid", "Rigid", "Rigid", turning, turning, turning);
        }

        sheets.Add(pointSupports);

        var lineSupports = Sheet("StructuralEdgeConnection", "Name", "Type", "Boundary condition", "2D Member", "Edge", "ux", "uy", "uz", "fix", "fiy", "fiz",
            "Coordinate system", "Coordinate definition", "Origin", "Start point [m]", "End point [m]");
        foreach (var support in model.Supports.Where(support => support.IsLine))
        {
            var turning = support.Fixity == SupportFixity.Fixed ? "Rigid" : "Free";
            lineSupports.Add(support.Name, support.Fixity == SupportFixity.Fixed ? "Fixed" : "Hinged", "On edge", support.Panel!.Name, support.Panel.EdgeIndex(support.From!, support.To!) + 1,
                "Rigid", "Rigid", "Rigid", turning, turning, turning, "Global", "Relative", "From start", 0.0, 1.0);
        }

        sheets.Add(lineSupports);

        var ground = Sheet("StructuralSurfaceConnection", "Name", "2D Member", "Subsoil", "Description", "C1x [MN/m3]", "C1y [MN/m3]", "C1z Spring", "C1z [MN/m3]", "C2x [MN/m]", "C2y [MN/m]");
        var groundCount = 0;
        foreach (var panel in model.Panels.Where(panel => panel.Subgrade is not null))
            ground.Add($"SS{++groundCount}", panel.Name, info.Ground.ToString(), "Modulus of subgrade reaction, the middle of Bowles' range", panel.Subgrade, panel.Subgrade, "Linear", panel.Subgrade, 0.0, 0.0);
        sheets.Add(ground);

        // The loads: groups, cases, combinations.
        var groups = Sheet("StructuralLoadGroup", "Name", "Load group type", "Relation", "Load type");
        string GroupOf(LoadCase loadCase) => loadCase.Nature switch
        {
            LoadNature.SelfWeight or LoadNature.Dead => "LG1",
            LoadNature.Imposed => "LG2",
            LoadNature.RoofImposed => "LG3",
            LoadNature.Snow => "LG4",
            _ => "LG5"
        };
        var office = loading.SurfaceLoads.Concat<object>(loading.LineLoads).Any(load => load is SurfaceLoad { Case.Nature: LoadNature.Imposed } surface && surface.Said.StartsWith("office", StringComparison.Ordinal));
        foreach (var (name, type, relation, loadType, natures) in new[]
                 {
                     ("LG1", "Permanent", "Together", (string?)null, new[] { LoadNature.SelfWeight, LoadNature.Dead }),
                     ("LG2", "Variable", "Standard", office ? "Offices" : "Domestic", new[] { LoadNature.Imposed }),
                     ("LG3", "Variable", "Standard", "Roofs", new[] { LoadNature.RoofImposed }),
                     ("LG4", "Variable", "Standard", "Snow", new[] { LoadNature.Snow }),
                     ("LG5", "Variable", "Exclusive", "Wind", new[] { LoadNature.Wind })
                 })
            if (loading.Cases.Any(each => natures.Contains(each.Nature)))
                groups.Add(name, type, relation, loadType);
        sheets.Add(groups);

        var cases = Sheet("StructuralLoadCase", "Name", "Description", "Action type", "Load group", "Load type", "Duration");
        foreach (var loadCase in loading.Cases)
            cases.Add(loadCase.Name, loadCase.Description, loadCase.Permanent ? "Permanent" : loadCase.Accidental ? "Accidental" : "Variable", GroupOf(loadCase), loadCase.Nature switch
            {
                LoadNature.SelfWeight => "Self weight",
                LoadNature.Dead => "Standard",
                LoadNature.Imposed => "Static",
                LoadNature.RoofImposed => "Maintenance",
                LoadNature.Snow => "Snow",
                _ => "Wind"
            }, loadCase.Nature switch
            {
                LoadNature.Imposed => "Medium",
                LoadNature.RoofImposed or LoadNature.Snow => "Short",
                LoadNature.Wind => "Instantaneous",
                _ => null
            });
        sheets.Add(cases);

        var most = loading.Combinations.Select(combination => combination.Terms.Count).DefaultIfEmpty(1).Max();
        var combinationColumns = new List<string> { "Name", "Description", "Category", "National standard", "Type" };
        for (var i = 1; i <= most; i++) combinationColumns.AddRange(new[] { $"Load case name {i}", $"Load factor {i}", $"Multiplier {i}" });
        var combinations = Sheet("StructuralLoadCombination", combinationColumns.ToArray());
        foreach (var combination in loading.Combinations)
        {
            var row = new List<object?> { combination.Name, $"{combination.Description}: {combination.Said}", combination.State == LimitState.Ultimate ? "ULS" : "SLS", null, "Linear" };
            foreach (var (loadCase, factor) in combination.Terms) row.AddRange(new object?[] { loadCase.Name, Math.Round(factor, 4), 1.0 });
            combinations.Add(row.ToArray());
        }

        sheets.Add(combinations);

        var surfaceActions = Sheet("StructuralSurfaceAction", "Name", "Direction", "Type", "Force action", "Value [kN/m2]", "2D Member", "Load case", "Coordinate system", "Location");
        var actionCount = 0;
        // A drift, falling away over its plate, is written as its average over the plate: what it comes to in all, not where.
        foreach (var load in loading.SurfaceLoads)
        foreach (var (direction, value) in new[] { ("X", load.Force.X), ("Y", load.Force.Y), ("Z", load.Force.Z) })
            if (Math.Abs(value) >= 1e-6)
                surfaceActions.Add($"SA{++actionCount}", direction, load.Case.Nature switch { LoadNature.Wind => "Wind", LoadNature.Snow => "Snow", _ => "Standard" },
                    "On 2D member", Math.Round(load.Ramp is null ? value : value * load.LoadedArea / Math.Max(load.Panel.Area, 1e-9), 4), load.Panel.Name, load.Case.Name, "Global", "Length");
        sheets.Add(surfaceActions);

        var curveActions = Sheet("StructuralCurveAction", "Name", "Type", "Force action", "Distribution", "Direction", "Value 1 [kN/m]", "Member", "2D Member", "Edge", "Load case",
            "Coordinate system", "Location", "Coordinate definition", "Origin", "Extent", "Start point [m]", "End point [m]", "Eccentricity ey [mm]", "Eccentricity ez [mm]");
        actionCount = 0;
        foreach (var load in loading.LineLoads)
        {
            var length = load.Member is { } member ? Math.Sqrt(Math.Pow(member.End.At.X - member.Start.At.X, 2) + Math.Pow(member.End.At.Y - member.Start.At.Y, 2)) : AnalyticalModel.Distance(load.From!.At, load.To!.At);
            var whole = load.Start <= 1 && load.End >= length - 1;
            foreach (var (direction, value) in new[] { ("X", load.Force.X), ("Y", load.Force.Y), ("Z", load.Force.Z) })
            {
                if (Math.Abs(value) < 1e-6) continue;
                var onEdge = load.Member is null;
                curveActions.Add($"CA{++actionCount}", load.Case.Nature == LoadNature.Snow ? "Snow" : "Standard", onEdge ? "On edge" : "On beam", "Uniform", direction, Math.Round(value, 4),
                    load.Member?.Name, onEdge ? load.Panel!.Name : null, onEdge ? load.Panel!.EdgeIndex(load.From!, load.To!) + 1 : null, load.Case.Name,
                    "Global", "Length", "Absolute", "From start", whole ? "Full Span" : "Span", Metres(load.Start), Metres(load.End), 0.0, 0.0);
            }
        }

        sheets.Add(curveActions);
        return sheets;
    }
}
