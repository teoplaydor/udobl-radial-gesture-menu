using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Udobl.UI
{
    /// <summary>
    /// GPU pixel shader (ps_2_0) that refracts the captured desktop through the glass:
    /// the bevel slopes (from a height map) bend the background, and a global shift follows
    /// the wheel's lean. The menu content (Input) is composited on top.
    /// </summary>
    public sealed class RefractionEffect : ShaderEffect
    {
        private static readonly PixelShader Shader = LoadShader();

        private static PixelShader LoadShader()
        {
            var ps = new PixelShader();
            try
            {
                Assembly asm = typeof(RefractionEffect).Assembly;
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("refract.ps", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                if (name != null)
                    using (Stream s = asm.GetManifestResourceStream(name))
                        ps.SetStreamSource(s);
            }
            catch { }
            return ps;
        }

        public RefractionEffect()
        {
            PixelShader = Shader;
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(BgProperty);
            UpdateShaderValue(HeightProperty);
            UpdateShaderValue(TexelProperty);
            UpdateShaderValue(StrengthProperty);
            UpdateShaderValue(TintProperty);
            UpdateShaderValue(GlobalShiftProperty);
        }

        public static readonly DependencyProperty InputProperty =
            RegisterPixelShaderSamplerProperty("Input", typeof(RefractionEffect), 0);
        public Brush Input { get { return (Brush)GetValue(InputProperty); } set { SetValue(InputProperty, value); } }

        public static readonly DependencyProperty BgProperty =
            RegisterPixelShaderSamplerProperty("Bg", typeof(RefractionEffect), 1);
        public Brush Bg { get { return (Brush)GetValue(BgProperty); } set { SetValue(BgProperty, value); } }

        public static readonly DependencyProperty HeightProperty =
            RegisterPixelShaderSamplerProperty("Height", typeof(RefractionEffect), 2);
        public Brush Height { get { return (Brush)GetValue(HeightProperty); } set { SetValue(HeightProperty, value); } }

        public static readonly DependencyProperty TexelProperty =
            DependencyProperty.Register("Texel", typeof(Point), typeof(RefractionEffect),
                new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(0)));
        public Point Texel { get { return (Point)GetValue(TexelProperty); } set { SetValue(TexelProperty, value); } }

        public static readonly DependencyProperty StrengthProperty =
            DependencyProperty.Register("Strength", typeof(double), typeof(RefractionEffect),
                new UIPropertyMetadata(0.0, PixelShaderConstantCallback(1)));
        public double Strength { get { return (double)GetValue(StrengthProperty); } set { SetValue(StrengthProperty, value); } }

        public static readonly DependencyProperty TintProperty =
            DependencyProperty.Register("Tint", typeof(double), typeof(RefractionEffect),
                new UIPropertyMetadata(0.85, PixelShaderConstantCallback(2)));
        public double Tint { get { return (double)GetValue(TintProperty); } set { SetValue(TintProperty, value); } }

        public static readonly DependencyProperty GlobalShiftProperty =
            DependencyProperty.Register("GlobalShift", typeof(Point), typeof(RefractionEffect),
                new UIPropertyMetadata(new Point(0, 0), PixelShaderConstantCallback(3)));
        public Point GlobalShift { get { return (Point)GetValue(GlobalShiftProperty); } set { SetValue(GlobalShiftProperty, value); } }

        public static readonly DependencyProperty BgScaleProperty =
            DependencyProperty.Register("BgScale", typeof(double), typeof(RefractionEffect),
                new UIPropertyMetadata(1.0, PixelShaderConstantCallback(4)));
        public double BgScale { get { return (double)GetValue(BgScaleProperty); } set { SetValue(BgScaleProperty, value); } }

        public static readonly DependencyProperty LightDirProperty =
            DependencyProperty.Register("LightDir", typeof(Point), typeof(RefractionEffect),
                new UIPropertyMetadata(new Point(-0.6, -0.8), PixelShaderConstantCallback(5)));
        public Point LightDir { get { return (Point)GetValue(LightDirProperty); } set { SetValue(LightDirProperty, value); } }

        public static readonly DependencyProperty EmbossProperty =
            DependencyProperty.Register("Emboss", typeof(double), typeof(RefractionEffect),
                new UIPropertyMetadata(0.0, PixelShaderConstantCallback(6)));
        public double Emboss { get { return (double)GetValue(EmbossProperty); } set { SetValue(EmbossProperty, value); } }
    }
}
