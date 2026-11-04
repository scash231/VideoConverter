using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace VideoConverterUI.Controls;

/// <summary>
/// Apple Liquid Glass as a pixel shader over a barely blurred backdrop. The flat middle is frosted (a disc blur of
/// radius <see cref="Frost"/>) so text on the glass stays legible, while the rim stays sharp so its lensing reads. Its input is larger than the card by
/// <see cref="Inset"/> on every side, so the rim can lens in what lies just beyond the edge: across a convex bevel of
/// width <see cref="Bevel"/> the backdrop is squeezed in from up to <see cref="Refraction"/> outside, with a slight
/// colour split. It also boosts saturation/brightness like Apple's materials and lights the rim: a crisp line just
/// inside the edge plus a soft inner glow, strongest where the edge faces the light (top-left) and on the opposite side.
/// The edge is the same superellipse as <see cref="Services.SquircleBorder"/>, so the light follows the real outline.
/// The HLSL is compiled once at runtime by Windows' own d3dcompiler_47.dll, so the build needs no fxc. That takes
/// close to a second, so <see cref="Warmup"/> does it on a background thread at startup, and the bytecode is cached in
/// %LOCALAPPDATA%\VideoConverter\cache (keyed by a hash of the HLSL) so later launches only read a file.
/// <see cref="TryCreate"/> returns null when the shader can't run (software rendering, compile failure).
/// <para>That rim-squeeze lens is <see cref="LensStyle"/> 0. Style 1 (the default) is glisse's
/// (github.com/0kzh/glisse): a dome lens that shows the backdrop from further in, so the rim magnifies and wraps what is
/// under the glass, with glisse's colour split and its thin iOS rim line over a 0.5 DIP edge line. Its sizes are shares
/// of the shorter side (<see cref="LensStrength"/>, <see cref="LensDepth"/>, <see cref="LensDome"/>), so one setting
/// fits every surface.</para>
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    private const string Hlsl = """
        sampler2D input : register(s0);
        float2 size       : register(c0);
        float  radius     : register(c1);
        float  saturation : register(c2);
        float  brightness : register(c3);
        float  refraction : register(c4);
        float  bevel      : register(c5);
        float  dispersion : register(c6);
        float  highlight  : register(c7);
        float  inset      : register(c8);
        float  frost      : register(c9);
        float  reflection : register(c10);
        float  reflectReach : register(c11);
        float  reflectWidth : register(c12);
        float2 arms       : register(c13);   // backwards-L shape: right arm width, bottom arm height (0 = rounded rect)
        float  lensStyle  : register(c14);   // 0 = rim squeeze (outward), 1 = glisse dome lens (inward); between blends
        float  lensStrength   : register(c15);
        float  lensDepth      : register(c16);
        float  lensDome       : register(c17);
        float  lensDispersion : register(c18);
        float  rimShine   : register(c19);
        float  rimEdge    : register(c20);   // the 0.5 px edge line: > 0 white, < 0 black, its size the alpha
        float3 domeScale  : register(c21);   // the dome arcs' slope scales (across, down, along an L's arms), from C#
        float  lensBlur   : register(c22);   // glisse lens: how far (DIPs) the rim's samples spread, its softness
        float  lensMaxSide : register(c23);  // the lens is sized as if the shorter side were at most this (DIPs)

        // Rounded rect with superellipse (n = 4) corners like SquircleBorder: 0 on the edge, negative inside.
        // Fully rounded (r = half the short side) it is a circle / capsule (n = 2), also like SquircleBorder.
        // Not an exact distance in the corners, close enough for a few pixels around the edge.
        float sdSquircle(float2 p, float2 h, float r)
        {
            float2 q = abs(p) - h + r;
            float2 c = max(q, 0.0001);
            float2 c2 = c * c;
            float corner = lerp(pow(dot(c2, c2), 0.25), sqrt(c2.x + c2.y), step(min(h.x, h.y) - 0.5, r));
            return corner + min(max(q.x, q.y), 0) - r;
        }

        // The card's outline: the rounded rect, or the backwards L (LiquidGlassPanel.ReverseLArms) as the union of a
        // right arm and a bottom arm (each a squircle, like the panel's clip). Below-right of the inner fillet's centre
        // the boundary is only that fillet and the outer edges, so there it is "outside the fillet circle, inside the
        // card" (no crease from the two arms meeting).
        float sdShape(float2 p, float2 h, float r)
        {
            [branch] if (arms.x <= 0 || arms.y <= 0)
                return sdSquircle(p, h, r);

            float2 a = min(arms, h * 2);
            float ra = min(r, min(a.x, a.y) * 0.5);
            float2 inner = h - a;   // the inner corner, card-centred
            float right  = sdSquircle(p - float2((inner.x + h.x) * 0.5, 0), float2(a.x * 0.5, h.y), ra);
            float bottom = sdSquircle(p - float2(0, (inner.y + h.y) * 0.5), float2(h.x, a.y * 0.5), ra);
            float2 c = inner - ra;
            [branch] if (p.x > c.x && p.y > c.y)
                return max(ra - length(p - c), sdSquircle(p, h, ra));
            return min(right, bottom);
        }

        // The shorter side the lens is sized by: the card's, or an L's narrower arm, up to lensMaxSide. Without that
        // limit a big surface got a band and a pull that grew with it, tens of DIPs wide, smearing what's under it.
        float domeSide(float2 h)
        {
            float2 a = min(arms, h * 2);
            return min(lerp(2 * min(h.x, h.y), max(min(a.x, a.y), 1), step(0.5, arms.x) * step(0.5, arms.y)), max(lensMaxSide, 1));
        }

        // How far into the dome's band a point is: 0 in the flat middle, 0.5 on the band's inner edge, about 0.84 at
        // the rim. glisse measures from an inner rounded rect (circular corners, the card's radius); an L from its
        // own outline moved in.
        float domeBand(float2 p, float2 h, float r, float d)
        {
            float depth = max(lensDepth * domeSide(h), 0.5);
            float2 ih = max(h - depth, 0);
            float ir = clamp(r, 0, min(ih.x, ih.y));
            float2 q = abs(p) - ih + ir;
            float inner = length(max(q, 0)) + min(max(q.x, q.y), 0) - ir;
            inner = lerp(inner, d + depth, step(0.5, arms.x) * step(0.5, arms.y));
            return 0.5 + 0.5 * tanh(1.7724539 * inner / (depth * 1.4142136));
        }

        // The glisse lens (github.com/0kzh/glisse, after Aave's web glass): within a band lensDepth of the shorter side
        // wide around the rim, each point shows the backdrop from further in, by the slope of a dome that sags
        // lensDome of the shorter side, so the rim magnifies and wraps what is under the glass instead of pulling the
        // surroundings in. Across and down are separate arcs over the half width and half height (glisse's splay 1),
        // each scaled so its slope averages one half, and the band fades in with an erf ramp. The pull is at most half
        // of lensStrength of the shorter side, as glisse's 8-bit map holds. A backwards L has no one centre: there the
        // pull follows the edge's normal, with the dome across the arms. Returns the offset in DIPs.
        float2 domeShift(float2 p, float2 h, float f, float d, float2 n)
        {
            float m = domeSide(h);
            float sag = max(0.01, min(lensDome * m, m * 0.5 - 1));

            // Each arc's scale (its mean slope made one half) is worked out in C# the way glisse does it (DomeScale).
            float2 R = (h * h + sag * sag) / (2 * sag);
            float2 ax = min(abs(p), 0.999 * R);
            float2 slopes = sign(p) * ax / sqrt(R * R - ax * ax) * domeScale.xy;

            float hm = m * 0.5;
            float RL = (hm * hm + sag * sag) / (2 * sag);
            float xL = clamp(hm + d, 0, 0.999 * RL);
            float slopeL = xL / sqrt(RL * RL - xL * xL) * domeScale.z;

            float2 pull = lerp(slopes, n * slopeL, step(0.5, arms.x) * step(0.5, arms.y));
            return -0.5 * lensStrength * m * clamp(pull * f, -1, 1);
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float2 s = max(size, 1);
            float2 h = max(s * 0.5 - inset, 1);
            float r = min(radius, min(h.x, h.y));
            float2 p = uv * s - s * 0.5;

            // Distance and (forward-difference) normal of the outline.
            float d = sdShape(p, h, r);
            float2 n = float2(sdShape(p + float2(0.5, 0), h, r) - d, sdShape(p + float2(0, 0.5), h, r) - d);
            n = n / max(length(n), 0.0001);

            // Convex bevel (quarter circle): flat in the middle, steepest at the edge. Sampling outward squeezes the
            // backdrop from just beyond the edge into the rim, the way a glass lens wraps its surroundings. Like
            // Apple's, the lens is about as wide as the corner is round (at least about half the radius), so a
            // round button bends most of its face and the bend runs out gently instead of streaking in a hairline.
            float b = max(bevel, min(r, min(h.x, h.y)) * 0.55);
            float t = saturate(1 + d / b);
            float bend = 1 - sqrt(saturate(1 - t * t));
            float band = domeBand(p, h, r, d);
            float2 dome = domeShift(p, h, band, d, n);
            float2 shift = lerp(n * refraction * bend, dome, lensStyle) / s;
            float2 at = uv + shift;

            // The rim stays sharp (and splits colour) so the lensing reads; the flat middle is frosted for legibility.
            // Towards the edge the bevel squeezes many pixels of backdrop into each one, and a single sample per pixel
            // turned that into streaks and a hard stop. Five samples across the pixel's width (±1 px of bevel) average
            // what it covers, so the bend runs out smoothly.
            // The glisse lens splits colour the way glisse does: red bends 0.2 x lensDispersion further than blue, green
            // halfway. glisse bends a backdrop blurred by 3 px; here the five samples sit on a small square lensBlur
            // across (one in the middle, four on the diagonals) and spread the pull by +-12 %, which softens the band
            // as much and smooths where the dome folds the backdrop over.
            float3 spectrum = lerp(float3(1 + dispersion, 1, 1 - dispersion),
                                   float3(1 + 0.2 * lensDispersion, 1 + 0.1 * lensDispersion, 1), lensStyle);
            float4 rim = 0;
            for (int k = -2; k <= 2; k++)
            {
                float tk = saturate(t + k * 0.5 / b);
                float2 soft = float2(2 * abs(k) - 3, -sign(k)) * (0.7071 * lensBlur * abs(sign(k)));
                float2 sk = lerp(n * refraction * (1 - sqrt(saturate(1 - tk * tk))), dome * (1 + 0.06 * k) + soft, lensStyle) / s;
                rim.r  += tex2D(input, uv + sk * spectrum.r).r;
                rim.ga += tex2D(input, uv + sk * spectrum.g).ga;
                rim.b  += tex2D(input, uv + sk * spectrum.b).b;
            }
            rim /= 5;

            // Frost: 16 taps on a golden-angle spiral with Gaussian weights, a smooth blur rather than the stairs and
            // ghost copies of a few even disc taps (a bright line behind the glass showed as two or three).
            float4 frosted = 0;
            float weights = 0;
            for (int i = 0; i < 16; i++)
            {
                float rr = sqrt((i + 0.5) / 16);
                float w = exp(-2.2 * rr * rr);
                frosted += w * tex2D(input, at + float2(cos(i * 2.39996), sin(i * 2.39996)) * rr * frost / s);
                weights += w;
            }
            frosted /= weights;

            // Frosted to clear towards the edge, so where the bend begins doesn't show as a line; the outer part of
            // the (now wider) bevel stays sharp so the bend reads. The glisse lens: frosted in the flat middle, the
            // bent (and split) backdrop across its band.
            float4 c = lerp(frosted, rim, lerp(smoothstep(0.25, 1.0, t), smoothstep(0.1, 0.6, band), lensStyle));

            // Reflection: the colour of what lies beyond the edge (from 40 % to 100 % of reflectReach out) spills into
            // a soft band inside it, like bright surroundings mirrored in thick glass. It reaches across gaps the lens
            // alone would squeeze into a hairline. Spread along the edge too (±30 % of the reach), so it is a colour
            // glow rather than a copy of the text rows beside it. Equal weights turned a sharp edge out there (where
            // a thumbnail ends) into five even stairs along the rim; tent weights, with every other row shifted by
            // half a step, fade it in instead. Off at reflection = 0.
            float spill = saturate(1 + d / max(reflectWidth, 1));
            float2 along = float2(-n.y, n.x);
            float4 refl = 0;
            for (int j = 1; j <= 4; j++)
                for (int k = -2; k <= 2; k++)
                    refl += (3 - abs(k)) * tex2D(input, uv + (n * (0.2 + 0.2 * j) + along * (0.15 * k + 0.075 * (j % 2 - 0.5))) * reflectReach / s);
            c = lerp(c, refl / 36, saturate(reflection) * spill * spill);

            float l = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
            c.rgb = lerp(l.xxx, c.rgb, saturation) * brightness;

            // Specular rim like iOS: a crisp hairline right inside the edge with a soft glow under it, bright towards
            // the top-left and bottom-right corners and fading to a faint line towards the other two. Where along the
            // edge a point lies counts as much as which way the edge faces, so a straight edge fades along its length
            // (bright at the lit corner, dim at the far one) the way Apple's does; on a circle both are the normal.
            float2 dir = normalize(n + 0.8 * p / max(h, 1));
            float facing = dot(dir, float2(-0.7071, -0.7071));
            float lit = pow(saturate(facing), 2.5) + 0.8 * pow(saturate(-facing), 2.5);
            float edgeLine = saturate(1 - abs(d + 0.75) / 0.85);
            float glow = exp(min(d, 0) / 3.0) * (1 - edgeLine);
            c.rgb += (1 - lensStyle) * highlight * (edgeLine * (0.2 + 1.5 * lit) + 0.5 * glow * lit) * c.a;

            // glisse's rim, as iOS 26.1 draws it: a white line brightest at the very edge and gone 1.22 DIPs in. A
            // light from the top left lights the left side and top, its reflection the right side and bottom; it fades
            // out over the two corners between and is a little wider on the two it faces. Under it the 0.5 DIP edge
            // line (white in dark mode, black in light mode).
            float inside = -d;
            float faces = abs(n.x + n.y);   // 1 along the sides, sqrt 2 on the lit corners, 0 on the others
            float alongRim = max(inside, 0) / (1.22 * sqrt(max(1, faces)));
            float shine = alongRim < 1 ? rimShine * pow(min(1, faces), 1.5) * (1 - pow(saturate(alongRim), 1.5)) : 0;
            shine *= step(-0.5, inside) * lensStyle;
            float edgeMark = abs(rimEdge) * saturate((0.75 - inside) / 0.5) * step(-0.5, inside) * (1 - shine) * lensStyle;
            c.rgb = c.rgb * (1 - shine - edgeMark) + (shine + edgeMark * step(0, rimEdge)) * c.a;

            c.rgb = min(c.rgb, c.a);
            return c;
        }
        """;

    private static PixelShader? _shader;
    private static bool _compileFailed;
    private static Task<byte[]?>? _bytecode;
    private static readonly object BytecodeLock = new();

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty(nameof(Input), typeof(LiquidGlassEffect), 0);

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(Size),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(new Size(1, 1), ShapeChanged(0)));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(16.0, PixelShaderConstantCallback(1)));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(nameof(Saturation), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(1.8, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty RefractionProperty = DependencyProperty.Register(nameof(Refraction), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(10.0, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty BevelProperty = DependencyProperty.Register(nameof(Bevel), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(14.0, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty DispersionProperty = DependencyProperty.Register(nameof(Dispersion), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.2, PixelShaderConstantCallback(6)));

    public static readonly DependencyProperty HighlightProperty = DependencyProperty.Register(nameof(Highlight), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.4, PixelShaderConstantCallback(7)));

    public static readonly DependencyProperty FrostProperty = DependencyProperty.Register(nameof(Frost), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(10.0, PixelShaderConstantCallback(9)));

    public static readonly DependencyProperty InsetProperty = DependencyProperty.Register(nameof(Inset), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, ShapeChanged(8)));

    public static readonly DependencyProperty ReflectionProperty = DependencyProperty.Register(nameof(Reflection), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(10)));

    public static readonly DependencyProperty ReflectionReachProperty = DependencyProperty.Register(nameof(ReflectionReach), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(40.0, PixelShaderConstantCallback(11)));

    public static readonly DependencyProperty ReflectionWidthProperty = DependencyProperty.Register(nameof(ReflectionWidth), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(24.0, PixelShaderConstantCallback(12)));

    public static readonly DependencyProperty ArmsProperty = DependencyProperty.Register(nameof(Arms), typeof(Size),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(new Size(0, 0), ShapeChanged(13)));

    public static readonly DependencyProperty LensStyleProperty = DependencyProperty.Register(nameof(LensStyle), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(1.0, PixelShaderConstantCallback(14)));

    public static readonly DependencyProperty LensStrengthProperty = DependencyProperty.Register(nameof(LensStrength), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.38, PixelShaderConstantCallback(15)));

    public static readonly DependencyProperty LensDepthProperty = DependencyProperty.Register(nameof(LensDepth), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.25, PixelShaderConstantCallback(16)));

    public static readonly DependencyProperty LensDomeProperty = DependencyProperty.Register(nameof(LensDome), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.5, ShapeChanged(17)));

    public static readonly DependencyProperty LensMaxSideProperty = DependencyProperty.Register(nameof(LensMaxSide), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(72.0, ShapeChanged(23)));

    public static readonly DependencyProperty LensBlurProperty = DependencyProperty.Register(nameof(LensBlur), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(3.0, PixelShaderConstantCallback(22)));

    private static readonly DependencyProperty DomeScaleProperty = DependencyProperty.Register("DomeScale", typeof(Point3D),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(new Point3D(1, 1, 1), PixelShaderConstantCallback(21)));

    /// <summary>Sets the shader constant, then works out the dome's scales again for the new shape.</summary>
    private static PropertyChangedCallback ShapeChanged(int register)
    {
        var constant = PixelShaderConstantCallback(register);
        return (d, e) =>
        {
            constant(d, e);
            ((LiquidGlassEffect)d).UpdateDomeScale();
        };
    }

    /// <summary>
    /// The glisse lens's arcs, scaled so their slope averages one half over the half width or height (glisse's
    /// <c>arc</c>, the same 200-step trapezoid, so the bend matches glisse's to the pixel: near a hemisphere its last,
    /// steep sample weighs in far more than in the integral, which softens the short side's bend). The shader needs one
    /// scale per arc, across, down and across an L's arms; they change only with the shape.
    /// </summary>
    private void UpdateDomeScale()
    {
        var s = Size;
        double hx = Math.Max(s.Width * 0.5 - Inset, 1), hy = Math.Max(s.Height * 0.5 - Inset, 1);
        var arms = Arms;
        bool l = arms.Width >= 0.5 && arms.Height >= 0.5;
        double m = l ? Math.Max(Math.Min(Math.Min(arms.Width, hx * 2), Math.Min(arms.Height, hy * 2)), 1) : 2 * Math.Min(hx, hy);
        m = Math.Min(m, Math.Max(LensMaxSide, 1));
        double sag = Math.Max(0.01, Math.Min(LensDome * m, m * 0.5 - 1));
        SetValue(DomeScaleProperty, new Point3D(ArcScale(sag, hx), ArcScale(sag, hy), ArcScale(sag, m * 0.5)));
    }

    private static double ArcScale(double sag, double half)
    {
        double radius = (half * half + sag * sag) / (2 * sag);
        double sum = 0;
        for (int i = 0; i <= 200; i++)
        {
            double x = i / 200.0 * half;
            double slope = x / Math.Sqrt(Math.Max(radius * radius - x * x, 1e-9));
            sum += i == 0 || i == 200 ? slope / 2 : slope;
        }
        double mean = sum / 200;
        return mean > 0 ? 0.5 / mean : 1;
    }

    public static readonly DependencyProperty LensDispersionProperty = DependencyProperty.Register(nameof(LensDispersion), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.4, PixelShaderConstantCallback(18)));

    public static readonly DependencyProperty RimShineProperty = DependencyProperty.Register(nameof(RimShine), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.13, PixelShaderConstantCallback(19)));

    public static readonly DependencyProperty RimEdgeProperty = DependencyProperty.Register(nameof(RimEdge), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.08, PixelShaderConstantCallback(20)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    /// <summary>Size of the effect's element in DIPs (the card plus <see cref="Inset"/> on every side).</summary>
    public Size Size { get => (Size)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    /// <summary>1 = unchanged, Apple materials use about 1.8.</summary>
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => (double)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    /// <summary>How far (DIPs) beyond the edge the rim samples the backdrop from.</summary>
    public double Refraction { get => (double)GetValue(RefractionProperty); set => SetValue(RefractionProperty, value); }
    /// <summary>Width (DIPs) of the refracting rim.</summary>
    public double Bevel { get => (double)GetValue(BevelProperty); set => SetValue(BevelProperty, value); }
    /// <summary>Colour split along the rim, as a fraction of the refraction.</summary>
    public double Dispersion { get => (double)GetValue(DispersionProperty); set => SetValue(DispersionProperty, value); }
    /// <summary>Brightness added along the rim where it faces the light.</summary>
    public double Highlight { get => (double)GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }
    /// <summary>Radius (DIPs) of the frost in the flat middle; the rim fades back to the sharp, lensed backdrop.</summary>
    public double Frost { get => (double)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }
    /// <summary>How far (DIPs) the element extends past the card on every side; the rim lenses in from there.</summary>
    public double Inset { get => (double)GetValue(InsetProperty); set => SetValue(InsetProperty, value); }
    /// <summary>How strongly (0-1) the colour beyond the edge is mirrored into a band inside the rim.</summary>
    public double Reflection { get => (double)GetValue(ReflectionProperty); set => SetValue(ReflectionProperty, value); }
    /// <summary>How far (DIPs) beyond the edge the reflection looks; must stay within <see cref="Inset"/>.</summary>
    public double ReflectionReach { get => (double)GetValue(ReflectionReachProperty); set => SetValue(ReflectionReachProperty, value); }
    /// <summary>Width (DIPs) of the soft band inside the edge the reflection shows in.</summary>
    public double ReflectionWidth { get => (double)GetValue(ReflectionWidthProperty); set => SetValue(ReflectionWidthProperty, value); }
    /// <summary>Backwards-L outline (see <see cref="LiquidGlassPanel.ReverseLArms"/>): right arm width and bottom arm
    /// height in DIPs; 0×0 = the rounded rectangle.</summary>
    public Size Arms { get => (Size)GetValue(ArmsProperty); set => SetValue(ArmsProperty, value); }
    /// <summary>0 = the rim squeezes in the surroundings (Refraction, Bevel, Dispersion, Highlight); 1 = glisse's dome
    /// lens and rim (the Lens* and Rim* values); values between blend the two.</summary>
    public double LensStyle { get => (double)GetValue(LensStyleProperty); set => SetValue(LensStyleProperty, value); }
    /// <summary>glisse lens: how far in the rim reaches for what it shows, as a share of the shorter side (0.38; glisse 0.45).</summary>
    public double LensStrength { get => (double)GetValue(LensStrengthProperty); set => SetValue(LensStrengthProperty, value); }
    /// <summary>glisse lens: width of the band around the rim that bends, as a share of the shorter side (0.25).</summary>
    public double LensDepth { get => (double)GetValue(LensDepthProperty); set => SetValue(LensDepthProperty, value); }
    /// <summary>glisse lens: sag of the dome whose slope sets the bend, as a share of the shorter side (0.5).</summary>
    public double LensDome { get => (double)GetValue(LensDomeProperty); set => SetValue(LensDomeProperty, value); }
    /// <summary>glisse lens: how much further red bends than blue (at 1, a fifth further; glisse uses 0.4).</summary>
    public double LensDispersion { get => (double)GetValue(LensDispersionProperty); set => SetValue(LensDispersionProperty, value); }
    /// <summary>glisse lens: the shorter side the lens's shares are taken of is at most this many DIPs (72), so a big surface
    /// keeps a narrow bent rim instead of one that grows with it. Small surfaces aren't affected.</summary>
    public double LensMaxSide { get => (double)GetValue(LensMaxSideProperty); set => SetValue(LensMaxSideProperty, value); }
    /// <summary>glisse lens: how far (DIPs) the rim's samples spread, so the bent band is as soft as glisse's (3).</summary>
    public double LensBlur { get => (double)GetValue(LensBlurProperty); set => SetValue(LensBlurProperty, value); }
    /// <summary>glisse rim: alpha of the white line along the lit edges (glisse: 0.13 dark, 0.3 light).</summary>
    public double RimShine { get => (double)GetValue(RimShineProperty); set => SetValue(RimShineProperty, value); }
    /// <summary>glisse rim: the 0.5 DIP edge line's alpha; positive draws it white, negative black.</summary>
    public double RimEdge { get => (double)GetValue(RimEdgeProperty); set => SetValue(RimEdgeProperty, value); }

    private LiquidGlassEffect(PixelShader shader)
    {
        PixelShader = shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SizeProperty);
        UpdateShaderValue(CornerRadiusProperty);
        UpdateShaderValue(SaturationProperty);
        UpdateShaderValue(BrightnessProperty);
        UpdateShaderValue(RefractionProperty);
        UpdateShaderValue(BevelProperty);
        UpdateShaderValue(DispersionProperty);
        UpdateShaderValue(HighlightProperty);
        UpdateShaderValue(InsetProperty);
        UpdateShaderValue(FrostProperty);
        UpdateShaderValue(ReflectionProperty);
        UpdateShaderValue(ReflectionReachProperty);
        UpdateShaderValue(ReflectionWidthProperty);
        UpdateShaderValue(ArmsProperty);
        UpdateShaderValue(LensStyleProperty);
        UpdateShaderValue(LensStrengthProperty);
        UpdateShaderValue(LensDepthProperty);
        UpdateShaderValue(LensDomeProperty);
        UpdateShaderValue(LensDispersionProperty);
        UpdateShaderValue(RimShineProperty);
        UpdateShaderValue(RimEdgeProperty);
        UpdateShaderValue(LensBlurProperty);
        UpdateShaderValue(LensMaxSideProperty);
        UpdateDomeScale();
        UpdateShaderValue(DomeScaleProperty);
    }

    public static LiquidGlassEffect? TryCreate()
    {
        // ps_3_0 effects are silently dropped under software rendering; fall back to plain blur there.
        if (!RenderCapability.IsPixelShaderVersionSupported(3, 0)) return null;
        var shader = GetShader();
        return shader == null ? null : new LiquidGlassEffect(shader);
    }

    /// <summary>Starts loading or compiling the shader on a background thread (call early, e.g. at app startup), so
    /// the first glass surface doesn't stall the UI thread (and its opening animation) for the compile.</summary>
    public static void Warmup()
    {
        // The one-time hardware check (~100 ms on first call) and creating the PixelShader must happen on the UI
        // thread: do them while it is idle, once the bytecode is ready, rather than inside the first glass window's
        // opening animation.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        BytecodeTask().ContinueWith(_ => dispatcher.BeginInvoke(
            () => { if (RenderCapability.IsPixelShaderVersionSupported(3, 0)) GetShader(); },
            System.Windows.Threading.DispatcherPriority.ApplicationIdle), TaskScheduler.Default);
    }

    private static Task<byte[]?> BytecodeTask()
    {
        lock (BytecodeLock)
            return _bytecode ??= Task.Run(LoadOrCompile);
    }

    private static byte[]? LoadOrCompile()
    {
        string? cachePath = null;
        try
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Hlsl)))[..16];
            cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VideoConverter", "cache", $"liquidglass-{hash}.ps");
            if (File.Exists(cachePath)) return File.ReadAllBytes(cachePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LiquidGlassEffect: shader cache unreadable: {ex.Message}");
        }

        try
        {
            var bytecode = Compile(Hlsl, "main", "ps_3_0");
            if (cachePath != null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    var tmp = cachePath + ".tmp";
                    File.WriteAllBytes(tmp, bytecode);
                    File.Move(tmp, cachePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"LiquidGlassEffect: shader cache not written: {ex.Message}");
                }
            }
            return bytecode;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LiquidGlassEffect: shader unavailable: {ex.Message}");
            return null;
        }
    }

    private static PixelShader? GetShader()
    {
        if (_shader != null || _compileFailed) return _shader;
        // Normally finished long ago (Warmup at startup); otherwise this waits for the compile once.
        var bytecode = BytecodeTask().GetAwaiter().GetResult();
        if (bytecode == null) { _compileFailed = true; return null; }
        try
        {
            var shader = new PixelShader();
            shader.SetStreamSource(new MemoryStream(bytecode));
            shader.Freeze();
            _shader = shader;
        }
        catch (Exception ex)
        {
            _compileFailed = true;
            System.Diagnostics.Debug.WriteLine($"LiquidGlassEffect: shader unavailable: {ex.Message}");
        }
        return _shader;
    }

    // ═══ Runtime HLSL compile (d3dcompiler_47.dll ships with Windows 10+) ═══

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] nint GetBufferPointer();
        [PreserveSig] nuint GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] srcData, nuint srcDataSize, string? sourceName, nint defines, nint include,
        string entryPoint, string target, uint flags1, uint flags2, out ID3DBlob? code, out ID3DBlob? errorMsgs);

    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    private static byte[] Compile(string source, string entryPoint, string target)
    {
        var src = Encoding.ASCII.GetBytes(source);
        int hr = D3DCompile(src, (nuint)src.Length, "LiquidGlass.hlsl", 0, 0, entryPoint, target,
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out var code, out var errors);
        try
        {
            if (hr < 0 || code == null)
            {
                var msg = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) : null;
                throw new InvalidOperationException($"D3DCompile 0x{hr:X8}: {msg?.Trim()}");
            }
            var bytes = new byte[(int)code.GetBufferSize()];
            Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (code != null) Marshal.ReleaseComObject(code);
            if (errors != null) Marshal.ReleaseComObject(errors);
        }
    }
}
