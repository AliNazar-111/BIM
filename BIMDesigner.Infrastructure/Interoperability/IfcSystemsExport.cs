using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Mep;
using Xbim.Ifc4.GeometricConstraintResource;
using Xbim.Ifc4.GeometryResource;
using Xbim.Ifc4.Interfaces;
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
                    ports.TryAdd((component.Id, placed[i].Connector.Kind, placed[i].Connector.Feeds), port);
                }

                New<IfcRelNests>(relation =>
                {
                    relation.GlobalId = PartId(component.Id, 0x9E, 0x51).ToIfc();
                    relation.RelatingObject = element;
                    relation.RelatedObjects.AddRange(nested);
                });
            }

            var systems = _document.All<MepSystem>().ToList();
            if (systems.Count == 0) return;

            var groups = new List<IfcSystem>();
            foreach (var system in systems)
            {
                var members = MepSystems.MembersOf(_document, system)
                    .Select(member => _products.TryGetValue(member.Id, out var product) ? product : null).OfType<IfcProduct>().ToList();
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
            SystemKind.Gas => IfcDistributionSystemEnum.GAS,
            SystemKind.ExtractAir => IfcDistributionSystemEnum.EXHAUST,
            SystemKind.Power => IfcDistributionSystemEnum.ELECTRICAL,
            _ => IfcDistributionSystemEnum.SIGNAL
        };
    }
}
