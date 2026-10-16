using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using Tools;

namespace Composition
{
    // The shape of the interface: how rounded buttons and panels are, how far they
    // sit in from their neighbours, their outlines and shadows. All lengths are in
    // pixels on a 1080 pixel high window and scale with the window. Everything is
    // zero by default, which draws square, edge to edge, exactly as before; a
    // theme turns it on.
    public static class ShapeStyle
    {
        public static float ButtonRadius { get; set; }
        public static float ButtonInset { get; set; }

        public static float HoverRadius { get; set; }
        public static float HoverInset { get; set; }

        public static float PanelRadius { get; set; }
        public static float PanelShadow { get; set; }

        public static float InputRadius { get; set; }

        // List rows, colour swatches and the small labels over video.
        public static float RowRadius { get; set; }
        public static float RowInset { get; set; }
        public static float SwatchRadius { get; set; }
        public static float ChipRadius { get; set; }

        public static Color Ring { get; set; }
        public static float RingWidth { get; set; }

        public static Color ShadowColor { get; set; }

        // The modern layout: one header row holding the tabs, content on cards
        // with gutters between them. Off by default (the classic layout).
        public static bool Modern { get; set; }

        // A font for figures (times, positions), ideally with tabular digits.
        public static string NumberFont { get; set; }

        // The colour of a primary action and its text, and of quieter text.
        public static Color Primary { get; set; }
        public static Color PrimaryText { get; set; }
        public static Color Muted { get; set; }
        public static Color Surface { get; set; }
        public static Color Elevated { get; set; }
        public static Color Danger { get; set; }

        public static bool Rounded { get { return ButtonRadius > 0 || PanelRadius > 0 || HoverRadius > 0 || RowRadius > 0; } }

        static ShapeStyle()
        {
            Reset();
        }

        public static void Reset()
        {
            ButtonRadius = 0;
            ButtonInset = 0;
            HoverRadius = 0;
            HoverInset = 0;
            PanelRadius = 0;
            PanelShadow = 0;
            InputRadius = 0;
            RowRadius = 0;
            RowInset = 0;
            SwatchRadius = 0;
            ChipRadius = 0;
            Ring = Color.Transparent;
            RingWidth = 0;
            ShadowColor = new Color(0, 0, 0, 140);
            Modern = false;
            NumberFont = null;
            Primary = Color.Transparent;
            PrimaryText = Color.White;
            Muted = new Color(138, 153, 179);
            Surface = new Color(10, 16, 29, 235);
            Elevated = new Color(21, 32, 54);
            Danger = new Color(244, 63, 94);
        }

        public static float Scale(Drawer id)
        {
            int height = id.GraphicsDevice.PresentationParameters.BackBufferHeight;
            if (height <= 0)
                return 1;
            return height / 1080.0f;
        }
    }

    public static class Shapes
    {
        // Fills a rounded rectangle with a colour. Radius is in screen pixels.
        public static void Fill(Drawer id, RectangleF rect, float radius, Color color, float alpha)
        {
            Draw(id, rect, radius, 0, 0, color, alpha);
        }

        // An outline `width` screen pixels wide, inside the rectangle.
        public static void Ring(Drawer id, RectangleF rect, float radius, float width, Color color, float alpha)
        {
            if (width <= 0)
                return;
            Draw(id, rect, radius, Math.Max(1, (int)Math.Round(width)), 0, color, alpha);
        }

        // A soft shadow `blur` screen pixels wide around the rectangle.
        public static void Shadow(Drawer id, RectangleF rect, float radius, float blur, Color color, float alpha)
        {
            if (blur <= 0)
                return;
            Draw(id, rect, radius, 0, Math.Max(1, (int)Math.Round(blur)), color, alpha);
        }

        private static void Draw(Drawer id, RectangleF rectF, float radiusF, int ring, int blur, Color color, float alpha)
        {
            Rectangle rect = new Rectangle((int)Math.Round(rectF.X), (int)Math.Round(rectF.Y), (int)Math.Round(rectF.Width), (int)Math.Round(rectF.Height));
            if (rect.Width <= 0 || rect.Height <= 0)
                return;

            int radius = (int)Math.Round(radiusF);
            radius = Math.Max(0, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));

            alpha *= color.A / 255.0f;
            if (alpha <= 0)
                return;

            Color tint = new Color(color.R, color.G, color.B);

            if (radius == 0 && ring == 0 && blur == 0)
            {
                id.Draw(id.TextureCache.GetTextureFromColor(Color.White), rect, tint, alpha);
                return;
            }

            Texture2D texture = id.TextureCache.GetShapeTexture(radius, ring, blur);
            int c = radius + blur;

            // The drawn area: the rectangle plus the soft edge all round.
            int x0 = rect.X - blur;
            int y0 = rect.Y - blur;
            int x3 = rect.Right + blur;
            int y3 = rect.Bottom + blur;
            int x1 = x0 + c;
            int y1 = y0 + c;
            int x2 = x3 - c;
            int y2 = y3 - c;

            int[] dx = { x0, x1, x2, x3 };
            int[] dy = { y0, y1, y2, y3 };
            int[] sx = { 0, c, c + 2, c * 2 + 2 };

            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 3; col++)
                {
                    int w = dx[col + 1] - dx[col];
                    int h = dy[row + 1] - dy[row];
                    if (w <= 0 || h <= 0)
                        continue;

                    Rectangle src = new Rectangle(sx[col], sx[row], sx[col + 1] - sx[col], sx[row + 1] - sx[row]);
                    id.Draw(texture, src, new Rectangle(dx[col], dy[row], w, h), tint, alpha);
                }
            }
        }
    }
}
