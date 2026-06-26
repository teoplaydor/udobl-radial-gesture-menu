// Glass: refract the captured desktop through the bevels, keep it 1:1 with the real screen
// (BgScale = perspective projection factor), and emboss the bevels for a 3D button look.
sampler2D Input  : register(s0);
sampler2D BgTex  : register(s1);
sampler2D HgtTex : register(s2);

float2 Texel       : register(c0);
float  Strength    : register(c1);
float  Tint        : register(c2);
float2 GlobalShift : register(c3);
float  BgScale     : register(c4);   // disc-on-screen / window  → keeps bg 1:1
float2 LightDir    : register(c5);
float  Emboss      : register(c6);

float4 main(float2 uv : TEXCOORD0) : COLOR
{
    float h  = tex2D(HgtTex, uv).a;
    float hr = tex2D(HgtTex, uv + float2(Texel.x, 0)).a;
    float hd = tex2D(HgtTex, uv + float2(0, Texel.y)).a;
    float2 grad = float2(hr - h, hd - h);

    float2 bgUv = 0.5 + (uv - 0.5) * BgScale;     // 1:1 with the actual desktop behind the disc
    float2 off  = grad * Strength + GlobalShift * h;

    float4 bg = tex2D(BgTex, bgUv + off);
    float4 fg = tex2D(Input, uv);
    float  g  = saturate(h * 1.3) * Tint;
    float4 outc = fg + (bg * g) * (1.0 - fg.a);

    float e = (grad.x * LightDir.x + grad.y * LightDir.y) * Emboss; // bevel lighting → 3D
    outc.rgb = saturate(outc.rgb + e * outc.a);
    return outc;
}
