using BIMDesigner.Core.Architecture;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Geometry;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Mep;

/// <summary>
/// One connection point of a fitting: where a pipe, a duct or a cable joins it, what it carries
/// and which way - a basin's hot and cold supplies and its waste, a radiator's flow and return, a
/// socket's power. It sits in the fitting's own frame - across it, front to back and up from its
/// base - pointing the way the pipe or cable leaves it, as IFC4 places a port on its element.
/// </summary>
public sealed record Connector(string Name, SystemKind Kind, ConnectorFlow Direction, double Size, double Across, double Forward, double Up)
{
    /// <summary>The way it points in the fitting's frame - back into the wall, down through the floor, up into the ceiling.</summary>
    public (double Across, double Forward, double Up) Points { get; init; } = (0, 0, -1);

    /// <summary>
    /// Whether it feeds the system it is on rather than taking from it: a boiler's flow, a
    /// cylinder's hot water, the stop valve on the incoming main, a consumer unit. A fitting
    /// feeding a system is its source, not one of its members.
    /// </summary>
    public bool Feeds { get; init; }

    /// <summary>
    /// Whether it may be left unconnected without anything being wanting: a cooker hood that
    /// recirculates, a carbon monoxide alarm on a battery, a boiler's condensate not drawn yet.
    /// </summary>
    public bool Optional { get; init; }

    /// <summary>What it is joined with: the kind's own pipe, duct or cable - or a control cable, as a room thermostat's is.</summary>
    public ConnectorDomain Domain { get; init; } = Kind.Domain();

    /// <summary>Whether it can feed more than one system: a consumer unit's circuits, a boiler's heating zones.</summary>
    public bool FeedsMany => Feeds && Kind is SystemKind.Power or SystemKind.Heating;
}

/// <summary>A connector where it is in the building: the point it is at and the way it points.</summary>
public readonly record struct PlacedConnector(Connector Connector, Point3D At, (double X, double Y, double Z) Points);

/// <summary>
/// The connectors each fitting has, by its form and size - the sizes a home's are in the UK: 15
/// mm supplies to taps and radiators, 22 mm to a bath and from a boiler, a 32 mm waste from a
/// basin and 40 mm from a bath, shower, sink or machine, a 100 mm soil pipe from a WC
/// (Approved Document H, Table 1's traps).
/// </summary>
public static class Connectors
{
    private static readonly (double, double, double) Back = (0, -1, 0);
    private static readonly (double, double, double) Front = (0, 1, 0);
    private static readonly (double, double, double) Down = (0, 0, -1);
    private static readonly (double, double, double) Up = (0, 0, 1);
    private static readonly (double, double, double) Right = (1, 0, 0);

    /// <summary>The connectors of a fitting of this type; none for furniture, or anything else with no services.</summary>
    public static IReadOnlyList<Connector> Of(ComponentType type)
    {
        var (w, d, h) = (type.Width, type.Depth, type.Height);
        var (halfW, halfD) = (w / 2, d / 2);
        Connector Pipe(string name, SystemKind kind, ConnectorFlow direction, double size, double across, double forward, double up, (double, double, double) points) =>
            new(name, kind, direction, size, across, forward, up) { Points = points };
        Connector Cable(string name, double across, double forward, double up, (double, double, double) points) =>
            new(name, SystemKind.Power, ConnectorFlow.In, 0, across, forward, up) { Points = points };

        switch (type.Form)
        {
            case ComponentForm.Basin:
            {
                // The tap tails under the bowl, hot on the left as you face it, and the waste below the bowl.
                var tails = Math.Max(h - 150, h * 0.5);
                return new[]
                {
                    Pipe("Hot Supply", SystemKind.HotWater, ConnectorFlow.In, 15, -Math.Min(60, halfW * 0.4), -halfD + Math.Min(70, d * 0.3), tails, Down),
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, Math.Min(60, halfW * 0.4), -halfD + Math.Min(70, d * 0.3), tails, Down),
                    Pipe("Waste", SystemKind.Foul, ConnectorFlow.Out, 32, 0, -halfD + d * 0.45, Math.Max(h - 180, 0), Down)
                };
            }

            case ComponentForm.Bath:
                // The taps at the head end, on the rim; the waste under that end.
                return new[]
                {
                    Pipe("Hot Supply", SystemKind.HotWater, ConnectorFlow.In, 22, -halfW + 90, -75, h - 20, Down),
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 22, -halfW + 90, 75, h - 20, Down),
                    Pipe("Waste", SystemKind.Foul, ConnectorFlow.Out, 40, -halfW + 200, 0, 40, Down)
                };

            case ComponentForm.Shower:
                // The mixer on the wall behind, at about a metre; the waste in the middle of the tray.
                return new[]
                {
                    Pipe("Hot Supply", SystemKind.HotWater, ConnectorFlow.In, 15, -75, -halfD, 1100, Back),
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, 75, -halfD, 1100, Back),
                    Pipe("Waste", SystemKind.Foul, ConnectorFlow.Out, 40, 0, 0, 0, Down)
                };

            case ComponentForm.Sink:
            {
                // Taps behind the bowl, which is to the left of a sink with a drainer.
                var bowl = w >= 800 ? -w * 0.2 : 0;
                return new[]
                {
                    Pipe("Hot Supply", SystemKind.HotWater, ConnectorFlow.In, 15, bowl - 60, -halfD + 60, h - 40, Down),
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, bowl + 60, -halfD + 60, h - 40, Down),
                    Pipe("Waste", SystemKind.Foul, ConnectorFlow.Out, 40, bowl, -halfD + Math.Min(250, d * 0.45), Math.Max(h - 200, 0), Down)
                };
            }

            case ComponentForm.Toilet:
                // The cistern fed from the side, the pan's outlet out of the back into the soil pipe.
                return new[]
                {
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, halfW - 40, -halfD + 60, Math.Max(h - 150, 0), Right),
                    Pipe("Soil", SystemKind.Foul, ConnectorFlow.Out, 100, 0, -halfD, 180, Back)
                };

            case ComponentForm.WallHungToilet:
                return new[]
                {
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, halfW - 60, -halfD, Math.Max(h - 150, 0), Back),
                    Pipe("Soil", SystemKind.Foul, ConnectorFlow.Out, 100, 0, -halfD, 230, Back)
                };

            case ComponentForm.WashingMachine:
            case ComponentForm.Dishwasher:
                // Its fill hose to a valve and its drain to a standpipe or trap behind it; it plugs into a socket.
                return new[]
                {
                    Pipe("Cold Supply", SystemKind.ColdWater, ConnectorFlow.In, 15, halfW - 80, -halfD, 550, Back),
                    Pipe("Waste", SystemKind.Foul, ConnectorFlow.Out, 40, -halfW + 80, -halfD, 500, Back)
                };

            case ComponentForm.Radiator:
            case ComponentForm.TowelRail:
            {
                // Flow in at one bottom corner and return out at the other, down to the floor.
                var end = halfW - Math.Min(30, halfW * 0.1);
                return new[]
                {
                    Pipe("Flow", SystemKind.Heating, ConnectorFlow.In, 15, -end, 0, 0, Down),
                    Pipe("Return", SystemKind.Heating, ConnectorFlow.Out, 15, end, 0, 0, Down)
                };
            }

            case ComponentForm.Boiler:
            {
                // Along its underside, left to right as a combi's are: heating flow, hot water, gas,
                // cold main, heating return - and its condensate, and the fused spur feeding it.
                var forward = -halfD + Math.Min(80, d * 0.3);
                var combi = type.HotWater == BoilerHotWater.Combi;
                var connectors = new List<Connector>
                {
                    Pipe("Heating Flow", SystemKind.Heating, ConnectorFlow.Out, 22, -Math.Min(150, halfW * 0.7), forward, 0, Down) with { Feeds = true },
                    Pipe("Gas", SystemKind.Gas, ConnectorFlow.In, 22, 0, forward, 0, Down),
                    Pipe("Heating Return", SystemKind.Heating, ConnectorFlow.In, 22, Math.Min(150, halfW * 0.7), forward, 0, Down),
                    Pipe("Condensate", SystemKind.Foul, ConnectorFlow.Out, 22, halfW - Math.Min(40, halfW * 0.2), forward, 0, Down) with { Optional = true },
                    Cable("Power", halfW - Math.Min(60, halfW * 0.3), -halfD, 40, Back)
                };
                if (combi)
                {
                    connectors.Insert(1, Pipe("Hot Water", SystemKind.HotWater, ConnectorFlow.Out, 15, -Math.Min(75, halfW * 0.35), forward, 0, Down) with { Feeds = true });
                    connectors.Insert(3, Pipe("Cold Main", SystemKind.ColdWater, ConnectorFlow.In, 15, Math.Min(75, halfW * 0.35), forward, 0, Down));
                }

                return connectors;
            }

            case ComponentForm.HeatPump:
                // A monobloc unit outside: its heating flow and return out of the back, low, through the wall to the house; its power.
                return new[]
                {
                    Pipe("Heating Flow", SystemKind.Heating, ConnectorFlow.Out, 28, -Math.Min(150, halfW * 0.4), -halfD, Math.Min(250, h * 0.3), Back) with { Feeds = true },
                    Pipe("Heating Return", SystemKind.Heating, ConnectorFlow.In, 28, Math.Min(150, halfW * 0.4), -halfD, Math.Min(250, h * 0.3), Back),
                    Cable("Power", halfW - Math.Min(80, halfW * 0.3), -halfD, h * 0.6, Back)
                };

            case ComponentForm.UnderfloorHeating:
                // The loop's two ends at the back of it, where they go off to the manifold.
                return new[]
                {
                    Pipe("Flow", SystemKind.Heating, ConnectorFlow.In, 16, -Math.Min(40, halfW * 0.3), -halfD, 0, Back),
                    Pipe("Return", SystemKind.Heating, ConnectorFlow.Out, 16, Math.Min(40, halfW * 0.3), -halfD, 0, Back)
                };

            case ComponentForm.HotWaterCylinder:
                // Cold in at the bottom, hot out of the top; the boiler's coil through it, flow in high and return out low; its immersion heater.
                return new[]
                {
                    Pipe("Cold Feed", SystemKind.ColdWater, ConnectorFlow.In, 22, 0, -halfD, 150, Back),
                    Pipe("Hot Draw-off", SystemKind.HotWater, ConnectorFlow.Out, 22, 0, 0, h, Up) with { Feeds = true },
                    Pipe("Coil Flow", SystemKind.Heating, ConnectorFlow.In, 22, -w * 0.25, -halfD, h * 0.55, Back),
                    Pipe("Coil Return", SystemKind.Heating, ConnectorFlow.Out, 22, w * 0.25, -halfD, h * 0.2, Back),
                    Cable("Immersion Heater", 0, halfD, h * 0.6, Front)
                };

            case ComponentForm.FoulOutfall:
                // Where the foul drain meets the sewer: the root its drains run to.
                return new[] { Pipe("Sewer", SystemKind.Foul, ConnectorFlow.In, 100, 0, 0, 0, Down) with { Feeds = true } };

            case ComponentForm.SurfaceWaterOutfall:
            case ComponentForm.Soakaway:
                return new[] { Pipe(type.Form == ComponentForm.Soakaway ? "Soakaway" : "Sewer", SystemKind.Rainwater, ConnectorFlow.In, 100, 0, 0, 0, Down) with { Feeds = true } };

            case ComponentForm.StopValve:
                return new[] { Pipe("Cold Main", SystemKind.ColdWater, ConnectorFlow.Out, 22, 0, 0, h, Up) with { Feeds = true } };

            case ComponentForm.GasMeter:
                return new[] { Pipe("Gas Outlet", SystemKind.Gas, ConnectorFlow.Out, 22, 0, -halfD, h * 0.3, Back) with { Feeds = true } };

            case ComponentForm.ConsumerUnit:
                // Its circuits leave from the top.
                return new[] { new Connector("Circuits", SystemKind.Power, ConnectorFlow.Out, 0, 0, -halfD + Math.Min(40, d * 0.3), h) { Points = Up, Feeds = true } };

            case ComponentForm.Socket:
            case ComponentForm.ShaverSocket:
            case ComponentForm.Switch:
            case ComponentForm.WallLight:
                return new[] { Cable("Power", 0, -halfD, h / 2, Back) };

            case ComponentForm.CeilingLight:
                return new[] { Cable("Power", 0, 0, h, Up) };

            case ComponentForm.ExtractFan:
            {
                // Its duct out through the wall behind it - 100 mm for a small fan, 150 mm for a kitchen's.
                var duct = type.ExtractRate > 30 ? 150 : type.ExtractRate > 15 ? 125 : 100;
                return new[]
                {
                    Pipe("Extract", SystemKind.ExtractAir, ConnectorFlow.Out, duct, 0, -halfD, h / 2, Back),
                    Cable("Power", halfW * 0.6, -halfD, h / 2, Back)
                };
            }

            case ComponentForm.CookerHood:
                return new[]
                {
                    Pipe("Extract", SystemKind.ExtractAir, ConnectorFlow.Out, 150, 0, -halfD + d * 0.4, h, Up) with { Optional = true },
                    Cable("Power", halfW - Math.Min(60, halfW * 0.2), -halfD, Math.Max(h - 120, 0), Back)
                };

            case ComponentForm.Cooker:
                return new[] { Cable("Power", 0, -halfD, 150, Back) };

            case ComponentForm.OvenHousing:
                return new[] { Cable("Power", 0, -halfD, 300, Back) with { Optional = true } };

            case ComponentForm.SmokeAlarm:
            case ComponentForm.HeatAlarm:
                // Mains powered, with the cable linking it to the others alongside.
                return new[]
                {
                    Cable("Power", 0, 0, h, Up),
                    new Connector("Interlink", SystemKind.FireAlarm, ConnectorFlow.Both, 0, 0, 0, h) { Points = Up }
                };

            case ComponentForm.CarbonMonoxideAlarm:
                return new[] { Cable("Power", 0, 0, h, Up) with { Optional = true } };

            case ComponentForm.Thermostat:
                // Wired to the zone it controls.
                return new[] { new Connector("Zone Control", SystemKind.Heating, ConnectorFlow.Both, 0, 0, -halfD, h / 2) { Points = Back, Domain = ConnectorDomain.Electrical } };

            default:
                return Array.Empty<Connector>();
        }
    }

    /// <summary>The connectors of a placed fitting, where they are in the building.</summary>
    public static IReadOnlyList<PlacedConnector> Placed(BimDocument document, Component component)
    {
        if (document.FindType<ComponentType>(component.TypeId) is not { } type) return Array.Empty<PlacedConnector>();
        var connectors = Of(type);
        if (connectors.Count == 0) return Array.Empty<PlacedConnector>();

        var frame = ComponentModel.FrameOf(component, ComponentHosting.BaseElevation(document, component, type));
        var (across, facing) = (frame.Mirrored ? -frame.Across : frame.Across, frame.Facing);
        return connectors.Select(connector =>
        {
            var plan = frame.Plan(connector.Across, connector.Forward);
            var (a, f, u) = connector.Points;
            var way = across * a + facing * f;
            return new PlacedConnector(connector, new Point3D(plan.X, plan.Y, frame.Base + connector.Up), (way.X, way.Y, u));
        }).ToList();
    }
}
