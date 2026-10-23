using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Mep;
using Xbim.Ifc4.GeometricConstraintResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
using Xbim.Ifc4.MeasureResource;
using Xbim.Ifc4.Kernel;
using Xbim.Ifc4.ProductExtension;
using Xbim.Ifc4.SharedBldgServiceElements;

namespace BIMDesigner.Infrastructure.Interoperability;

public static partial class IfcExport
{
    private sealed partial class Exporter
    {
        /// <summary>
        /// The services as IFC4 has them. Each fitting's connectors as IfcDistributionPorts nested
        /// in it (IfcRelNests), placed in its frame with their axis pointing the way what is
        /// carried flows - out of it at a source, into it at a sink - and typed by what they carry.
        /// Each system as an IfcDistributionSystem of its kind serving the building
        /// (IfcRelServicesBuildings), its fittings assigned to it (IfcRelAssignsToGroup) and the
        /// equipment feeding it hosting it at its port (IfcRelAssignsToProduct); and each circuit as
        /// an IfcDistributionCircuit, aggregated into the power system of the consumer unit it is on.
        /// </summary>
        private void ExportSystems(IfcBuilding building)
        {
            var ports = new Dictionary<(Guid Component, SystemKind Kind, bool Feeds), IfcDistributionPort>();
            var placedPorts = new List<(BIMDesigner.Core.Geometry.Point3D At, IfcDistributionPort Port, Guid Owner)>();
            foreach (var component in _document.All<Component>())
            {
                if (!_products.TryGetValue(component.Id, out var product) || product is not IfcDistributionElement element) continue;
                var placed = Connectors.Placed(_document, component);
                if (placed.Count == 0) continue;

                var nested = new List<IfcDistributionPort>();
                for (var i = 0; i < placed.Count; i++)
                {
                    var port = Port(component, element, placed[i], i);
                    nested.Add(port);
                    placedPorts.Add((placed[i].At, port, component.Id));
                    ports.TryAdd((component.Id, placed[i].Connector.Kind, placed[i].Connector.Feeds), port);
                }

                New<IfcRelNests>(relation =>
                {
                    relation.GlobalId = PartId(component.Id, 0x9E, 0x51).ToIfc();
                    relation.RelatingObject = element;
                    relation.RelatedObjects.AddRange(nested);
                });
            }

            // The pipes: each a segment with a port at each end, joined port to port where they meet a fitting or each other.
            var segments = new Dictionary<Guid, IfcProduct>();
            foreach (var pipe in _document.All<Pipe>())
            {
                if (ExportPipe(pipe) is not { } segment) continue;
                segments[pipe.Id] = segment;
                var (start, end) = pipe.Ends(_document);
                var ends = new[] { (start, "Start", 0), (end, "End", 1) }.Select(entry =>
                {
                    var port = New<IfcDistributionPort>(each =>
                    {
                        each.GlobalId = PartId(pipe.Id, (byte)(0x70 + entry.Item3), 0xD1).ToIfc();
                        each.Name = entry.Item2;
                        each.FlowDirection = IfcFlowDirectionEnum.SOURCEANDSINK;
                        each.PredefinedType = IfcDistributionPortTypeEnum.PIPE;
                        each.SystemType = SystemType(pipe.Kind);
                        each.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                        {
                            placement.PlacementRelTo = segment.ObjectPlacement;
                            placement.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                                axis.Location = Point3D(entry.Item1.X, entry.Item1.Y, entry.Item1.Z - (_document.FindLevel(pipe.LevelId)?.Elevation ?? 0)));
                        });
                    });
                    placedPorts.Add((entry.Item1, port, pipe.Id));
                    return port;
                }).ToList();
                New<IfcRelNests>(relation =>
                {
                    relation.GlobalId = PartId(pipe.Id, 0x9E, 0x52).ToIfc();
                    relation.RelatingObject = segment;
                    relation.RelatedObjects.AddRange(ends);
                });
            }

            // The ducts likewise, each a segment with a port at each end - and a damper where one keeps a fire wall it passes through.
            var ducts = new HashSet<Guid>();
            foreach (var duct in _document.All<Duct>())
            {
                if (ExportDuct(duct) is not { } segment) continue;
                segments[duct.Id] = segment;
                ducts.Add(duct.Id);
                var (start, end) = duct.Ends(_document);
                var ends = new[] { (start, "Start", 0), (end, "End", 1) }.Select(entry =>
                {
                    var port = New<IfcDistributionPort>(each =>
                    {
                        each.GlobalId = PartId(duct.Id, (byte)(0x70 + entry.Item3), 0xD2).ToIfc();
                        each.Name = entry.Item2;
                        each.FlowDirection = IfcFlowDirectionEnum.SOURCEANDSINK;
                        each.PredefinedType = IfcDistributionPortTypeEnum.DUCT;
                        each.SystemType = SystemType(duct.Kind);
                        each.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                        {
                            placement.PlacementRelTo = segment.ObjectPlacement;
                            placement.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                                axis.Location = Point3D(entry.Item1.X, entry.Item1.Y, entry.Item1.Z - (_document.FindLevel(duct.LevelId)?.Elevation ?? 0)));
                        });
                    });
                    placedPorts.Add((entry.Item1, port, duct.Id));
                    return port;
                }).ToList();
                New<IfcRelNests>(relation =>
                {
                    relation.GlobalId = PartId(duct.Id, 0x9E, 0x53).ToIfc();
                    relation.RelatingObject = segment;
                    relation.RelatedObjects.AddRange(ends);
                });

                if (duct.FireProtection is FireProtection.FireDamper or FireProtection.FireAndSmokeDamper)
                {
                    var index = 0;
                    foreach (var (wall, at) in DuctNetworks.Crossings(_document, duct).Where(crossing => _document.GetWallType(crossing.Wall)?.Separating == true))
                        ExportDamper(duct, at, Math.Max(_document.GetWallType(wall)?.Width ?? 100, 50), null, index++);
                    foreach (var (floor, at) in DuctNetworks.FloorCrossings(_document, duct))
                        ExportDamper(duct, at, 100, (floor.GetTopElevation(_document) + floor.GetBottomElevation(_document)) / 2 - (_document.FindLevel(duct.LevelId)?.Elevation ?? 0), index++);
                }
            }

            var joined = new HashSet<(IfcDistributionPort, IfcDistributionPort)>();
            foreach (var (at, port, owner) in placedPorts.Where(entry => segments.ContainsKey(entry.Owner)))
                foreach (var (otherAt, other, otherOwner) in placedPorts)
                {
                    if (otherOwner == owner || ReferenceEquals(port, other) || joined.Contains((other, port))) continue;
                    var touch = ducts.Contains(owner) ? DuctNetworks.Touch : PipeNetworks.Touch;
                    if (Math.Sqrt(Math.Pow(at.X - otherAt.X, 2) + Math.Pow(at.Y - otherAt.Y, 2) + Math.Pow(at.Z - otherAt.Z, 2)) > touch) continue;
                    if (other.SystemType != port.SystemType) continue;
                    joined.Add((port, other));
                    var (relating, related) = other.FlowDirection == IfcFlowDirectionEnum.SOURCE ? (other, port) : (port, other);
                    New<IfcRelConnectsPorts>(relation =>
                    {
                        relation.RelatingPort = relating;
                        relation.RelatedPort = related;
                    });
                }

            var systems = _document.All<MepSystem>().ToList();
            if (systems.Count == 0) return;

            var groups = new List<IfcSystem>();
            foreach (var system in systems)
            {
                var members = MepSystems.MembersOf(_document, system)
                    .Select(member => _products.TryGetValue(member.Id, out var product) ? product : null).OfType<IfcProduct>()
                    .Concat(PipeNetworks.PipesOf(_document, system).Select(pipe => segments.GetValueOrDefault(pipe.Id)).OfType<IfcProduct>())
                    .ToList();
                IfcDistributionSystem group = system.Kind == SystemKind.Power ? New<IfcDistributionCircuit>() : New<IfcDistributionSystem>();
                group.GlobalId = system.Id.ToIfc();
                group.Name = system.Name;
                group.LongName = system.Kind == SystemKind.Power ? $"{system.Use.Title()} circuit" : system.Kind.Title();
                group.PredefinedType = PredefinedType(system);
                groups.Add(group);

                if (members.Count > 0)
                    New<IfcRelAssignsToGroup>(relation =>
                    {
                        relation.RelatingGroup = group;
                        relation.RelatedObjects.AddRange(members);
                    });

                // The equipment feeding it hosts it at the port it leaves by.
                if (system.SourceId is { } source && ports.TryGetValue((source, system.Kind, true), out var feeding))
                    New<IfcRelAssignsToProduct>(relation =>
                    {
                        relation.RelatingProduct = feeding;
                        relation.RelatedObjects.Add(group);
                    });
            }

            // Each consumer unit's circuits make up its power system.
            foreach (var board in systems.Where(system => system.Kind == SystemKind.Power && system.SourceId is not null).GroupBy(system => system.SourceId!.Value))
            {
                var circuits = board.Select(circuit => groups[systems.IndexOf(circuit)]).ToList();
                var name = _document.Find<Component>(board.Key) is { } unit ? _document.FindType<ComponentType>(unit.TypeId)?.Name ?? "Consumer unit" : "Consumer unit";
                var supply = New<IfcDistributionSystem>(power =>
                {
                    power.GlobalId = PartId(board.Key, 0xE1, 0xEC).ToIfc();
                    power.Name = $"{name} supply";
                    power.LongName = "Power";
                    power.PredefinedType = IfcDistributionSystemEnum.ELECTRICAL;
                });
                groups.Add(supply);
                New<IfcRelAggregates>(relation =>
                {
                    relation.RelatingObject = supply;
                    relation.RelatedObjects.AddRange(circuits);
                });
            }

            foreach (var group in groups)
                New<IfcRelServicesBuildings>(relation =>
                {
                    relation.RelatingSystem = group;
                    relation.RelatedBuildings.Add(building);
                });
        }

        /// <summary>A pipe as an IfcPipeSegment on its storey: its round body, its size and material, and its length.</summary>
        private IfcProduct? ExportPipe(Pipe pipe)
        {
            if (PipeModel.Mesh(_document, pipe) is not { } mesh) return null;
            var storey = StoreyOf(pipe);
            var elevation = _document.FindLevel(pipe.LevelId)?.Elevation ?? 0;
            var type = _document.FindType<PipeType>(pipe.TypeId);
            var segment = New<Xbim.Ifc4.HvacDomain.IfcPipeSegment>(each =>
            {
                each.GlobalId = pipe.Id.ToIfc();
                each.Name = $"{(type is null ? "Pipe" : type.Name)} {pipe.SizeName(type)}";
                each.ObjectType = pipe.Kind.Title();
                each.Tag = pipe.Mark;
                each.PredefinedType = type?.Material == PipeMaterial.PeX ? IfcPipeSegmentTypeEnum.FLEXIBLESEGMENT : IfcPipeSegmentTypeEnum.RIGIDSEGMENT;
                each.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = New<IfcAxis2Placement3D>(axis => axis.Location = Point3D(0, 0, 0));
                });
                each.Representation = TessellatedBody(new[] { mesh }, point => new BIMDesigner.Core.Geometry.Point3D(point.X, point.Y, point.Z - elevation));
            });
            Contain(pipe, segment);
            WriteSet(segment, "Pset_PipeSegmentTypeCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = type is null || string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["NominalDiameter"] = new IfcPositiveLengthMeasure(pipe.Diameter),
                ["InnerDiameter"] = type is null ? null : new IfcPositiveLengthMeasure(PipeSizes.Nearest(type.Material, pipe.Diameter).Inside),
                ["OuterDiameter"] = new IfcPositiveLengthMeasure(pipe.Diameter)
            });
            WriteSet(segment, "Qto_PipeSegmentBaseQuantities", new Dictionary<string, IfcValue?> { ["Length"] = new IfcLengthMeasure(pipe.Length) });
            return segment;
        }

        /// <summary>A duct as an IfcDuctSegment on its storey: its body, its size and its length.</summary>
        private IfcProduct? ExportDuct(Duct duct)
        {
            if (DuctModel.Mesh(_document, duct) is not { } mesh) return null;
            var storey = StoreyOf(duct);
            var elevation = _document.FindLevel(duct.LevelId)?.Elevation ?? 0;
            var type = _document.FindType<DuctType>(duct.TypeId);
            var size = duct.Size(_document);
            var segment = New<Xbim.Ifc4.HvacDomain.IfcDuctSegment>(each =>
            {
                each.GlobalId = duct.Id.ToIfc();
                each.Name = $"{(type is null ? "Duct" : type.Name)} {size.Name}";
                each.ObjectType = duct.Kind.Title();
                each.Tag = duct.Mark;
                each.PredefinedType = type?.Material == DuctMaterial.Flexible ? IfcDuctSegmentTypeEnum.FLEXIBLESEGMENT : IfcDuctSegmentTypeEnum.RIGIDSEGMENT;
                each.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = New<IfcAxis2Placement3D>(axis => axis.Location = Point3D(0, 0, 0));
                });
                each.Representation = TessellatedBody(new[] { mesh }, point => new BIMDesigner.Core.Geometry.Point3D(point.X, point.Y, point.Z - elevation));
            });
            Contain(duct, segment);
            WriteSet(segment, "Pset_DuctSegmentTypeCommon", new Dictionary<string, IfcValue?>
            {
                ["Reference"] = type is null || string.IsNullOrEmpty(type.TypeMark) ? null : new IfcIdentifier(type.TypeMark),
                ["Shape"] = new IfcLabel(size.Shape == DuctShape.Round ? "ROUND" : "RECTANGULAR"),
                ["NominalDiameterOrWidth"] = new IfcPositiveLengthMeasure(size.Width),
                ["NominalHeight"] = size.Shape == DuctShape.Round ? null : new IfcPositiveLengthMeasure(size.Height)
            });
            WriteSet(segment, "Qto_DuctSegmentBaseQuantities", new Dictionary<string, IfcValue?> { ["Length"] = new IfcLengthMeasure(duct.Length) });
            return segment;
        }

        /// <summary>A fire damper where a duct passes through a fire-separating wall or floor: an IfcDamper there, as big as the duct and the wall or floor is thick.</summary>
        private void ExportDamper(Duct duct, BIMDesigner.Core.Geometry.Point2D at, double thickness, double? height, int index)
        {
            var piece = new Duct
            {
                Id = PartId(duct.Id, (byte)(0x40 + index), 0xDA), TypeId = duct.TypeId, LevelId = duct.LevelId,
                Width = duct.Width + 20, Height = duct.Height + 20, Kind = duct.Kind
            };
            if (height is { } z)
                (piece.Start, piece.End, piece.StartOffset, piece.EndOffset) = (at, at, z - thickness / 2, z + thickness / 2);
            else
            {
                var direction = (duct.End - duct.Start).NormalisedOrDefault(BIMDesigner.Core.Geometry.Vector2D.UnitX);
                var t = duct.Start.DistanceTo(at) / Math.Max(1, duct.Start.DistanceTo(duct.End));
                var offset = duct.StartOffset + (duct.EndOffset - duct.StartOffset) * t;
                (piece.Start, piece.End, piece.StartOffset, piece.EndOffset) = (at - direction * (thickness / 2), at + direction * (thickness / 2), offset, offset);
            }
            if (DuctModel.Mesh(_document, piece) is not { } mesh) return;
            var storey = StoreyOf(duct);
            var elevation = _document.FindLevel(duct.LevelId)?.Elevation ?? 0;
            var damper = New<Xbim.Ifc4.HvacDomain.IfcDamper>(each =>
            {
                each.GlobalId = piece.Id.ToIfc();
                each.Name = Duct.Describe(duct.FireProtection);
                each.PredefinedType = duct.FireProtection == FireProtection.FireAndSmokeDamper ? IfcDamperTypeEnum.FIRESMOKEDAMPER : IfcDamperTypeEnum.FIREDAMPER;
                each.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = storey?.ObjectPlacement;
                    placement.RelativePlacement = New<IfcAxis2Placement3D>(axis => axis.Location = Point3D(0, 0, 0));
                });
                each.Representation = TessellatedBody(new[] { mesh }, point => new BIMDesigner.Core.Geometry.Point3D(point.X, point.Y, point.Z - elevation));
            });
            Contain(duct, damper);
        }

        /// <summary>One connector of a fitting as a port, placed in the fitting's own frame.</summary>
        private IfcDistributionPort Port(Component component, IfcDistributionElement element, PlacedConnector placed, int index)
        {
            var connector = placed.Connector;
            var storeyElevation = _document.FindLevel(component.LevelId)?.Elevation ?? 0;
            var turn = component.Rotation * Math.PI / 180;
            var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
            (double X, double Y) Local(double x, double y) => (x * cos + y * sin, -x * sin + y * cos);

            var (x, y) = Local(placed.At.X - component.Location.X, placed.At.Y - component.Location.Y);
            // The axis along the flow: away from the fitting at a source, toward it at a sink.
            var sign = connector.Direction == ConnectorFlow.In ? -1 : 1;
            var (ax, ay) = Local(placed.Points.X * sign, placed.Points.Y * sign);
            var az = placed.Points.Z * sign;
            var (rx, ry, rz) = Math.Abs(az) > 0.9 ? (1.0, 0.0, 0.0) : (0.0, 0.0, 1.0);

            return New<IfcDistributionPort>(port =>
            {
                port.GlobalId = PartId(component.Id, (byte)(0x30 + index), 0xC0).ToIfc();
                port.Name = connector.Name;
                port.FlowDirection = connector.Direction switch
                {
                    ConnectorFlow.In => IfcFlowDirectionEnum.SINK,
                    ConnectorFlow.Out => IfcFlowDirectionEnum.SOURCE,
                    _ => IfcFlowDirectionEnum.SOURCEANDSINK
                };
                port.PredefinedType = connector.Domain switch
                {
                    ConnectorDomain.Pipe => IfcDistributionPortTypeEnum.PIPE,
                    ConnectorDomain.Duct => IfcDistributionPortTypeEnum.DUCT,
                    _ => IfcDistributionPortTypeEnum.CABLE
                };
                port.SystemType = SystemType(connector.Kind);
                port.ObjectPlacement = New<IfcLocalPlacement>(placement =>
                {
                    placement.PlacementRelTo = element.ObjectPlacement;
                    placement.RelativePlacement = New<IfcAxis2Placement3D>(axis =>
                    {
                        axis.Location = Point3D(x, y, placed.At.Z - storeyElevation);
                        axis.Axis = Direction(ax, ay, az);
                        axis.RefDirection = Direction(rx, ry, rz);
                    });
                });
            });
        }

        /// <summary>What a system is in IFC4: its kind - and a circuit for the lights a lighting one.</summary>
        private static IfcDistributionSystemEnum PredefinedType(MepSystem system) =>
            system.Kind == SystemKind.Power && system.Use == CircuitUse.Lighting ? IfcDistributionSystemEnum.LIGHTING : SystemType(system.Kind);

        private static IfcDistributionSystemEnum SystemType(SystemKind kind) => kind switch
        {
            SystemKind.ColdWater => IfcDistributionSystemEnum.DOMESTICCOLDWATER,
            SystemKind.HotWater => IfcDistributionSystemEnum.DOMESTICHOTWATER,
            SystemKind.Heating => IfcDistributionSystemEnum.HEATING,
            SystemKind.Foul => IfcDistributionSystemEnum.DRAINAGE,
            SystemKind.Rainwater => IfcDistributionSystemEnum.RAINWATER,
            SystemKind.Gas => IfcDistributionSystemEnum.GAS,
            SystemKind.ExtractAir => IfcDistributionSystemEnum.EXHAUST,
            SystemKind.SupplyAir or SystemKind.OutsideAir => IfcDistributionSystemEnum.VENTILATION,
            SystemKind.Power => IfcDistributionSystemEnum.ELECTRICAL,
            _ => IfcDistributionSystemEnum.SIGNAL
        };
    }
}
