using Microsoft.Xna.Framework;
using System;
using Tools;
using Composition;

namespace Composition.Nodes
{
    public class ColorNode : ImageNode
    {
        public Color Color
        {
            get
            {
                return color;
            }
            set
            {
                SetFilename(null);
                color = value;
                dirty = true;
            }
        }

        private Color color;
        private bool dirty;

        // Optional shape (see ShapeStyle). Lengths are pixels on a 1080 pixel high window.
        public float CornerRadius { get; set; }
        public float Inset { get; set; }
        public float InsetX { get; set; }
        public Color RingColor { get; set; }
        public float RingWidth { get; set; }
        public float ShadowBlur { get; set; }

        public bool Shaped
        {
            get
            {
                return CornerRadius > 0 || Inset > 0 || InsetX > 0 || (RingWidth > 0 && RingColor.A > 0) || ShadowBlur > 0;
            }
        }

        // Take the shape of buttons, panels or hovers from the current ShapeStyle.
        public void StyleAsButton()
        {
            CornerRadius = ShapeStyle.ButtonRadius;
            Inset = ShapeStyle.ButtonInset;
        }

        public void StyleAsHover()
        {
            CornerRadius = ShapeStyle.HoverRadius;
            Inset = ShapeStyle.HoverInset;
        }

        public void StyleAsRow()
        {
            CornerRadius = ShapeStyle.RowRadius;
            Inset = ShapeStyle.RowInset;
        }

        public void StyleAsSwatch()
        {
            CornerRadius = ShapeStyle.SwatchRadius;
        }

        public void StyleAsChip()
        {
            CornerRadius = ShapeStyle.ChipRadius;
        }

        public void StyleAsInput()
        {
            CornerRadius = ShapeStyle.InputRadius;
            if (ShapeStyle.InputRadius > 0)
            {
                RingColor = ShapeStyle.Ring;
                RingWidth = ShapeStyle.RingWidth;
            }
        }

        public void StyleAsPanel()
        {
            CornerRadius = ShapeStyle.PanelRadius;
            RingColor = ShapeStyle.Ring;
            RingWidth = ShapeStyle.RingWidth;
            ShadowBlur = ShapeStyle.PanelShadow;
        }

        public RectangleF ShapeBounds(Drawer id)
        {
            float scale = ShapeStyle.Scale(id);
            float ix = (Inset + InsetX) * scale;
            float iy = Inset * scale;
            RectangleF b = BoundsF;
            return new RectangleF(b.X + ix, b.Y + iy, Math.Max(0, b.Width - ix * 2), Math.Max(0, b.Height - iy * 2));
        }

        protected override void DrawImage(Drawer id, float alpha)
        {
            if (!Shaped)
            {
                base.DrawImage(id, alpha);
                return;
            }

            float scale = ShapeStyle.Scale(id);
            RectangleF shape = ShapeBounds(id);
            float radius = CornerRadius * scale;

            if (ShadowBlur > 0)
            {
                float blur = ShadowBlur * scale;
                RectangleF shadow = new RectangleF(shape.X, shape.Y + blur * 0.35f, shape.Width, shape.Height);
                Shapes.Shadow(id, shadow, radius, blur, ShapeStyle.ShadowColor, alpha);
            }

            // A rounded node without a sliced image is drawn flat in its colour (for a
            // theme region, the colour at its centre): stretching a square image would
            // square the corners off again.
            bool fromFile = !string.IsNullOrEmpty(FileName) && (Slice > 0 || CornerRadius <= 0);
            if (fromFile)
            {
                if (Slice > 0)
                {
                    DrawSliced(id, texture, SourceBounds, shape, Slice, Slice * SliceScale * scale, Tint, alpha);
                }
                else
                {
                    id.Draw(texture, SourceBounds, shape.ToRectangle(), Tint, alpha);
                }
            }
            else
            {
                Color fill = color;
                if (Tint != Color.White)
                {
                    fill = new Color(color.R * Tint.R / 255, color.G * Tint.G / 255, color.B * Tint.B / 255, color.A);
                }
                Shapes.Fill(id, shape, radius, fill, alpha);
            }

            if (RingWidth > 0 && RingColor.A > 0)
            {
                Shapes.Ring(id, shape, radius, Math.Max(1, RingWidth * scale), RingColor, alpha);
            }
        }

        public ColorNode(Color color)
        {
            this.color = color;
            KeepAspectRatio = false;
            dirty = true;
        }

        // Loads a file first, then the color
        public ColorNode(string filename, Color color)
            :base(filename)
        {
            this.color = color;
            KeepAspectRatio = false;
            dirty = true;
        }

        public ColorNode(string filename, Rectangle sourceBounds, Color color)
            : base(filename, sourceBounds)
        {
            this.color = color;
            KeepAspectRatio = false;
            dirty = true;
        }

        public ColorNode(Tools.ToolTexture tt)
           : this(tt.TextureFilename, tt.Region, tt.XNA)
        {
        }

        public override void SetToolTexture(ToolTexture tt)
        {
            color = tt.XNA;
            base.SetToolTexture(tt);
            dirty = true;
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            if (dirty)
            {
                Texture = null;

                if (!string.IsNullOrEmpty(FileName) && System.IO.File.Exists(FileName))
                {
                    Texture = id.TextureCache.GetTextureFromFilename(FileName, Color, false);
                    if (SourceBounds.Width == 0 || SourceBounds.Height == 0)
                    {
                        SourceBounds = new Rectangle(0, 0, Texture.Width, Texture.Height);
                    }

                    sharedTexture = true;
                    UpdateAspectRatioFromTexture();
                }

                if (texture == null)
                {
                    texture = id.TextureCache.GetTextureFromColor(Color);
                }

                sharedTexture = true;
                dirty = false;
            }
            base.Draw(id, parentAlpha);
        }
    }

}
