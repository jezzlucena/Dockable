using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Dockable.Interop;

namespace Dockable.Genie;

/// <summary>
/// A WPF pixel-shader effect that refracts the element it's applied to (the captured backdrop) by
/// offsetting each pixel's UV using a displacement map — strongest at the rounded rim, ~zero in the
/// middle. The HLSL is compiled to ps_2_0 bytecode at runtime (no fxc needed) and loaded as a
/// <see cref="PixelShader"/>. If compilation/loading fails the effect is a no-op pass-through.
/// </summary>
public sealed class RefractionEffect : ShaderEffect
{
    private static readonly PixelShader? Shader = Build();

    // Bilinear so the Gaussian's sub-pixel tap offsets interpolate smoothly (no blocky stepping).
    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(RefractionEffect), 0, SamplingMode.Bilinear);

    public static readonly DependencyProperty DistortionAmountProperty =
        DependencyProperty.Register(nameof(DistortionAmount), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.06, PixelShaderConstantCallback(0)));

    // c1 is reserved for the UV-space pixel size (ddx/ddy), auto-filled by WPF — see the constructor.
    public static readonly DependencyProperty BlurRadiusProperty =
        DependencyProperty.Register(nameof(BlurRadius), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(2)));

    public static readonly DependencyProperty BlurDirectionProperty =
        DependencyProperty.Register(nameof(BlurDirection), typeof(Vector), typeof(RefractionEffect),
            new UIPropertyMetadata(new Vector(1.0, 0.0), PixelShaderConstantCallback(3)));

    public static readonly DependencyProperty SaturationProperty =
        DependencyProperty.Register(nameof(Saturation), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));

    public static readonly DependencyProperty AberrationProperty =
        DependencyProperty.Register(nameof(Aberration), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(5)));

    public static readonly DependencyProperty LightPositionProperty =
        DependencyProperty.Register(nameof(LightPosition), typeof(Point), typeof(RefractionEffect),
            new UIPropertyMetadata(new Point(0.5, -0.2), PixelShaderConstantCallback(6)));

    public static readonly DependencyProperty SpecularIntensityProperty =
        DependencyProperty.Register(nameof(SpecularIntensity), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(7)));

    public static readonly DependencyProperty ShininessProperty =
        DependencyProperty.Register(nameof(Shininess), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(8.0, PixelShaderConstantCallback(8)));

    public static readonly DependencyProperty RimSharpnessProperty =
        DependencyProperty.Register(nameof(RimSharpness), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(1.0, PixelShaderConstantCallback(9)));

    public static readonly DependencyProperty CornerFractionProperty =
        DependencyProperty.Register(nameof(CornerFraction), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.5, PixelShaderConstantCallback(10)));

    public static readonly DependencyProperty BezelFractionProperty =
        DependencyProperty.Register(nameof(BezelFraction), typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.6, PixelShaderConstantCallback(11)));

    /// <summary>True when the shader compiled/loaded AND the GPU supports ps_3_0 in hardware. ps_3_0 has
    /// no WPF software-rendering path, so on a software/RDP renderer we report unavailable and the caller
    /// falls back to Acrylic + the rim sheen rather than rendering a blank layer.</summary>
    public static bool IsAvailable =>
        Shader is not null && RenderCapability.IsPixelShaderVersionSupported(3, 0);

    public RefractionEffect()
    {
        if (Shader is null)
            return; // pass-through; caller should check IsAvailable
        PixelShader = Shader;
        // Have WPF fill c1 with the texture coordinate's ddx/ddy (the UV size of one device pixel) so the
        // frosted blur steps by real pixels — aspect-correct on the wide, short bar without us passing its size.
        DdxUvDdyUvRegisterIndex = 1;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(DistortionAmountProperty);
        UpdateShaderValue(BlurRadiusProperty);
        UpdateShaderValue(BlurDirectionProperty);
        UpdateShaderValue(SaturationProperty);
        UpdateShaderValue(AberrationProperty);
        UpdateShaderValue(LightPositionProperty);
        UpdateShaderValue(SpecularIntensityProperty);
        UpdateShaderValue(ShininessProperty);
        UpdateShaderValue(RimSharpnessProperty);
        UpdateShaderValue(CornerFractionProperty);
        UpdateShaderValue(BezelFractionProperty);
    }

    /// <summary>The content to refract (s0). Auto-bound to the element the effect is applied to.</summary>
    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    /// <summary>Refraction strength: how far (in device pixels) the sample is pulled inward at the rim.</summary>
    public double DistortionAmount
    {
        get => (double)GetValue(DistortionAmountProperty);
        set => SetValue(DistortionAmountProperty, value);
    }

    /// <summary>Gaussian blur sigma in device pixels (0 = sharp). One axis of a separable 13-tap pass.</summary>
    public double BlurRadius
    {
        get => (double)GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    /// <summary>Which axis this pass blurs: (1,0) = horizontal, (0,1) = vertical. The two-pass pair
    /// composes into a true 2-D Gaussian (separable), far cheaper than a single 2-D kernel.</summary>
    public Vector BlurDirection
    {
        get => (Vector)GetValue(BlurDirectionProperty);
        set => SetValue(BlurDirectionProperty, value);
    }

    /// <summary>Colour saturation multiplier (1.0 = unchanged, &gt;1 = more vibrant). Apply on a single
    /// pass only — set it on the final (vertical) pass and leave the other at 1.0 so it isn't compounded.</summary>
    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    /// <summary>Chromatic aberration strength (0 = none). Splits R/B along the refraction direction,
    /// weighted by local displacement so colour fringing appears only at the rim. Set on the refracting
    /// (horizontal) pass only — it's gated by the displacement, so it self-disables where distortion is 0.</summary>
    public double Aberration
    {
        get => (double)GetValue(AberrationProperty);
        set => SetValue(AberrationProperty, value);
    }

    /// <summary>Position of the (virtual) light in UV space — drives the angle of the rim specular.
    /// Anchor it near the cursor for a pointer-reactive glint. y &lt; 0 places the light above the bar.</summary>
    public Point LightPosition
    {
        get => (Point)GetValue(LightPositionProperty);
        set => SetValue(LightPositionProperty, value);
    }

    /// <summary>Rim specular highlight strength (0 = none). Additive white glint at the rim, brightest
    /// where the surface faces <see cref="LightPosition"/>, with a matching counter-glint lit from the
    /// point-reflected position (1 - LightPosition) — the opposite rim at the horizontally mirrored X.
    /// Set on the final pass only.</summary>
    public double SpecularIntensity
    {
        get => (double)GetValue(SpecularIntensityProperty);
        set => SetValue(SpecularIntensityProperty, value);
    }

    /// <summary>Specular exponent — higher = tighter, glassier glint; lower = a broader sheen.</summary>
    public double Shininess
    {
        get => (double)GetValue(ShininessProperty);
        set => SetValue(ShininessProperty, value);
    }

    /// <summary>How tightly the specular hugs the very edge (radial concentration): 1 = spread across the
    /// whole bezel, higher = a thin bright line right at the rim, like a glowing border.</summary>
    public double RimSharpness
    {
        get => (double)GetValue(RimSharpnessProperty);
        set => SetValue(RimSharpnessProperty, value);
    }

    /// <summary>Corner radius of the glass rounded-rect, as a fraction of the bar height (matches the
    /// dock's own corner). Applied isotropically in pixels, so the corners stay circular at any width.</summary>
    public double CornerFraction
    {
        get => (double)GetValue(CornerFractionProperty);
        set => SetValue(CornerFractionProperty, value);
    }

    /// <summary>Width of the refracting rim band, as a fraction of the bar height (uniform all around).</summary>
    public double BezelFraction
    {
        get => (double)GetValue(BezelFractionProperty);
        set => SetValue(BezelFractionProperty, value);
    }

    private static PixelShader? Build()
    {
        // ps_3_0: the refraction + 2-pass Gaussian + chromatic aberration + rim specular together exceed
        // ps_2_0's 64-arithmetic-slot limit (~69). ps_3_0 has ample headroom and is hardware-universal.
        byte[]? bytecode = ShaderCompiler.Compile(Hlsl, "main", "ps_3_0");
        if (bytecode is null)
            return null;
        try
        {
            // PixelShader loads compiled bytecode from a URI; write it to a temp .ps and point at it.
            string path = Path.Combine(Path.GetTempPath(), "Dockable.refraction.ps");
            File.WriteAllBytes(path, bytecode);
            var ps = new PixelShader { UriSource = new Uri(path) };
            return ps;
        }
        catch
        {
            return null;
        }
    }

    // s0 = content being refracted; c0 = refraction strength (px); c1 = ddx/ddy of UV (WPF-filled: one
    // device pixel in UV); c2 = blur sigma (px); c3 = blur axis; c10/c11 = corner/bezel as bar-height
    // fractions. The rim field is computed ANALYTICALLY from a rounded-rect signed-distance field in
    // pixel space (using ddxy → the bar's true pixel size), so the refraction follows the dock's actual
    // rounded rectangle with circular corners at any width — not the oval a stretched square map gave.
    //
    // The blur is ONE axis of a separable Gaussian, applied twice (horizontal then vertical, via nested
    // effects) → a true 2-D Gaussian. 13 dense taps (centre ± 6) spaced sigma*0.4167 px apart reach
    // ~2.5 sigma with no gaps between taps (bilinear-filtered); the weights are the area-normalised
    // Gaussian for that fixed spacing (constant regardless of sigma — spacing scales with sigma).
    private const string Hlsl = @"
sampler2D inputSampler : register(s0);
float  distortion : register(c0);   // refraction strength: px the sample is pulled inward at the rim
float4 ddxy       : register(c1);   // (ddxU, ddxV, ddyU, ddyV) — UV size of one device pixel
float  blurRadius : register(c2);   // Gaussian sigma, in device pixels
float2 blurDir    : register(c3);   // (1,0) horizontal pass / (0,1) vertical pass
float  saturation : register(c4);   // colour vibrance (1 = unchanged); applied on the final pass only
float  aberration : register(c5);   // chromatic aberration strength (0 = none); rim-weighted
float2 lightPos   : register(c6);   // UV position of the light (drives the rim specular angle)
float  specInt    : register(c7);   // rim specular strength (0 = none); final pass only
float  shininess  : register(c8);   // specular exponent (higher = tighter glint)
float  rimSharp   : register(c9);   // radial concentration of the glint (higher = thinner border)
float  cornerFrac : register(c10);  // corner radius as a fraction of the bar height
float  bezelFrac  : register(c11);  // rim band width as a fraction of the bar height

// Analytic rounded-rectangle rim field in device-pixel space (aspect-correct). Returns the outward unit
// normal in .xy and the refraction magnitude (0 across the flat centre → 1 at the rim) in .z.
float3 rimField(float2 uv)
{
    float2 sizePx = float2(1.0 / ddxy.x, 1.0 / ddxy.w);
    float2 bb = sizePx * 0.5;
    float h = sizePx.y;
    float R = min(cornerFrac * h, min(bb.x, bb.y)); // circular corners, clamped to half the short side
    float W = max(bezelFrac * h, 1e-3);

    float2 pc = (uv - 0.5) * sizePx;                 // centred pixel coords
    float2 q = abs(pc) - bb + R;
    float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - R; // < 0 inside
    float dist = -sd;                                 // px from the nearest edge

    float2 n = (max(q.x, q.y) > 0.0)
        ? normalize(max(q, 0.0) + 1e-6) * sign(pc)    // on a corner arc
        : ((q.x > q.y) ? float2(sign(pc.x), 0.0) : float2(0.0, sign(pc.y))); // on a straight edge

    // Convex-squircle surface profile → tangential refraction fraction (bounded, spikes at the rim).
    float x = saturate(dist / W);
    float u = 1.0 - x;
    float u4 = u * u * u * u;
    float slope = (u * u * u) * pow(max(1.0 - u4, 1e-4), -0.75);
    float m = (slope / sqrt(1.0 + slope * slope)) * step(0.0, dist);
    return float3(n, m);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float3 rf = rimField(uv);
    float2 n = rf.xy;                                 // outward rim normal (unit)
    float  m = rf.z;                                  // refraction magnitude (0 centre → 1 rim)

    // Refraction: pull the sample inward (-n) by up to `distortion` px at the rim, converted to UV.
    float2 ruv = uv - n * (m * distortion) * float2(ddxy.x, ddxy.w);

    // One tap step along the blur axis, in UV. ddxy.x = UV width of a device pixel, ddxy.w = its height.
    float2 step = float2(ddxy.x, ddxy.w) * blurDir * (blurRadius * 0.41667);

    float4 c  = tex2D(inputSampler, ruv) * 0.16729;
    c += (tex2D(inputSampler, ruv + step)        + tex2D(inputSampler, ruv - step))        * 0.15338;
    c += (tex2D(inputSampler, ruv + step * 2.0)  + tex2D(inputSampler, ruv - step * 2.0))  * 0.11823;
    c += (tex2D(inputSampler, ruv + step * 3.0)  + tex2D(inputSampler, ruv - step * 3.0))  * 0.07660;
    c += (tex2D(inputSampler, ruv + step * 4.0)  + tex2D(inputSampler, ruv - step * 4.0))  * 0.04170;
    c += (tex2D(inputSampler, ruv + step * 5.0)  + tex2D(inputSampler, ruv - step * 5.0))  * 0.01910;
    c += (tex2D(inputSampler, ruv + step * 6.0)  + tex2D(inputSampler, ruv - step * 6.0))  * 0.00734;

    // Chromatic aberration: glass splits wavelengths most where it bends light hardest — the rim.
    // Sample R/B shifted along the refraction direction (-n), weighted by the rim magnitude m so the
    // fringe fades to nothing across the flat centre.
    float2 caDir = -n * (m * distortion * 0.25) * float2(ddxy.x, ddxy.w);
    float k = saturate(m * aberration);
    c.r = lerp(c.r, tex2D(inputSampler, ruv + caDir).r, k);
    c.b = lerp(c.b, tex2D(inputSampler, ruv - caDir).b, k);

    // Vibrance: pull each channel away from the perceptual grey (Rec.601 luma). Works on premultiplied
    // alpha since luma + lerp scale linearly with alpha. saturation = 1 leaves the colour untouched.
    float luma = dot(c.rgb, float3(0.299, 0.587, 0.114));
    c.rgb = lerp(luma.xxx, c.rgb, saturation);

    // Rim specular: light the outward normal (n) where it faces lightPos, plus a second counter-glint
    // from a bounce light point-reflected through the bar's centre (1 - lightPos, i.e. below the bar at
    // the horizontally mirrored X) — so a glint on the top-left rim pairs with one on the bottom-right.
    // Both are added crisp on top of the blurred glass; the rim mask m (already 0→1) is raised to
    // rimSharp so the glints collapse to thin lines hugging the edge — like a glowing border.
    // specInt = 0 (the refraction pass) makes it a no-op.
    float edge = pow(saturate(m), rimSharp);
    float2 ldir  = normalize(lightPos - uv);
    float2 ldir2 = normalize((1.0 - lightPos) - uv);
    float spec = pow(saturate(dot(n, ldir)), shininess)
               + pow(saturate(dot(n, ldir2)), shininess);
    c.rgb += spec * edge * specInt;
    return c;
}";
}
