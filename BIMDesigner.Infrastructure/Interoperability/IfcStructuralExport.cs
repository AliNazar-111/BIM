using System.Security.Cryptography;
using System.Text;
using BIMDesigner.Core.Architecture;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.MaterialResource;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.ProfileResource;
using Xbim.Ifc4.PropertyResource;
using Xbim.Ifc4.RepresentationResource;
using Xbim.Ifc4.StructuralAnalysisDomain;
using Xbim.Ifc4.StructuralLoadResource;
using Xbim.Ifc4.TopologyResource;

using CorePoint3D = BIMDesigner.Core.Geometry.Point3D;

namespace BIMDesigner.Infrastructure.Interoperability;

public static partial class IfcExport
{
    private sealed partial class Exporter
    {
        /// <summary>Whether the analytical model goes out with the building, as IFC4's structural analysis view has it.</summary>
        public bool Analytical { get; init; }

        private readonly Dictionary<AnalyticalNode, (IfcVertexPoint Vertex, IfcStructuralPointConnection Connection)> _nodes = new();
        private readonly Dictionary<string, IfcMaterial> _structuralMaterials = new();

        /// <summary>
        /// The analytical model as IFC4's structural analysis view has it: an IfcStructuralAnalysisModel
        /// serving the building, its members and connections assigned to it. Each node an
        /// IfcStructuralPointConnection at an IfcVertexPoint, held by an IfcBoundaryNodeCondition where a
        /// foundation holds it; each member, between each pair of nodes along it, an
        /// IfcStructuralCurveMember along an IfcEdge with its profile and material; each plate an
        /// IfcStructuralSurfaceMember on an IfcFaceSurface bounded by the edges between its nodes, its
        /// openings cut out of it; each joined to the nodes it meets by IfcRelConnectsStructuralMember -
        /// a pinned end of a beam released in turning. A strip footing is an IfcStructuralCurveConnection
        /// along the foot of its wall, the ground under a raft an IfcStructuralSurfaceConnection of its
        /// stiffness. Each is assigned to the element it is drawn from (IfcRelAssignsToProduct). Each load
        /// case is an IfcStructuralLoadCase - the first its self weight, by its coefficients - its loads
        /// IfcStructuralPlanarActions and IfcStructuralLinearActions connected to what they are on; each
        /// combination an IfcStructuralLoadGroup assigning its cases by their factors
        /// (IfcRelAssignsToGroupByFactor). Forces are in kN, kN/m and kN/m².
        /// </summary>
        private void ExportAnalytical(IfcBuilding building)
        {
            var model = AnalyticalModel.Of(_document);
            if (model.Members.Count == 0 && model.Panels.Count == 0) return;
            var loading = StructuralLoading.Of(_document, model);

            var analysis = New<IfcStructuralAnalysisModel>(each =>
            {
                each.GlobalId = Seeded("analysis model").ToIfc();
                each.Name = $"{_document.ProjectInformation.Name} - analytical model";
                each.Description = _document.ProjectInformation.ClearanceStandard.IsUs() ? "ASCE 7-16" : "BS EN 1990, BS EN 1991 and their UK National Annexes";
                each.PredefinedType = IfcAnalysisModelTypeEnum.LOADING_3D;
                each.SharedPlacement = _origin;
            });
            New<IfcRelServicesBuildings>(relation =>
            {
                relation.RelatingSystem = analysis;
                relation.RelatedBuildings.Add(building);
            });

            var items = new List<IfcProduct>();
            foreach (var node in model.Nodes) items.Add(NodeOf(node).Connection);

            // Each node a foundation holds.
            foreach (var support in model.Supports.Where(support => support.Node is not null))
                _nodes[support.Node!].Connection.AppliedCondition = NodeCondition($"{support.Name} {support.Fixity}", true, support.Fixity == SupportFixity.Fixed);

            var memberOf = new Dictionary<AnalyticalMember, List<IfcStructuralCurveMember>>();
            foreach (var member in model.Members)
            {
                var segments = new List<IfcStructuralCurveMember>();
                for (var i = 0; i < member.Nodes.Count - 1; i++)
                {
                    var (from, to) = (member.Nodes[i], member.Nodes[i + 1]);
                    var x = Unit(Minus(to.At, from.At));
                    var z = Cross(x, (member.LocalY.X, member.LocalY.Y, member.LocalY.Z));
                    var name = member.Nodes.Count > 2 ? $"{member.Name}.{i + 1}" : member.Name;
                    var segment = New<IfcStructuralCurveMember>(each =>
                    {
                        each.GlobalId = Seeded($"member {name}", member.Element.Id).ToIfc();
                        each.Name = name;
                        each.Description = $"{member.Section.Name}, {member.Section.Material.Quality}";
                        each.ObjectType = member.Kind.ToString();
                        each.PredefinedType = member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist && member.StartRelease == MemberRelease.Pinned && member.EndRelease == MemberRelease.Pinned && member.Nodes.Count == 2
                            ? IfcStructuralCurveMemberTypeEnum.PIN_JOINED_MEMBER
                            : IfcStructuralCurveMemberTypeEnum.RIGID_JOINED_MEMBER;
                        each.Axis = Direction(z.X, z.Y, z.Z);
                        each.ObjectPlacement = _origin;
                        each.Representation = Topology("Edge", Edge(from, to));
                    });
                    segments.Add(segment);

                    // Joined to its nodes: a pinned end of a beam free to turn there.
                    Join(segment, from, i == 0 && member.StartRelease == MemberRelease.Pinned && member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist);
                    Join(segment, to, i == member.Nodes.Count - 2 && member.EndRelease == MemberRelease.Pinned && member.Kind is AnalyticalMemberKind.Beam or AnalyticalMemberKind.Joist);
                }

                memberOf[member] = segments;
                items.AddRange(segments);
                New<IfcRelAssociatesMaterial>(relation =>
                {
                    relation.RelatingMaterial = New<IfcMaterialProfileSet>(set =>
                    {
                        set.Name = member.Section.Name;
                        set.MaterialProfiles.Add(New<IfcMaterialProfile>(profile =>
                        {
                            profile.Name = member.Section.Name;
                            profile.Material = StructuralMaterial(member.Section.Material);
                            profile.Profile = ProfileOf(member.Section);
                        }));
                    });
                    relation.RelatedObjects.AddRange(segments);
                });
                Link(member.Element.Id, segments);
            }

            var panelOf = new Dictionary<AnalyticalPanel, (IfcStructuralSurfaceMember Member, IfcProductDefinitionShape Shape)>();
            foreach (var panel in model.Panels)
            {
                var face = Face(panel);
                var surface = New<IfcStructuralSurfaceMember>(each =>
                {
                    each.GlobalId = Seeded($"panel {panel.Name}", panel.Element.Id).ToIfc();
                    each.Name = panel.Name;
                    each.Description = $"{panel.Thickness:0} mm of {panel.Material.Quality}, its plane at its {panel.SystemPlane.ToLowerInvariant()}";
                    each.ObjectType = panel.Kind.ToString();
                    each.PredefinedType = IfcStructuralSurfaceMemberTypeEnum.SHELL;
                    each.Thickness = Math.Max(1, panel.Thickness);
                    each.ObjectPlacement = _origin;
                    each.Representation = face;
                });
                panelOf[panel] = (surface, face);
                items.Add(surface);
                foreach (var node in panel.Outline.Concat(panel.Inside).Distinct()) Join(surface, node, false);
                New<IfcRelAssociatesMaterial>(relation =>
                {
                    relation.RelatingMaterial = StructuralMaterial(panel.Material);
                    relation.RelatedObjects.Add(surface);
                });
                Link(panel.Element.Id, new[] { surface });

                // A plate on the ground held by its stiffness.
                if (panel.Subgrade is { } stiffness)
                {
                    var ground = New<IfcStructuralSurfaceConnection>(each =>
                    {
                        each.GlobalId = Seeded($"ground under {panel.Name}", panel.Element.Id).ToIfc();
                        each.Name = $"Ground under {panel.Name}";
                        each.ObjectPlacement = _origin;
                        each.Representation = face;
                        each.AppliedCondition = New<IfcBoundaryFaceCondition>(condition =>
                        {
                            condition.Name = $"{_document.ProjectInformation.Ground}, {stiffness:0} MN/m³";
                            condition.TranslationalStiffnessByAreaX = new IfcBoolean(false);
                            condition.TranslationalStiffnessByAreaY = new IfcBoolean(false);
                            condition.TranslationalStiffnessByAreaZ = new IfcModulusOfSubgradeReactionMeasure(stiffness * 1000);
                        });
                    });
                    items.Add(ground);
                    New<IfcRelConnectsStructuralMember>(relation =>
                    {
                        relation.RelatingStructuralMember = surface;
                        relation.RelatedStructuralConnection = ground;
                    });
                }
            }

            // Walls held along their foot.
            foreach (var support in model.Supports.Where(support => support.IsLine))
            {
                var (from, to) = (support.From!, support.To!);
                var along = Unit(Minus(to.At, from.At));
                var line = New<IfcStructuralCurveConnection>(each =>
                {
                    each.GlobalId = Seeded($"support {support.Name}", support.Foundation.Id).ToIfc();
                    each.Name = support.Name;
                    each.Axis = Direction(-along.Y, along.X, 0);
                    each.ObjectPlacement = _origin;
                    each.Representation = Topology("Edge", Edge(from, to));
                    each.AppliedCondition = New<IfcBoundaryEdgeCondition>(condition =>
                    {
                        condition.Name = $"{support.Name} {support.Fixity}";
                        condition.TranslationalStiffnessByLengthX = new IfcBoolean(true);
                        condition.TranslationalStiffnessByLengthY = new IfcBoolean(true);
                        condition.TranslationalStiffnessByLengthZ = new IfcBoolean(true);
                        condition.RotationalStiffnessByLengthX = new IfcBoolean(support.Fixity == SupportFixity.Fixed);
                        condition.RotationalStiffnessByLengthY = new IfcBoolean(support.Fixity == SupportFixity.Fixed);
                        condition.RotationalStiffnessByLengthZ = new IfcBoolean(support.Fixity == SupportFixity.Fixed);
                    });
                });
                items.Add(line);
                New<IfcRelConnectsStructuralMember>(relation =>
                {
                    relation.RelatingStructuralMember = panelOf[support.Panel!].Member;
                    relation.RelatedStructuralConnection = line;
                });
                Link(support.Foundation.Id, new[] { line });
            }

            foreach (var support in model.Supports.Where(support => support.Node is not null))
                Link(support.Foundation.Id, new[] { _nodes[support.Node!].Connection });

            New<IfcRelAssignsToGroup>(relation =>
            {
                relation.RelatingGroup = analysis;
                relation.RelatedObjects.AddRange(items);
            });

            // The load cases and what is in them.
            var cases = new Dictionary<LoadCase, IfcStructuralLoadCase>();
            foreach (var loadCase in loading.Cases)
            {
                var group = New<IfcStructuralLoadCase>(each =>
                {
                    each.GlobalId = Seeded($"case {loadCase.Name}").ToIfc();
                    each.Name = loadCase.Name;
                    each.Description = loadCase.Description;
                    each.PredefinedType = IfcLoadGroupTypeEnum.LOAD_CASE;
                    each.ActionType = loadCase.Permanent ? IfcActionTypeEnum.PERMANENT_G : IfcActionTypeEnum.VARIABLE_Q;
                    each.ActionSource = loadCase.Nature switch
                    {
                        LoadNature.SelfWeight => IfcActionSourceTypeEnum.DEAD_LOAD_G,
                        LoadNature.Dead => IfcActionSourceTypeEnum.COMPLETION_G1,
                        LoadNature.Snow => IfcActionSourceTypeEnum.SNOW_S,
                        LoadNature.Wind => IfcActionSourceTypeEnum.WIND_W,
                        _ => IfcActionSourceTypeEnum.LIVE_LOAD_Q
                    };
                    each.Coefficient = 1.0;
                    if (loadCase.Nature == LoadNature.SelfWeight)
                    {
                        each.SelfWeightCoefficients.Add(0.0);
                        each.SelfWeightCoefficients.Add(0.0);
                        each.SelfWeightCoefficients.Add(-1.0);
                    }
                });
                analysis.LoadedBy.Add(group);
                cases[loadCase] = group;

                var actions = new List<IfcProduct>();
                var index = 0;
                foreach (var load in loading.SurfaceLoads.Where(load => load.Case == loadCase))
                {
                    var (member, shape) = panelOf[load.Panel];
                    var action = New<IfcStructuralPlanarAction>(each =>
                    {
                        each.GlobalId = Seeded($"action {loadCase.Name} {++index} {load.Panel.Name}").ToIfc();
                        each.Name = $"{loadCase.Name} on {load.Panel.Name}";
                        each.Description = load.Said;
                        each.ObjectPlacement = _origin;
                        each.Representation = shape;
                        each.GlobalOrLocal = IfcGlobalOrLocalEnum.GLOBAL_COORDS;
                        each.ProjectedOrTrue = IfcProjectedOrTrueLengthEnum.TRUE_LENGTH;
                        each.PredefinedType = IfcStructuralSurfaceActivityTypeEnum.CONST;
                        each.AppliedLoad = New<IfcStructuralLoadPlanarForce>(force =>
                        {
                            force.Name = load.Said.Length > 60 ? load.Said[..60] : load.Said;
                            force.PlanarForceX = load.Force.X;
                            force.PlanarForceY = load.Force.Y;
                            force.PlanarForceZ = load.Force.Z;
                        });
                    });
                    actions.Add(action);
                    New<IfcRelConnectsStructuralActivity>(relation =>
                    {
                        relation.RelatingElement = member;
                        relation.RelatedStructuralActivity = action;
                    });
                }

                foreach (var load in loading.LineLoads.Where(load => load.Case == loadCase))
                {
                    // Along a member's line, or a plate's edge, over the stretch it is on.
                    var (a, b) = load.Member is { } on ? (on.Start.At, on.End.At) : (load.From!.At, load.To!.At);
                    var length = AnalyticalModel.Distance(a, b);
                    if (length < 1) continue;
                    CorePoint3D At(double distance) => new(a.X + (b.X - a.X) * distance / length, a.Y + (b.Y - a.Y) * distance / length, a.Z + (b.Z - a.Z) * distance / length);
                    var (start, end) = (At(load.Start), At(load.End));
                    IfcStructuralItem target = load.Member is { } loaded
                        ? memberOf[loaded][0]
                        : panelOf[load.Panel!].Member;
                    if (load.Member is { } split && memberOf[split].Count > 1)
                    {
                        // On a member split at its nodes: the piece its middle is on.
                        var middle = (load.Start + load.End) / 2;
                        var passed = 0.0;
                        for (var i = 0; i < split.Nodes.Count - 1; i++)
                        {
                            passed += AnalyticalModel.Distance(split.Nodes[i].At, split.Nodes[i + 1].At);
                            if (middle > passed && i < split.Nodes.Count - 2) continue;
                            target = memberOf[split][i];
                            break;
                        }
                    }

                    var action = New<IfcStructuralLinearAction>(each =>
                    {
                        each.GlobalId = Seeded($"action {loadCase.Name} {++index} line").ToIfc();
                        each.Name = $"{loadCase.Name} on {(load.Member?.Name ?? load.Panel!.Name)}";
                        each.Description = load.Said;
                        each.ObjectPlacement = _origin;
                        each.Representation = Topology("Edge", New<IfcEdge>(edge =>
                        {
                            edge.EdgeStart = New<IfcVertexPoint>(vertex => vertex.VertexGeometry = Point3D(start.X, start.Y, start.Z));
                            edge.EdgeEnd = New<IfcVertexPoint>(vertex => vertex.VertexGeometry = Point3D(end.X, end.Y, end.Z));
                        }));
                        each.GlobalOrLocal = IfcGlobalOrLocalEnum.GLOBAL_COORDS;
                        each.ProjectedOrTrue = IfcProjectedOrTrueLengthEnum.TRUE_LENGTH;
                        each.PredefinedType = IfcStructuralCurveActivityTypeEnum.CONST;
                        each.AppliedLoad = New<IfcStructuralLoadLinearForce>(force =>
                        {
                            force.Name = load.Said.Length > 60 ? load.Said[..60] : load.Said;
                            force.LinearForceX = load.Force.X;
                            force.LinearForceY = load.Force.Y;
                            force.LinearForceZ = load.Force.Z;
                        });
                    });
                    actions.Add(action);
                    New<IfcRelConnectsStructuralActivity>(relation =>
                    {
                        relation.RelatingElement = target;
                        relation.RelatedStructuralActivity = action;
                    });
                }

                if (actions.Count > 0)
                    New<IfcRelAssignsToGroup>(relation =>
                    {
                        relation.RelatingGroup = group;
                        relation.RelatedObjects.AddRange(actions);
                    });
            }

            // The combinations, each case by its factor.
            foreach (var combination in loading.Combinations)
            {
                var group = New<IfcStructuralLoadGroup>(each =>
                {
                    each.GlobalId = Seeded($"combination {combination.Name}").ToIfc();
                    each.Name = combination.Name;
                    each.Description = $"{combination.Description}: {combination.Said}";
                    each.PredefinedType = IfcLoadGroupTypeEnum.LOAD_COMBINATION;
                    each.ActionType = IfcActionTypeEnum.NOTDEFINED;
                    each.ActionSource = IfcActionSourceTypeEnum.NOTDEFINED;
                    each.Purpose = combination.State == LimitState.Ultimate ? "ULS" : "SLS";
                });
                analysis.LoadedBy.Add(group);
                foreach (var (loadCase, factor) in combination.Terms)
                    New<IfcRelAssignsToGroupByFactor>(relation =>
                    {
                        relation.RelatingGroup = group;
                        relation.RelatedObjects.Add(cases[loadCase]);
                        relation.Factor = factor;
                    });
            }
        }

        /// <summary>A node's vertex and the connection at it, made once.</summary>
        private (IfcVertexPoint Vertex, IfcStructuralPointConnection Connection) NodeOf(AnalyticalNode node)
        {
            if (_nodes.TryGetValue(node, out var made)) return made;
            var vertex = New<IfcVertexPoint>(each => each.VertexGeometry = Point3D(node.At.X, node.At.Y, node.At.Z));
            var connection = New<IfcStructuralPointConnection>(each =>
            {
                each.GlobalId = Seeded($"node {node.Name}").ToIfc();
                each.Name = node.Name;
                each.ObjectPlacement = _origin;
                each.Representation = Topology("Vertex", vertex);
            });
            return _nodes[node] = (vertex, connection);
        }

        private IfcEdge Edge(AnalyticalNode from, AnalyticalNode to) => New<IfcEdge>(edge =>
        {
            edge.EdgeStart = NodeOf(from).Vertex;
            edge.EdgeEnd = NodeOf(to).Vertex;
        });

        private void Join(IfcStructuralMember member, AnalyticalNode node, bool pinned) =>
            New<IfcRelConnectsStructuralMember>(relation =>
            {
                relation.RelatingStructuralMember = member;
                relation.RelatedStructuralConnection = NodeOf(node).Connection;
                if (pinned) relation.AppliedCondition = NodeCondition("Pinned", true, false);
            });

        private IfcBoundaryNodeCondition NodeCondition(string name, bool held, bool turning) => New<IfcBoundaryNodeCondition>(condition =>
        {
            condition.Name = name;
            condition.TranslationalStiffnessX = new IfcBoolean(held);
            condition.TranslationalStiffnessY = new IfcBoolean(held);
            condition.TranslationalStiffnessZ = new IfcBoolean(held);
            condition.RotationalStiffnessX = new IfcBoolean(turning);
            condition.RotationalStiffnessY = new IfcBoolean(turning);
            condition.RotationalStiffnessZ = new IfcBoolean(turning);
        });

        private IfcProductDefinitionShape Topology(string type, IfcRepresentationItem item) => New<IfcProductDefinitionShape>(shape =>
            shape.Representations.Add(New<IfcTopologyRepresentation>(representation =>
            {
                representation.ContextOfItems = _context;
                representation.RepresentationIdentifier = "Reference";
                representation.RepresentationType = type;
                representation.Items.Add(item);
            })));

        /// <summary>A plate's face: bounded by the edges between its nodes, its openings inner bounds, on its plane.</summary>
        private IfcProductDefinitionShape Face(AnalyticalPanel panel)
        {
            var face = New<IfcFaceSurface>(each =>
            {
                each.Bounds.Add(New<IfcFaceOuterBound>(bound =>
                {
                    bound.Orientation = true;
                    bound.Bound = New<IfcEdgeLoop>(loop =>
                    {
                        foreach (var (from, to) in panel.Edges)
                            loop.EdgeList.Add(New<IfcOrientedEdge>(oriented =>
                            {
                                oriented.EdgeElement = Edge(from, to);
                                oriented.Orientation = true;
                            }));
                    });
                }));
                foreach (var opening in panel.Openings)
                    each.Bounds.Add(New<IfcFaceBound>(bound =>
                    {
                        bound.Orientation = false;
                        bound.Bound = New<IfcPolyLoop>(loop =>
                        {
                            foreach (var point in opening) loop.Polygon.Add(Point3D(point.X, point.Y, point.Z));
                        });
                    }));
                each.FaceSurface = New<IfcPlane>(plane => plane.Position = New<IfcAxis2Placement3D>(axis =>
                {
                    axis.Location = Point3D(panel.Origin.X, panel.Origin.Y, panel.Origin.Z);
                    axis.Axis = Direction(panel.Normal.X, panel.Normal.Y, panel.Normal.Z);
                    axis.RefDirection = Direction(panel.U.X, panel.U.Y, panel.U.Z);
                }));
                each.SameSense = true;
            });
            return Topology("Face", face);
        }

        /// <summary>A structural material, with its mechanical properties: made once by its name.</summary>
        private IfcMaterial StructuralMaterial(AnalyticalMaterial material)
        {
            if (_structuralMaterials.TryGetValue(material.Name, out var made)) return made;
            made = New<IfcMaterial>(each =>
            {
                each.Name = $"{material.Name} ({material.Quality})";
                each.Category = material.Kind.ToString();
            });
            var properties = new List<IfcProperty>
            {
                New<IfcPropertySingleValue>(p => { p.Name = "YoungModulus"; p.NominalValue = new IfcModulusOfElasticityMeasure(material.Modulus * 1000); })
            };
            if (double.IsFinite(material.ShearModulus))
                properties.Add(New<IfcPropertySingleValue>(p => { p.Name = "ShearModulus"; p.NominalValue = new IfcModulusOfElasticityMeasure(material.ShearModulus * 1000); }));
            if (double.IsFinite(material.Poisson))
                properties.Add(New<IfcPropertySingleValue>(p => { p.Name = "PoissonRatio"; p.NominalValue = new IfcPositiveRatioMeasure(material.Poisson); }));
            if (double.IsFinite(material.Expansion))
                properties.Add(New<IfcPropertySingleValue>(p => { p.Name = "ThermalExpansionCoefficient"; p.NominalValue = new IfcThermalExpansionCoefficientMeasure(material.Expansion); }));
            New<IfcMaterialProperties>(set =>
            {
                set.Name = "Pset_MaterialMechanical";
                set.Material = made;
                set.Properties.AddRange(properties);
            });
            New<IfcMaterialProperties>(set =>
            {
                set.Name = "Pset_MaterialCommon";
                set.Material = made;
                set.Properties.Add(New<IfcPropertySingleValue>(p => { p.Name = "MassDensity"; p.NominalValue = new IfcMassDensityMeasure(material.Density); }));
            });
            return _structuralMaterials[material.Name] = made;
        }

        private IfcProfileDef ProfileOf(AnalyticalSection section)
        {
            var d = section.Dimensions;
            return section.Shape switch
            {
                "I Section" => New<IfcIShapeProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.ProfileName = section.Name;
                    p.OverallDepth = d[0];
                    p.OverallWidth = d[1];
                    p.FlangeThickness = d[3];
                    p.WebThickness = d[5];
                }),
                "Circle" => New<IfcCircleProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.ProfileName = section.Name;
                    p.Radius = d[0] / 2;
                }),
                "Pipe" => New<IfcCircleHollowProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.ProfileName = section.Name;
                    p.Radius = d[0] / 2;
                    p.WallThickness = d[1];
                }),
                "Tube" => New<IfcRectangleHollowProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.ProfileName = section.Name;
                    p.YDim = d[0];
                    p.XDim = d[1];
                    p.WallThickness = d[2];
                    p.OuterFilletRadius = d[3];
                    p.InnerFilletRadius = d[4];
                }),
                _ => New<IfcRectangleProfileDef>(p =>
                {
                    p.ProfileType = IfcProfileTypeEnum.AREA;
                    p.ProfileName = section.Name;
                    p.YDim = d[0];
                    p.XDim = d[1];
                })
            };
        }

        /// <summary>What the analytical items are drawn from, where it went out.</summary>
        private void Link(Guid element, IEnumerable<IfcProduct> items)
        {
            if (!_products.TryGetValue(element, out var product)) return;
            var list = items.ToList();
            if (list.Count == 0) return;
            New<IfcRelAssignsToProduct>(relation =>
            {
                relation.RelatingProduct = product;
                relation.RelatedObjects.AddRange(list);
            });
        }

        /// <summary>The units loads are given in - kN, kN/m, kN/m², kN/m³ - and materials' moduli, kN/m², and densities, kg/m³.</summary>
        private void AnalyticalUnits(IfcUnitAssignment assignment)
        {
            var kilonewton = SiUnit(IfcUnitEnum.FORCEUNIT, IfcSIUnitName.NEWTON, IfcSIPrefix.KILO);
            var metre = SiUnit(IfcUnitEnum.LENGTHUNIT, IfcSIUnitName.METRE);
            var kilogram = SiUnit(IfcUnitEnum.MASSUNIT, IfcSIUnitName.GRAM, IfcSIPrefix.KILO);
            assignment.Units.Add(kilonewton);
            assignment.Units.Add(kilogram);
            IfcDerivedUnit Per(IfcDerivedUnitEnum kind, IfcNamedUnit over, int power) => New<IfcDerivedUnit>(unit =>
            {
                unit.UnitType = kind;
                unit.Elements.Add(New<IfcDerivedUnitElement>(element => { element.Unit = over; element.Exponent = 1; }));
                unit.Elements.Add(New<IfcDerivedUnitElement>(element => { element.Unit = metre; element.Exponent = -power; }));
            });
            assignment.Units.Add(Per(IfcDerivedUnitEnum.LINEARFORCEUNIT, kilonewton, 1));
            assignment.Units.Add(Per(IfcDerivedUnitEnum.PLANARFORCEUNIT, kilonewton, 2));
            assignment.Units.Add(Per(IfcDerivedUnitEnum.MODULUSOFSUBGRADEREACTIONUNIT, kilonewton, 3));
            assignment.Units.Add(Per(IfcDerivedUnitEnum.MODULUSOFELASTICITYUNIT, kilonewton, 2));
            assignment.Units.Add(Per(IfcDerivedUnitEnum.MASSDENSITYUNIT, kilogram, 3));
        }

        /// <summary>A GlobalId the same each time the same model goes out: from the project's own and a name.</summary>
        private Guid Seeded(string name, Guid? element = null)
        {
            var seed = $"{element ?? _document.Levels.FirstOrDefault()?.Id ?? Guid.Empty}/{name}";
            return new Guid(MD5.HashData(Encoding.UTF8.GetBytes(seed)));
        }

        private static (double X, double Y, double Z) Minus(CorePoint3D a, CorePoint3D b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        private static (double X, double Y, double Z) Unit((double X, double Y, double Z) v)
        {
            var length = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            return length < 1e-12 ? (0, 0, 1) : (v.X / length, v.Y / length, v.Z / length);
        }

        private static (double X, double Y, double Z) Cross((double X, double Y, double Z) a, (double X, double Y, double Z) b)
        {
            var c = (X: a.Y * b.Z - a.Z * b.Y, Y: a.Z * b.X - a.X * b.Z, Z: a.X * b.Y - a.Y * b.X);
            return Math.Sqrt(c.X * c.X + c.Y * c.Y + c.Z * c.Z) < 1e-9 ? (0, 0, 1) : Unit(c);
        }
    }
}
