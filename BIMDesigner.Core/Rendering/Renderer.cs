using System.Numerics;
using BIMDesigner.Core.Documents;
using BIMDesigner.Core.Views;

namespace BIMDesigner.Core.Rendering;

/// <summary>
/// A light in the model a rendering is lit by: where it gives its light from, mm, whether it gives it
/// downward - a ceiling light, as Lambert has it - or all round, and how much, lumens.
/// </summary>
public sealed record RenderLamp(Guid ElementId, Geometry.Point3D At, bool Downward, float Lumens);

/// <summary>
/// The light a rendering is lit by: the sun's way, colour and strength - reddened and weakened by
/// the air it comes through low in the sky - the sky's, clear or overcast, dimming through twilight
/// into the night, and the model's own lights once it is dusk.
/// </summary>
public sealed record RenderLight(Vector3 SunDirection, Vector3 SunColour, float SunStrength, RenderSky Sky, float SkyStrength)
{
    /// <summary>The model's lights, where they are on; none by day unless asked for.</summary>
    public IReadOnlyList<RenderLamp> Lamps { get; init; } = Array.Empty<RenderLamp>();

    /// <summary>How much brighter the picture is made than the light is: the eye at home in the dark.</summary>
    public float Adaptation { get; init; } = 1;

    /// <summary>
    /// The model's lights: each fitting that gives light - its photometry's lumens, its maker's, or its watts
    /// at its efficacy - a ceiling light giving it downward from just under it, a wall light all round from
    /// just under its middle.
    /// </summary>
    public static IReadOnlyList<RenderLamp> LampsOf(BimDocument document) =>
        document.All<Architecture.Component>()
            .Select(light => (Light: light, Type: document.FindType<Architecture.ComponentType>(light.TypeId)))
            .Where(entry => entry.Type is { Kind: Elements.BuiltInCategory.LightingFixtures } && Mep.Lighting.LumensOf(entry.Type) > 0)
            .Select(entry =>
            {
                var (light, type) = (entry.Light, entry.Type!);
                var bottom = Architecture.ComponentHosting.BaseElevation(document, light, type);
                var wall = type.Form == Architecture.ComponentForm.WallLight;
                var height = wall ? bottom + type.Height / 2 - 40 : bottom - 40;
                return new RenderLamp(light.Id, Geometry.Point3D.On(light.Location, height), !wall, (float)Mep.Lighting.LumensOf(type));
            })
            .ToList();

    /// <summary>The light at the date, time and place set, the plan turned from true north as the project has it.</summary>
    public static RenderLight For(BimDocument document, RenderSettings settings)
    {
        var (altitude, azimuth) = settings.Sun(document);
        var (x, y, z) = SunPosition.Direction(altitude, azimuth, document.ProjectInformation.TrueNorth);
        var direction = Vector3.Normalize(new Vector3((float)x, (float)y, (float)z));

        // Twilight: the sky dims from 10° above the horizon to it, then a tenth for every 2.4° the sun is
        // below it - a thousandth of the day's at 6° down, the end of civil twilight - to a starlit night's.
        var sky = altitude >= 0 ? (float)Math.Min(1, (altitude + 6) / 16) : (float)Math.Max(1e-5, 0.375 * Math.Pow(10, altitude / 2.4));

        // The lights on at dusk and after, or as asked; the eye at home in the dark as it falls, keeping the
        // sky as bright as at sunset, up to ten stops - as a night interior is photographed.
        var on = settings.Lights == RenderLights.On || settings.Lights == RenderLights.Auto && altitude < 3;
        var lamps = on ? LampsOf(document) : Array.Empty<RenderLamp>();
        var adaptation = altitude < 0 ? (float)Math.Clamp(0.375 / sky, 1, 1024) : 1f;
        if (settings.Sky == RenderSky.Overcast || altitude <= 0) return new RenderLight(direction, Vector3.Zero, 0, settings.Sky, sky) { Lamps = lamps, Adaptation = adaptation };

        // Kasten and Young's air mass; each colour lost to it at its own rate, blue the most.
        var mass = 1 / (Math.Sin(altitude * Math.PI / 180) + 0.50572 * Math.Pow(altitude + 6.07995, -1.6364));
        var colour = new Vector3((float)Math.Exp(-0.04 * (mass - 1)), (float)Math.Exp(-0.09 * (mass - 1)), (float)Math.Exp(-0.20 * (mass - 1)));
        var strength = (float)(3.2 * Math.Clamp(altitude / 2, 0, 1));
        return new RenderLight(direction, colour, strength, settings.Sky, sky) { Lamps = lamps, Adaptation = adaptation };
    }
}

/// <summary>
/// A rendering under way: each pixel worked out from paths of light traced back from the eye - into
/// the model, off what they meet towards the sun and the sky, through glass - pass by pass, each
/// pass making it smoother, and shown as it goes.
/// </summary>
public sealed class RenderJob
{
    private readonly RenderScene _scene;
    private readonly RenderLight _light;
    private readonly Vector3[] _sum;
    private readonly Vector3 _eye, _look, _right, _up;
    private readonly bool _perspective;
    private readonly float _halfWidth;
    private readonly float _exposure;

    /// <summary>The lamps, in the scene's metres: where each is, whether it shines down, and its strength - its candelas, in the sun's units.</summary>
    private readonly (Vector3 At, bool Downward, float Strength)[] _lamps;

    /// <summary>A warm white, about 3000 K, as bright as white.</summary>
    private static readonly Vector3 LampColour = new(1.27f, 0.95f, 0.66f);

    /// <summary>The sun's strength, 3.2, is about 100,000 lux: what a lux is in the sun's units.</summary>
    private const float PerLux = 3.2f / 100000;
    private int _passes;

    public RenderJob(RenderScene scene, Camera3D camera, RenderLight light, int width, int height, double exposure)
    {
        (_scene, _light, Width, Height) = (scene, light, Math.Max(1, width), Math.Max(1, height));
        _sum = new Vector3[Width * Height];
        _eye = scene.ToScene(camera.Eye);
        Vector3 Of(Geometry.Point3D p) => Vector3.Normalize(new Vector3((float)p.X, (float)p.Y, (float)p.Z));
        (_look, _right, _up) = (Of(camera.Look), Of(camera.Right), Of(camera.Up));
        _perspective = camera.Perspective;
        _halfWidth = camera.Perspective ? (float)Math.Tan(camera.FieldOfView / 2 * Math.PI / 180) : (float)(camera.Width / 2 / 1000);
        _exposure = (float)(0.6 * Math.Pow(2, exposure)) * light.Adaptation;

        // A ceiling light's candelas straight down, its lumens over pi, Lambert's; one all round, its lumens over four pi.
        _lamps = light.Lamps.Select(lamp => (scene.ToScene(lamp.At), lamp.Downward, lamp.Lumens / (lamp.Downward ? MathF.PI : 4 * MathF.PI) * PerLux)).ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>How many paths each pixel has been worked out from so far.</summary>
    public int Samples { get; private set; }

    /// <summary>
    /// Another pass: each pixel worked out from more paths, rows shared among the processor's cores.
    /// Stopped where it has got to when cancelled; <paramref name="rowsDone"/> told as each row is.
    /// </summary>
    public void Pass(int samples, CancellationToken cancel = default, Action<int>? rowsDone = null)
    {
        var pass = _passes++;
        var done = 0;
        var sums = new Vector3[_sum.Length];
        try
        {
            Parallel.For(0, Height, new ParallelOptions { CancellationToken = cancel }, row =>
            {
                var random = new Random32((uint)(row * 9781 + pass * 6271 + 1));
                for (var column = 0; column < Width; column++)
                {
                    var sum = Vector3.Zero;
                    for (var s = 0; s < samples; s++)
                    {
                        var (dx, dy) = (random.Next(), random.Next());
                        var sx = (2 * (column + dx) / Width - 1) * _halfWidth;
                        var sy = (1 - 2 * (row + dy) / Height) * _halfWidth * Height / Width;
                        var (origin, direction) = _perspective
                            ? (_eye, Vector3.Normalize(_look + _right * sx + _up * sy))
                            : (_eye + _right * sx + _up * sy, _look);
                        sum += Trace(origin, direction, ref random);
                    }

                    sums[row * Width + column] = sum;
                }

                rowsDone?.Invoke(Interlocked.Increment(ref done));
            });
        }
        catch (OperationCanceledException)
        {
            // What was done of the pass is uneven across the picture: it is left out.
            _passes--;
            throw;
        }

        for (var i = 0; i < sums.Length; i++) _sum[i] += sums[i];
        Samples += samples;
    }

    /// <summary>The picture as it stands: blue, green, red and alpha a pixel, row by row from the top, filmically toned and gamma corrected.</summary>
    public byte[] Bgra()
    {
        var pixels = new byte[Width * Height * 4];
        var scale = _exposure / Math.Max(1, Samples);
        for (var i = 0; i < _sum.Length; i++)
        {
            var c = _sum[i] * scale;
            pixels[4 * i] = Encode(c.Z);
            pixels[4 * i + 1] = Encode(c.Y);
            pixels[4 * i + 2] = Encode(c.X);
            pixels[4 * i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>A pixel's light as a byte: Narkowicz's fit of the ACES film curve, then the screen's gamma.</summary>
    private static byte Encode(float x)
    {
        if (!float.IsFinite(x) || x <= 0) return 0;
        var toned = x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f);
        var c = Math.Clamp(toned, 0, 1);
        var encoded = c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(encoded * 255), 0, 255);
    }

    // ---- light --------------------------------------------------------------------------------

    private const float Epsilon = 2e-4f;

    /// <summary>The light coming back along a ray: what it meets lit by the sun and, bounce by bounce, by the sky and everything else.</summary>
    private Vector3 Trace(Vector3 origin, Vector3 direction, ref Random32 random)
    {
        var throughput = Vector3.One;
        var light = Vector3.Zero;
        var bounces = 0;
        for (var step = 0; step < 24; step++)
        {
            if (!_scene.Intersect(origin, direction, float.MaxValue, out var hit))
            {
                light += throughput * Sky(direction);
                break;
            }

            var material = _scene.Materials[_scene.MaterialOf[hit.Triangle]];
            var point = origin + direction * hit.T;

            // A light seen: its shade glowing.
            if (bounces == 0 && material.Emission != Vector3.Zero) light += throughput * material.Emission;
            var face = _scene.Normal[hit.Triangle];
            var normal = _scene.NormalAt(hit);
            var facing = Vector3.Dot(face, direction) < 0;
            var (geometric, shading) = facing ? (face, normal) : (-face, -normal);

            // A faded thing is seen through as much as it is faded.
            if (!material.Glass && material.Opacity < 1 && random.Next() > material.Opacity)
            {
                origin = point + direction * Epsilon;
                continue;
            }

            var cosine = Math.Clamp(-Vector3.Dot(direction, shading), 0, 1);
            if (material.Glass)
            {
                // Glass: some of the light reflected, more of it at a glancing angle; the rest straight through, tinted a little.
                var fresnel = 0.04f + 0.96f * MathF.Pow(1 - cosine, 5);
                if (random.Next() < fresnel)
                {
                    direction = Vector3.Reflect(direction, shading);
                    origin = point + geometric * Epsilon;
                }
                else
                {
                    throughput *= Vector3.Lerp(Vector3.One, material.Albedo / Math.Max(1e-3f, Max(material.Albedo)), 0.12f) * 0.94f;
                    origin = point + direction * Epsilon;
                }

                continue;
            }

            // Its colour where the ray meets it, its pattern's relief bending the light across it.
            var albedo = material.Albedo;
            if (material.Texture is { } texture)
            {
                var mm = new Vector3(point.X * 1000 + (float)_scene.Origin.X, point.Y * 1000 + (float)_scene.Origin.Y, point.Z * 1000 + (float)_scene.Origin.Z);
                var (along, up) = TextureMapping.Basis(face.X, face.Y, face.Z);
                var u = mm.X * along.X + mm.Y * along.Y + mm.Z * along.Z;
                var v = mm.X * up.X + mm.Y * up.Y + mm.Z * up.Z;
                var index = texture.IndexAt(u, v) * 3;
                albedo = Vector3.Min(new Vector3(RenderScene.ToLinear[texture.Pixels[index]], RenderScene.ToLinear[texture.Pixels[index + 1]], RenderScene.ToLinear[texture.Pixels[index + 2]]), new Vector3(0.9f));
                if (material.Varies) albedo *= 1 + 0.16f * Patches(mm.X / 4000, mm.Y / 4000) + 0.08f * Patches(mm.X / 900 + 17, mm.Y / 900 + 31);
                if (material.Relief > 0)
                {
                    var (du, dv) = (texture.Length / texture.Width, texture.Rise / texture.Height);
                    var slopeU = (float)((texture.ReliefAt(u + du, v) - texture.ReliefAt(u - du, v)) * material.Relief / (2 * du));
                    var slopeV = (float)((texture.ReliefAt(u, v + dv) - texture.ReliefAt(u, v - dv)) * material.Relief / (2 * dv));
                    var tangent = new Vector3((float)along.X, (float)along.Y, (float)along.Z);
                    var bitangent = new Vector3((float)up.X, (float)up.Y, (float)up.Z);
                    var bent = Vector3.Normalize(shading - (tangent * slopeU + bitangent * slopeV) * (facing ? 1 : -1));
                    if (Vector3.Dot(bent, geometric) > 0.05f) shading = bent;
                    cosine = Math.Clamp(-Vector3.Dot(direction, shading), 0, 1);
                }
            }

            // A polished face reflects as a mirror, more at a glancing angle; a rough one - slate, glazed
            // tiles - less, and blurred: Lagarde's rough Fresnel, its reflection scattered by its roughness.
            if (material.Reflectance > 0)
            {
                var rough = material.Reflectance < 0.1f ? 0.6f : 0.1f;
                var fresnel = material.Reflectance + (Math.Max(1 - rough, material.Reflectance) - material.Reflectance) * MathF.Pow(1 - cosine, 5);
                if (random.Next() < fresnel)
                {
                    direction = Vector3.Normalize(Vector3.Lerp(Vector3.Reflect(direction, shading), Scatter(shading, ref random), rough * rough));
                    if (Vector3.Dot(direction, geometric) <= 0) break;
                    origin = point + geometric * Epsilon;
                    continue;
                }
            }

            // The sun on it, unless something is in the way.
            if (_light.SunStrength > 0)
            {
                var sun = Vector3.Dot(shading, _light.SunDirection);
                if (sun > 0 && Vector3.Dot(geometric, _light.SunDirection) > 0)
                {
                    var through = _scene.Transmission(point + geometric * Epsilon, _light.SunDirection);
                    if (through > 0) light += throughput * albedo * _light.SunColour * (_light.SunStrength * sun * through / MathF.PI);
                }
            }

            // The lamps on it: two of them a bounce, chosen at random, each standing for its share.
            if (_lamps.Length > 0)
            {
                var picks = Math.Min(2, _lamps.Length);
                for (var k = 0; k < picks; k++)
                {
                    var (at, downward, strength) = _lamps[Math.Min(_lamps.Length - 1, (int)(random.Next() * _lamps.Length))];
                    var to = at - point;
                    var distance = to.Length();
                    if (distance < 1e-3f) continue;
                    var way = to / distance;
                    var onFace = Vector3.Dot(shading, way);
                    if (onFace <= 0 || Vector3.Dot(geometric, way) <= 0) continue;

                    // A ceiling light shines on what is under it: the more straight under, the more.
                    var intensity = downward ? strength * Math.Max(0, way.Z) : strength;
                    if (intensity <= 0) continue;
                    var through = _scene.Transmission(point + geometric * Epsilon, way, distance - 2 * Epsilon);
                    if (through > 0) light += throughput * albedo * LampColour * (intensity * onFace * through / (distance * distance) / MathF.PI * _lamps.Length / picks);
                }
            }

            // Then on, the way a matt face scatters it, until it has bounced three times or faded away.
            if (bounces++ >= 3) break;
            throughput *= albedo;
            if (bounces >= 2)
            {
                var keep = Math.Clamp(Max(throughput), 0.05f, 0.95f);
                if (random.Next() > keep) break;
                throughput /= keep;
            }

            direction = Scatter(shading, ref random);
            if (Vector3.Dot(direction, geometric) <= 0) break;
            origin = point + geometric * Epsilon;
        }

        return light;
    }

    /// <summary>The sky's light from a way: blue overhead paling to the horizon and bright round the sun, or overcast grey three times as bright overhead.</summary>
    private Vector3 Sky(Vector3 direction)
    {
        // Below the horizon, past the ground: the distance lost in haze.
        var height = direction.Z;
        if (_light.Sky == RenderSky.Overcast) return new Vector3(0.86f, 0.88f, 0.92f) * (1.6f * (1 + 2 * Math.Max(0, height)) / 3 * _light.SkyStrength);
        if (height < 0) return new Vector3(0.50f, 0.58f, 0.66f) * _light.SkyStrength;

        var horizon = MathF.Pow(1 - height, 4);
        var sky = Vector3.Lerp(new Vector3(0.15f, 0.30f, 0.68f), new Vector3(0.56f, 0.68f, 0.86f), horizon);
        var toSun = Math.Max(0, Vector3.Dot(direction, _light.SunDirection));
        var glow = _light.SunColour * (0.5f * MathF.Pow(toSun, 48) + 0.12f * MathF.Pow(toSun, 4));
        return (sky + glow) * _light.SkyStrength;
    }

    /// <summary>A way off a matt face, more of them near square to it than glancing: Lambert's cosine.</summary>
    private static Vector3 Scatter(Vector3 normal, ref Random32 random)
    {
        var (r1, r2) = (random.Next(), random.Next());
        var phi = 2 * MathF.PI * r1;
        var radius = MathF.Sqrt(r2);
        var (x, y, z) = (radius * MathF.Cos(phi), radius * MathF.Sin(phi), MathF.Sqrt(1 - r2));
        var helper = MathF.Abs(normal.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(helper, normal));
        var bitangent = Vector3.Cross(normal, tangent);
        return Vector3.Normalize(tangent * x + bitangent * y + normal * z);
    }

    private static float Max(Vector3 v) => Math.Max(v.X, Math.Max(v.Y, v.Z));

    /// <summary>Smooth noise over the plan that never repeats, -1 to 1: ground cover's lighter and darker patches.</summary>
    private static float Patches(float x, float y)
    {
        var (x0, y0) = ((int)MathF.Floor(x), (int)MathF.Floor(y));
        var (tx, ty) = (x - x0, y - y0);
        (tx, ty) = (tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty));
        static float V(int i, int j) => (float)(2 * SurfaceTextures.Hash(i, j, 977) - 1);
        var top = V(x0, y0) + (V(x0 + 1, y0) - V(x0, y0)) * tx;
        var bottom = V(x0, y0 + 1) + (V(x0 + 1, y0 + 1) - V(x0, y0 + 1)) * tx;
        return top + (bottom - top) * ty;
    }

    /// <summary>Numbers that look random, quickly, each row its own run of them.</summary>
    private struct Random32(uint seed)
    {
        private uint _state = seed == 0 ? 0x9E3779B9u : seed * 747796405u + 2891336453u;

        public float Next()
        {
            unchecked
            {
                _state ^= _state << 13;
                _state ^= _state >> 17;
                _state ^= _state << 5;
                return (_state >> 8) * (1f / 16777216f);
            }
        }
    }
}
