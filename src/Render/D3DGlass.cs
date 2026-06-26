using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Matrix = SharpDX.Matrix;

namespace Udobl.Render
{
    /// <summary>
    /// Real Direct3D 11 glass renderer. Each cell is an extruded prism; the pixel shader does
    /// screen-space refraction + reflection + Fresnel by sampling a captured environment texture
    /// (the whole screen), so every visible face refracts/reflects the area behind/around it.
    /// Renders offscreen and copies into a reusable WriteableBitmap (works headless + verifiable).
    /// </summary>
    public sealed class D3DGlass : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GVertex
        {
            public Vector3 Pos;
            public Vector3 Nrm;
            public Vector3 Tint;
            public GVertex(Vector3 p, Vector3 n, Vector3 t) { Pos = p; Nrm = n; Tint = t; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CB
        {
            public Matrix WVP;
            public Matrix World;
            public Vector4 CamPos;
            public Vector4 ScreenMap; // u0, v0, du, dv : env-uv = (u0 + px.x*du, v0 + px.y*dv)
            public Vector4 Params;    // refract, reflect, fresnelPow, pulse
            public Vector4 Hi;        // hiCenterRad, hiHalfRad, hiOn, _
        }

        private const string Hlsl = @"
cbuffer CB : register(b0) {
  row_major float4x4 WVP;
  row_major float4x4 World;
  float4 CamPos;
  float4 ScreenMap;
  float4 Params;
  float4 Hi;
};
Texture2D EnvTex : register(t0);
SamplerState Samp : register(s0);

struct VSIn  { float3 pos:POSITION; float3 nrm:NORMAL; float3 tint:COLOR; };
struct VSOut { float4 pos:SV_POSITION; float3 wpos:WPOS; float3 wn:WNORMAL; float3 tint:COLOR; float3 opos:OPOS; };

VSOut VS(VSIn i){
  VSOut o;
  o.pos  = mul(float4(i.pos,1), WVP);
  o.wpos = mul(float4(i.pos,1), World).xyz;
  o.wn   = normalize(mul(float4(i.nrm,0), World).xyz);
  o.tint = i.tint;
  o.opos = i.pos;
  return o;
}

float4 PS(VSOut i) : SV_TARGET {
  float2 px = i.pos.xy;
  float2 baseUv = ScreenMap.xy + px * ScreenMap.zw;     // -> uv into captured screen
  float3 N = normalize(i.wn);
  float3 V = normalize(CamPos.xyz - i.wpos);
  float ndv = saturate(abs(dot(N, V)));
  float fres = pow(saturate(1.0 - ndv), Params.z);

  float2 refrUv = baseUv + N.xy * Params.x * (0.6 + 0.4*(1-ndv));
  float3 refr = EnvTex.SampleLevel(Samp, refrUv, 0).rgb;

  float3 R = reflect(-V, N);
  float2 reflUv = baseUv + R.xy * Params.y;
  float3 refl = EnvTex.SampleLevel(Samp, reflUv, 0).rgb;

  float3 glass = lerp(refr, refl, fres);
  glass = glass * (0.5 + 0.5 * i.tint) + i.tint * 0.12;

  // edge sheen in the CELL's colour (грани в нужный цвет) + gentle pulse
  float edge = pow(saturate(1.0 - ndv), 3.0);
  float3 sheen = lerp(i.tint, float3(1.0,1.0,1.0), 0.35);
  glass += sheen * edge * (0.5 + 0.5*Params.w);

  // highlight the selected cell (by object-space angle)
  if (Hi.z > 0.5) {
    float ang = atan2(i.opos.x, i.opos.y); if (ang < 0) ang += 6.2831853;
    float d = abs(ang - Hi.x); d = min(d, 6.2831853 - d);
    if (d < Hi.y) glass += i.tint * 0.28 + 0.14;
  }

  float alpha = saturate(0.84 + 0.16*fres);
  return float4(saturate(glass), alpha);
}
";

        // Overlay sprite shader: a premultiplied textured quad expanded from SV_VertexID (no vertex buffer).
        private const string SpriteHlsl = @"
cbuffer SB : register(b1) { float4 Rect; }; // x0,y0,x1,y1 in NDC (y up)
Texture2D Tex : register(t0);
SamplerState Samp : register(s0);
struct SO { float4 pos:SV_POSITION; float2 uv:TEXCOORD; };
SO SVS(uint id : SV_VertexID) {
  SO o;
  float x = (id == 1 || id == 3) ? Rect.z : Rect.x;
  float y = (id >= 2) ? Rect.w : Rect.y;
  o.pos = float4(x, y, 0, 1);
  o.uv = float2((id == 1 || id == 3) ? 1.0 : 0.0, (id >= 2) ? 1.0 : 0.0);
  return o;
}
float4 SPS(SO i) : SV_TARGET { return Tex.SampleLevel(Samp, i.uv, 0); } // texture is premultiplied BGRA
";

        private readonly int _w, _h;
        private readonly Device _device;
        private readonly DeviceContext _ctx;
        private readonly Texture2D _rt;
        private readonly RenderTargetView _rtv;
        private readonly Texture2D _depth;
        private readonly DepthStencilView _dsv;
        private readonly VertexShader _vs;
        private readonly PixelShader _ps;
        private readonly InputLayout _layout;
        private readonly Buffer _cbuf;
        private readonly SamplerState _sampler;
        private readonly BlendState _blend;
        private readonly RasterizerState _raster;
        private readonly DepthStencilState _depthState;
        private readonly Texture2D[] _stage = new Texture2D[2]; // double-buffered readback (no per-frame GPU stall)
        private int _stageIdx;
        private bool _stageHasPrev;
        private readonly Texture2D _resolve;
        private readonly RenderTargetView _resolveRtv;
        private readonly int _samples;
        private const double DishDeg = 11; // each cell tilts a little in its own plane (faceted dome)

        // In-scene overlay sprites (icons + hub) so the whole menu is ONE GPU surface (no WPF overlay).
        private readonly VertexShader _spriteVs;
        private readonly PixelShader _spritePs;
        private readonly Buffer _spriteCb;
        private readonly BlendState _spriteBlend;
        private sealed class Sprite { public Texture2D Tex; public ShaderResourceView Srv; public Vector3 Anchor; public bool Centered; public int W, H; }
        private readonly List<Sprite> _iconSprites = new List<Sprite>();
        private Sprite _hubSprite;

        private Texture2D _envTex;
        private ShaderResourceView _envSrv;
        private Buffer _vbuf, _ibuf;
        private int _indexCount;
        private WriteableBitmap _wb;
        private Matrix _lastWVP = Matrix.Identity;

        public Device Device => _device;

        private double _u0, _v0, _du = 1.0 / 600, _dv = 1.0 / 600;

        public string Driver { get; private set; }

        private const double Unit = 300.0;
        private const double CamDistance = 3.1;
        private const double FovDeg = 46;

        public D3DGlass(int w, int h)
        {
            _w = w; _h = h;
            try { _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport); Driver = "Hardware"; }
            catch { _device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport); Driver = "WARP"; }
            _ctx = _device.ImmediateContext;

            int s = 4;
            try { if (_device.CheckMultisampleQualityLevels(Format.B8G8R8A8_UNorm, 4) <= 0) s = 1; } catch { s = 1; }
            _samples = s;
            var sd = new SampleDescription(_samples, 0);

            _rt = new Texture2D(_device, new Texture2DDescription
            {
                Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = sd,
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget, CpuAccessFlags = CpuAccessFlags.None,
            });
            _rtv = new RenderTargetView(_device, _rt);
            _depth = new Texture2D(_device, new Texture2DDescription
            {
                Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                Format = Format.D32_Float, SampleDescription = sd,
                Usage = ResourceUsage.Default, BindFlags = BindFlags.DepthStencil,
            });
            _dsv = new DepthStencilView(_device, _depth);
            _resolve = new Texture2D(_device, new Texture2DDescription
            {
                Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget, // overlay sprites are drawn into it
            });
            _resolveRtv = new RenderTargetView(_device, _resolve);
            for (int i = 0; i < 2; i++)
                _stage[i] = new Texture2D(_device, new Texture2DDescription
                {
                    Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CpuAccessFlags = CpuAccessFlags.Read,
                });

            // Dispose the compiled blobs + input signature after building the shaders (they keep copies).
            using (var vsCode = ShaderBytecode.Compile(Hlsl, "VS", "vs_4_0"))
            using (var psCode = ShaderBytecode.Compile(Hlsl, "PS", "ps_4_0"))
            using (var sig = ShaderSignature.GetInputSignature(vsCode))
            {
                _vs = new VertexShader(_device, vsCode);
                _ps = new PixelShader(_device, psCode);
                _layout = new InputLayout(_device, sig, new[]
                {
                    new InputElement("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElement("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
                    new InputElement("COLOR", 0, Format.R32G32B32_Float, 24, 0),
                });
            }
            using (var svs = ShaderBytecode.Compile(SpriteHlsl, "SVS", "vs_4_0"))
            using (var sps = ShaderBytecode.Compile(SpriteHlsl, "SPS", "ps_4_0"))
            {
                _spriteVs = new VertexShader(_device, svs);
                _spritePs = new PixelShader(_device, sps);
            }
            _cbuf = new Buffer(_device, Utilities.SizeOf<CB>(), ResourceUsage.Default,
                BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            _spriteCb = new Buffer(_device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never, MinimumLod = 0, MaximumLod = float.MaxValue,
            });
            var bd = new BlendStateDescription();
            bd.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = BlendOption.SourceAlpha, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One, DestinationAlphaBlend = BlendOption.InverseSourceAlpha, AlphaBlendOperation = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
            };
            _blend = new BlendState(_device, bd);

            // Premultiplied "over" for sprites (textures are premultiplied; dest glass is premultiplied).
            var sbd = new BlendStateDescription();
            sbd.RenderTarget[0] = new RenderTargetBlendDescription
            {
                IsBlendEnabled = true,
                SourceBlend = BlendOption.One, DestinationBlend = BlendOption.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
                SourceAlphaBlend = BlendOption.One, DestinationAlphaBlend = BlendOption.InverseSourceAlpha, AlphaBlendOperation = BlendOperation.Add,
                RenderTargetWriteMask = ColorWriteMaskFlags.All,
            };
            _spriteBlend = new BlendState(_device, sbd);

            _raster = new RasterizerState(_device, new RasterizerStateDescription
            {
                CullMode = CullMode.Back, FillMode = FillMode.Solid, IsDepthClipEnabled = true,
                IsMultisampleEnabled = _samples > 1, IsAntialiasedLineEnabled = false,
            });
            _depthState = new DepthStencilState(_device, new DepthStencilStateDescription
            {
                IsDepthEnabled = true, DepthWriteMask = DepthWriteMask.All, DepthComparison = Comparison.Less,
            });
        }

        /// <summary>Upload captured screen (BGRA). texW/texH = its pixel size.</summary>
        private int _envW, _envH;

        public void SetEnvironment(byte[] bgra, int texW, int texH)
        {
            _envSrv?.Dispose(); _envTex?.Dispose();
            _envW = texW; _envH = texH;
            var pin = GCHandle.Alloc(bgra, GCHandleType.Pinned); // pinned only for the upload below
            try
            {
                _envTex = new Texture2D(_device, new Texture2DDescription
                {
                    Width = texW, Height = texH, ArraySize = 1, MipLevels = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource, // updatable for live reflection
                }, new DataRectangle(pin.AddrOfPinnedObject(), texW * 4));
            }
            finally { pin.Free(); }
            _envSrv = new ShaderResourceView(_device, _envTex);
        }

        /// <summary>Drop the ring geometry so nothing stale is drawn (e.g. descending into an empty group).</summary>
        public void ClearRing()
        {
            _vbuf?.Dispose(); _ibuf?.Dispose();
            _vbuf = null; _ibuf = null; _indexCount = 0;
        }

        /// <summary>Push a fresh capture (same size as SetEnvironment) for live screen reflection.</summary>
        public void UpdateEnvironment(byte[] bgra)
        {
            if (_envTex == null || bgra == null || bgra.Length < _envW * _envH * 4) return;
            _ctx.UpdateSubresource(bgra, _envTex, 0, _envW * 4, 0);
        }

        /// <summary>Map a render-target pixel to an env-texture uv: uv = (u0 + px*du, v0 + py*dv).</summary>
        public void SetMapping(double u0, double v0, double du, double dv) { _u0 = u0; _v0 = v0; _du = du; _dv = dv; }

        public struct SpriteDef { public byte[] Bgra; public int W, H; public Vector3 Anchor; public bool Centered; }

        private Sprite MakeSprite(SpriteDef d)
        {
            if (d.Bgra == null || d.W <= 0 || d.H <= 0 || d.Bgra.Length < d.W * d.H * 4) return null;
            var pin = GCHandle.Alloc(d.Bgra, GCHandleType.Pinned);
            Texture2D tex;
            try
            {
                tex = new Texture2D(_device, new Texture2DDescription
                {
                    Width = d.W, Height = d.H, ArraySize = 1, MipLevels = 1,
                    Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
                }, new DataRectangle(pin.AddrOfPinnedObject(), d.W * 4));
            }
            finally { pin.Free(); }
            return new Sprite { Tex = tex, Srv = new ShaderResourceView(_device, tex), Anchor = d.Anchor, Centered = d.Centered, W = d.W, H = d.H };
        }

        /// <summary>Icons (premultiplied BGRA), uploaded once per ring (re)build.</summary>
        public void SetIcons(IReadOnlyList<SpriteDef> defs)
        {
            foreach (var s in _iconSprites) { s.Srv?.Dispose(); s.Tex?.Dispose(); }
            _iconSprites.Clear();
            if (defs == null) return;
            foreach (var d in defs) { var s = MakeSprite(d); if (s != null) _iconSprites.Add(s); }
        }

        /// <summary>The hub sprite (premultiplied BGRA), re-uploaded only when the highlight changes.</summary>
        public void SetHub(SpriteDef def)
        {
            if (_hubSprite != null) { _hubSprite.Srv?.Dispose(); _hubSprite.Tex?.Dispose(); _hubSprite = null; }
            _hubSprite = MakeSprite(def);
        }

        private void DrawOne(Sprite s)
        {
            double cx, cy;
            if (s.Centered) { cx = _w / 2.0; cy = _h / 2.0; }
            else if (!Project(s.Anchor, out cx, out cy)) return;
            double x0 = cx - s.W / 2.0, y0 = cy - s.H / 2.0, x1 = cx + s.W / 2.0, y1 = cy + s.H / 2.0;
            var rect = new Vector4((float)(x0 / _w * 2 - 1), (float)(1 - y0 / _h * 2),
                                   (float)(x1 / _w * 2 - 1), (float)(1 - y1 / _h * 2));
            _ctx.UpdateSubresource(ref rect, _spriteCb);
            _ctx.VertexShader.SetConstantBuffer(1, _spriteCb);
            _ctx.PixelShader.SetShaderResource(0, s.Srv);
            _ctx.Draw(4, 0);
        }

        private void DrawSprites()
        {
            if (_iconSprites.Count == 0 && _hubSprite == null) return;
            _ctx.OutputMerger.SetRenderTargets((DepthStencilView)null, _resolveRtv);
            _ctx.OutputMerger.SetBlendState(_spriteBlend);
            _ctx.InputAssembler.InputLayout = null;
            _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
            _ctx.VertexShader.Set(_spriteVs);
            _ctx.PixelShader.Set(_spritePs);
            _ctx.PixelShader.SetSampler(0, _sampler);
            foreach (var s in _iconSprites) DrawOne(s);
            if (_hubSprite != null) DrawOne(_hubSprite);
        }

        public void BuildRing(IReadOnlyList<CellDef> cells, double innerR, double outerR, double thickness)
        {
            _vbuf?.Dispose(); _ibuf?.Dispose();
            var verts = new List<GVertex>();
            var idx = new List<int>();
            double h = thickness / 2;
            const int seg = 8;
            foreach (var c in cells)
            {
                int start = verts.Count;
                double a0 = c.CenterDeg - c.HalfDeg, a1 = c.CenterDeg + c.HalfDeg;
                var tint = c.Tint;
                AddArcFace(verts, idx, a0, a1, innerR, outerR, +h, new Vector3(0, 0, 1), tint, seg, false);
                AddArcFace(verts, idx, a0, a1, innerR, outerR, -h, new Vector3(0, 0, -1), tint, seg, true);
                AddWall(verts, idx, a0, a1, outerR, h, tint, seg, true);
                AddWall(verts, idx, a0, a1, innerR, h, tint, seg, false);
                AddRadial(verts, idx, a0, innerR, outerR, h, tint, true);
                AddRadial(verts, idx, a1, innerR, outerR, h, tint, false);

                // Tilt this cell a little in its own plane (faceted dome): rotate about the cell's
                // tangent axis so its outer rim leans toward the viewer — each cell on its own plane.
                if (DishDeg != 0)
                {
                    double tc = c.CenterDeg * Math.PI / 180.0;
                    var axis = new Vector3((float)Math.Cos(tc), (float)(-Math.Sin(tc)), 0);
                    var m = Matrix.RotationAxis(axis, (float)(DishDeg * Math.PI / 180.0));
                    for (int k = start; k < verts.Count; k++)
                    {
                        var gv = verts[k];
                        gv.Pos = Vector3.TransformCoordinate(gv.Pos, m);
                        gv.Nrm = Vector3.TransformNormal(gv.Nrm, m);
                        verts[k] = gv;
                    }
                }
            }
            _vbuf = Buffer.Create(_device, BindFlags.VertexBuffer, verts.ToArray());
            _ibuf = Buffer.Create(_device, BindFlags.IndexBuffer, idx.ToArray());
            _indexCount = idx.Count;
        }

        public struct CellDef
        {
            public double CenterDeg, HalfDeg;
            public Vector3 Tint;
            public CellDef(double center, double half, Vector3 tint) { CenterDeg = center; HalfDeg = half; Tint = tint; }
        }

        public static Vector3 ModelPoint(double rPx, double angDeg, double z)
        {
            double a = angDeg * Math.PI / 180.0, rm = rPx / Unit;
            return new Vector3((float)(rm * Math.Sin(a)), (float)(rm * Math.Cos(a)), (float)z);
        }
        private static Vector3 P(double rPx, double angDeg, double z) => ModelPoint(rPx, angDeg, z);

        private static void AddArcFace(List<GVertex> v, List<int> idx, double a0, double a1, double ri, double ro, double z, Vector3 n, Vector3 tint, int seg, bool flip)
        {
            for (int s = 0; s < seg; s++)
            {
                double aa = a0 + (a1 - a0) * s / seg, bb = a0 + (a1 - a0) * (s + 1) / seg;
                int b = v.Count;
                v.Add(new GVertex(P(ri, aa, z), n, tint)); v.Add(new GVertex(P(ri, bb, z), n, tint));
                v.Add(new GVertex(P(ro, bb, z), n, tint)); v.Add(new GVertex(P(ro, aa, z), n, tint));
                AddQuadIdx(idx, b, flip);
            }
        }
        private static void AddWall(List<GVertex> v, List<int> idx, double a0, double a1, double r, double h, Vector3 tint, int seg, bool outward)
        {
            for (int s = 0; s < seg; s++)
            {
                double aa = a0 + (a1 - a0) * s / seg, bb = a0 + (a1 - a0) * (s + 1) / seg;
                double am = (aa + bb) / 2 * Math.PI / 180.0;
                var n = new Vector3((float)Math.Sin(am), (float)Math.Cos(am), 0); if (!outward) n = -n;
                int b = v.Count;
                v.Add(new GVertex(P(r, aa, -h), n, tint)); v.Add(new GVertex(P(r, bb, -h), n, tint));
                v.Add(new GVertex(P(r, bb, h), n, tint)); v.Add(new GVertex(P(r, aa, h), n, tint));
                AddQuadIdx(idx, b, !outward);
            }
        }
        private static void AddRadial(List<GVertex> v, List<int> idx, double ang, double ri, double ro, double h, Vector3 tint, bool side0)
        {
            double r = ang * Math.PI / 180.0;
            var n = new Vector3((float)Math.Cos(r), (float)(-Math.Sin(r)), 0); if (!side0) n = -n;
            int b = v.Count;
            v.Add(new GVertex(P(ri, ang, -h), n, tint)); v.Add(new GVertex(P(ro, ang, -h), n, tint));
            v.Add(new GVertex(P(ro, ang, h), n, tint)); v.Add(new GVertex(P(ri, ang, h), n, tint));
            AddQuadIdx(idx, b, !side0);
        }
        private static void AddQuadIdx(List<int> idx, int b, bool flip)
        {
            if (!flip) { idx.Add(b); idx.Add(b + 1); idx.Add(b + 2); idx.Add(b); idx.Add(b + 2); idx.Add(b + 3); }
            else { idx.Add(b); idx.Add(b + 2); idx.Add(b + 1); idx.Add(b); idx.Add(b + 3); idx.Add(b + 2); }
        }

        // Render glass (MSAA) -> resolve into _resolve -> draw overlay sprites into _resolve. Angles in degrees.
        private void RenderScene(double tiltX, double tiltY, double pulse, double hiCenterDeg, double hiHalfDeg, bool hiOn, RawColor4 clear)
        {
            double mag = Math.Sqrt(tiltX * tiltX + tiltY * tiltY);
            Matrix world = Matrix.Identity;
            if (mag > 0.05)
            {
                var axis = new Vector3((float)tiltY, (float)tiltX, 0); axis.Normalize();
                world = Matrix.RotationAxis(axis, (float)(-mag * Math.PI / 180.0));
            }
            var view = Matrix.LookAtRH(new Vector3(0, 0, (float)CamDistance), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix.PerspectiveFovRH((float)(FovDeg * Math.PI / 180.0), (float)_w / _h, 0.1f, 20f);
            _lastWVP = world * view * proj;

            double hc = hiCenterDeg * Math.PI / 180.0; if (hc < 0) hc += 2 * Math.PI;
            var cb = new CB
            {
                WVP = _lastWVP, World = world,
                CamPos = new Vector4(0, 0, (float)CamDistance, 1),
                ScreenMap = new Vector4((float)_u0, (float)_v0, (float)_du, (float)_dv),
                Params = new Vector4(0.06f, 0.10f, 4.0f, (float)pulse),
                Hi = new Vector4((float)hc, (float)(hiHalfDeg * Math.PI / 180.0), hiOn ? 1f : 0f, 0f),
            };
            _ctx.UpdateSubresource(ref cb, _cbuf);

            _ctx.Rasterizer.State = _raster;
            _ctx.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = _w, Height = _h, MinDepth = 0, MaxDepth = 1 });
            _ctx.OutputMerger.SetDepthStencilState(_depthState);
            _ctx.OutputMerger.SetBlendState(_blend);
            _ctx.OutputMerger.SetRenderTargets(_dsv, _rtv);
            _ctx.ClearRenderTargetView(_rtv, clear);
            _ctx.ClearDepthStencilView(_dsv, DepthStencilClearFlags.Depth, 1f, 0);

            if (_vbuf != null && _envSrv != null)
            {
                _ctx.InputAssembler.InputLayout = _layout;
                _ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                _ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_vbuf, Utilities.SizeOf<GVertex>(), 0));
                _ctx.InputAssembler.SetIndexBuffer(_ibuf, Format.R32_UInt, 0);
                _ctx.VertexShader.Set(_vs);
                _ctx.VertexShader.SetConstantBuffer(0, _cbuf);
                _ctx.PixelShader.Set(_ps);
                _ctx.PixelShader.SetConstantBuffer(0, _cbuf);
                _ctx.PixelShader.SetShaderResource(0, _envSrv);
                _ctx.PixelShader.SetSampler(0, _sampler);
                _ctx.DrawIndexed(_indexCount, 0, 0);
            }

            // Resolve glass into the single-sample _resolve, then composite the overlay sprites into it.
            if (_samples > 1) _ctx.ResolveSubresource(_rt, 0, _resolve, 0, Format.B8G8R8A8_UNorm);
            else _ctx.CopyResource(_rt, _resolve);
            DrawSprites();
        }

        /// <summary>Render one frame into the reusable WriteableBitmap (WPF/offscreen path).</summary>
        public WriteableBitmap Render(double tiltX, double tiltY, double pulse, double hiCenterDeg, double hiHalfDeg, bool hiOn, RawColor4 clear)
        {
            RenderScene(tiltX, tiltY, pulse, hiCenterDeg, hiHalfDeg, hiOn, clear);
            _ctx.Flush();
            return ReadBack();
        }

        /// <summary>Render the scene and copy it into an external swapchain backbuffer — for DirectComposition (full GPU, no readback).</summary>
        public void RenderToTarget(double tiltX, double tiltY, double pulse, double hiCenterDeg, double hiHalfDeg, bool hiOn, RawColor4 clear, Texture2D backBuffer)
        {
            RenderScene(tiltX, tiltY, pulse, hiCenterDeg, hiHalfDeg, hiOn, clear);
            _ctx.CopyResource(_resolve, backBuffer);
            _ctx.Flush();
        }

        private WriteableBitmap ReadBack()
        {
            // Pbgra32: the RT blends over a transparent clear, so its output is premultiplied.
            if (_wb == null) _wb = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Pbgra32, null);

            // Copy this frame (_resolve already holds glass+sprites) into stage[cur]; MAP the PREVIOUS
            // frame (already finished on the GPU) so the blocking Map never stalls on the in-flight frame.
            int cur = _stageIdx;
            _ctx.CopyResource(_resolve, _stage[cur]);

            int mapIdx = _stageHasPrev ? (1 - cur) : cur; // first frame: map the one we just copied
            DataBox box = _ctx.MapSubresource(_stage[mapIdx], 0, MapMode.Read, MapFlags.None);
            try
            {
                _wb.Lock();
                IntPtr dst = _wb.BackBuffer; int dstStride = _wb.BackBufferStride;
                for (int y = 0; y < _h; y++)
                    Utilities.CopyMemory(IntPtr.Add(dst, y * dstStride), IntPtr.Add(box.DataPointer, y * box.RowPitch), _w * 4);
                _wb.AddDirtyRect(new Int32Rect(0, 0, _w, _h));
            }
            finally { _wb.Unlock(); _ctx.UnmapSubresource(_stage[mapIdx], 0); }

            _stageIdx = 1 - cur;
            _stageHasPrev = true;
            return _wb;
        }

        /// <summary>Project a model-space point to render-target pixel coords (for placing 2D icons).</summary>
        public bool Project(Vector3 p, out double sx, out double sy)
        {
            Vector4 clip = Vector4.Transform(new Vector4(p, 1f), _lastWVP);
            if (clip.W <= 0.0001f) { sx = sy = 0; return false; }
            sx = (clip.X / clip.W * 0.5 + 0.5) * _w;
            sy = (1.0 - (clip.Y / clip.W * 0.5 + 0.5)) * _h;
            return true;
        }

        public void Dispose()
        {
            foreach (var s in _iconSprites) { s.Srv?.Dispose(); s.Tex?.Dispose(); }
            _iconSprites.Clear();
            if (_hubSprite != null) { _hubSprite.Srv?.Dispose(); _hubSprite.Tex?.Dispose(); _hubSprite = null; }
            _envSrv?.Dispose(); _envTex?.Dispose(); _vbuf?.Dispose(); _ibuf?.Dispose();
            _depthState?.Dispose(); _raster?.Dispose(); _blend?.Dispose(); _spriteBlend?.Dispose(); _sampler?.Dispose();
            _cbuf?.Dispose(); _spriteCb?.Dispose(); _layout?.Dispose(); _ps?.Dispose(); _vs?.Dispose();
            _spritePs?.Dispose(); _spriteVs?.Dispose();
            _stage[0]?.Dispose(); _stage[1]?.Dispose(); _resolveRtv?.Dispose(); _resolve?.Dispose();
            _dsv?.Dispose(); _depth?.Dispose(); _rtv?.Dispose(); _rt?.Dispose();
            _ctx?.Dispose(); _device?.Dispose();
        }

        // ---- offscreen test (UDOBL_D3DGLASS) ----
        public static int RenderTest(string path)
        {
            try
            {
                int W = 600, H = 600;
                using (var g = new D3DGlass(W, H))
                {
                    g.SetEnvironment(MakeEnv(W, H), W, H);
                    g.SetMapping(0, 0, 1.0 / W, 1.0 / H);
                    int n = 9;
                    var cells = new List<CellDef>();
                    var palette = new[]
                    {
                        new Vector3(0.95f,0.78f,0.25f), new Vector3(0.30f,0.65f,0.95f), new Vector3(0.55f,0.6f,0.7f),
                        new Vector3(0.30f,0.85f,0.55f), new Vector3(0.85f,0.45f,0.40f), new Vector3(0.5f,0.55f,0.95f),
                        new Vector3(0.9f,0.6f,0.3f), new Vector3(0.4f,0.8f,0.85f), new Vector3(0.7f,0.5f,0.85f),
                    };
                    double step = 360.0 / n, gap = 3.0;
                    for (int i = 0; i < n; i++)
                        cells.Add(new CellDef(i * step, step / 2 - gap / 2, palette[i % palette.Length]));
                    g.BuildRing(cells, 96, 270, 0.19);
                    double ang = 2 * step * Math.PI / 180.0;
                    var bmp = g.Render(Math.Sin(ang) * 26, -Math.Cos(ang) * 26, 1.0, 2 * step, step / 2, true, new RawColor4(0.06f, 0.07f, 0.10f, 1f));
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    File.WriteAllText(path + ".log", "glass OK: " + g.Driver);
                }
                return 0;
            }
            catch (Exception ex) { try { File.WriteAllText(path + ".log", "glass FAILED: " + ex); } catch { } return 1; }
        }

        private static byte[] MakeEnv(int w, int h)
        {
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    double fx = (double)x / w, fy = (double)y / h;
                    byte r = (byte)(40 + 200 * fx), gg = (byte)(40 + 200 * fy), b = (byte)(160 - 120 * fx);
                    if ((x % 60 < 4) || (y % 60 < 4)) { r = 245; gg = 245; b = 250; }
                    px[o] = b; px[o + 1] = gg; px[o + 2] = r; px[o + 3] = 255;
                }
            return px;
        }
    }
}
