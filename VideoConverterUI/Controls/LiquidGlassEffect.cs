using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

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

        // Poisson disc for the frost, unit radius.
        static const float2 taps[12] =
        {
            float2(-0.326, -0.406), float2(-0.840, -0.074), float2(-0.696, 0.457), float2(-0.203, 0.621),
            float2( 0.962, -0.195), float2( 0.473, -0.480), float2( 0.519, 0.767), float2( 0.185, -0.893),
            float2( 0.507, 0.064), float2( 0.896, 0.412), float2(-0.322, -0.933), float2(-0.792, -0.598)
        };

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
            // backdrop from just beyond the edge into the rim, the way a glass lens wraps its surroundings.
            float b = max(bevel, 1);
            float t = saturate(1 + d / b);
            float bend = 1 - sqrt(saturate(1 - t * t));
            float2 shift = n * refraction * bend / s;
            float2 at = uv + shift;

            // The rim stays sharp (and splits colour) so the lensing reads; the flat middle is frosted for legibility.
            // Towards the edge the bevel squeezes many pixels of backdrop into each one, and a single sample per pixel
            // turned that into streaks and a hard stop. Five samples across the pixel's width (±1 px of bevel) average
            // what it covers, so the bend runs out smoothly.
            float4 rim = 0;
            for (int k = -2; k <= 2; k++)
            {
                float tk = saturate(t + k * 0.5 / b);
                float2 sk = n * refraction * (1 - sqrt(saturate(1 - tk * tk))) / s;
                float2 split = sk * dispersion;
                rim.r  += tex2D(input, uv + sk + split).r;
                rim.ga += tex2D(input, uv + sk).ga;
                rim.b  += tex2D(input, uv + sk - split).b;
            }
            rim /= 5;

            float4 frosted = 0;
            for (int i = 0; i < 12; i++)
                frosted += tex2D(input, at + taps[i] * frost / s);
            frosted /= 12;

            // Frosted to clear across the whole bevel, so where the bend begins doesn't show as a line.
            float4 c = lerp(frosted, rim, smoothstep(0.0, 1.0, t));

            // Reflection: the colour of what lies beyond the edge (from 40 % to 100 % of reflectReach out) spills into
            // a soft band inside it, like bright surroundings mirrored in thick glass. It reaches across gaps the lens
            // alone would squeeze into a hairline. Spread along the edge too (±30 % of the reach), so it is a colour
            // glow rather than a copy of the text rows beside it. Off at reflection = 0.
            float spill = saturate(1 + d / max(reflectWidth, 1));
            float2 along = float2(-n.y, n.x);
            float4 refl = 0;
            for (int j = 1; j <= 4; j++)
                for (int k = -2; k <= 2; k++)
                    refl += tex2D(input, uv + (n * (0.2 + 0.2 * j) + along * (0.15 * k)) * reflectReach / s);
            c = lerp(c, refl / 20, saturate(reflection) * spill * spill);

            float l = dot(c.rgb, float3(0.2126, 0.7152, 0.0722));
            c.rgb = lerp(l.xxx, c.rgb, saturation) * brightness;

            // Rim light: key light from the top-left, a weaker reflection on the opposite side.
            float facing = dot(n, float2(-0.51, -0.86));
            float lit = facing * facing * lerp(0.55, 1.0, step(0, facing));
            float edgeLine = saturate(1 - abs(d + 1.0) / 1.2);
            c.rgb += highlight * (edgeLine * (0.3 + 0.7 * lit) + 0.45 * t * t * t * lit) * c.a;
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
        typeof(LiquidGlassEffect), new UIPropertyMetadata(new Size(1, 1), PixelShaderConstantCallback(0)));

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
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(8)));

    public static readonly DependencyProperty ReflectionProperty = DependencyProperty.Register(nameof(Reflection), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(0.0, PixelShaderConstantCallback(10)));

    public static readonly DependencyProperty ReflectionReachProperty = DependencyProperty.Register(nameof(ReflectionReach), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(40.0, PixelShaderConstantCallback(11)));

    public static readonly DependencyProperty ReflectionWidthProperty = DependencyProperty.Register(nameof(ReflectionWidth), typeof(double),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(24.0, PixelShaderConstantCallback(12)));

    public static readonly DependencyProperty ArmsProperty = DependencyProperty.Register(nameof(Arms), typeof(Size),
        typeof(LiquidGlassEffect), new UIPropertyMetadata(new Size(0, 0), PixelShaderConstantCallback(13)));

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
