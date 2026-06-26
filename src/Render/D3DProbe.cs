using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using D3D11 = SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;
using Buffer = SharpDX.Direct3D11.Buffer;

namespace Udobl.Render
{
    /// <summary>
    /// Feasibility probe: spin up a D3D11 device (hardware, else WARP software), draw one triangle to
    /// an offscreen BGRA target, read it back and save a PNG. Proves the Direct3D path works here.
    /// </summary>
    public static class D3DProbe
    {
        private const string Hlsl = @"
struct VSIn  { float4 pos : POSITION; float4 col : COLOR; };
struct VSOut { float4 pos : SV_POSITION; float4 col : COLOR; };
VSOut VS(VSIn i){ VSOut o; o.pos = i.pos; o.col = i.col; return o; }
float4 PS(VSOut i) : SV_TARGET { return i.col; }
";

        public static int RunTest(string path)
        {
            int w = 600, h = 600;
            string driver = "?";
            Device device = null;
            try
            {
                try { device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport); driver = "Hardware"; }
                catch { device = new Device(DriverType.Warp, DeviceCreationFlags.BgraSupport); driver = "WARP (software)"; }

                var ctx = device.ImmediateContext;

                var rtDesc = new Texture2DDescription
                {
                    Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None,
                };
                using (var rt = new Texture2D(device, rtDesc))
                using (var rtv = new RenderTargetView(device, rt))
                {
                    var vsCode = ShaderBytecode.Compile(Hlsl, "VS", "vs_4_0");
                    var psCode = ShaderBytecode.Compile(Hlsl, "PS", "ps_4_0");
                    using (var vs = new VertexShader(device, vsCode))
                    using (var ps = new PixelShader(device, psCode))
                    using (var layout = new InputLayout(device, ShaderSignature.GetInputSignature(vsCode), new[]
                    {
                        new InputElement("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
                        new InputElement("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
                    }))
                    {
                        var verts = new[]
                        {
                            new Vector4(0.0f, 0.6f, 0.5f, 1f),  new Vector4(0.30f, 0.85f, 0.70f, 1f),
                            new Vector4(0.6f, -0.6f, 0.5f, 1f), new Vector4(0.30f, 0.55f, 0.95f, 1f),
                            new Vector4(-0.6f, -0.6f, 0.5f, 1f), new Vector4(0.95f, 0.75f, 0.30f, 1f),
                        };
                        using (var vbuf = Buffer.Create(device, BindFlags.VertexBuffer, verts))
                        {
                            ctx.InputAssembler.InputLayout = layout;
                            ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                            ctx.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vbuf, Utilities.SizeOf<Vector4>() * 2, 0));
                            ctx.VertexShader.Set(vs);
                            ctx.PixelShader.Set(ps);
                            ctx.Rasterizer.SetViewport(new RawViewportF { X = 0, Y = 0, Width = w, Height = h, MinDepth = 0, MaxDepth = 1 });
                            ctx.OutputMerger.SetRenderTargets(rtv);
                            ctx.ClearRenderTargetView(rtv, new RawColor4(0.06f, 0.07f, 0.10f, 1f));
                            ctx.Draw(3, 0);
                            ctx.Flush();
                        }
                    }

                    SaveTexture(device, ctx, rt, w, h, path);
                }
                try { File.WriteAllText(path + ".log", "D3D11 OK: " + driver); } catch { }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".log", "D3D11 FAILED (" + driver + "): " + ex); } catch { }
                return 1;
            }
            finally { device?.Dispose(); }
        }

        /// <summary>Copy a render target to CPU and save as PNG (the readback path used for all QA).</summary>
        public static void SaveTexture(Device device, DeviceContext ctx, Texture2D src, int w, int h, string path)
        {
            var stagingDesc = new Texture2DDescription
            {
                Width = w, Height = h, ArraySize = 1, MipLevels = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None,
            };
            using (var staging = new Texture2D(device, stagingDesc))
            {
                ctx.CopyResource(src, staging);
                DataBox box = ctx.MapSubresource(staging, 0, MapMode.Read, D3D11.MapFlags.None);
                try
                {
                    var pixels = new byte[w * h * 4];
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(IntPtr.Add(box.DataPointer, y * box.RowPitch), pixels, y * w * 4, w * 4);

                    var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                    bmp.Freeze();
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(path)) enc.Save(fs);
                }
                finally { ctx.UnmapSubresource(staging, 0); }
            }
        }
    }
}
