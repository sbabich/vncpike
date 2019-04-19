#if !NETSTANDARD2_0 && !NETCOREAPP2_1
using System;
using System.Drawing;
using System.Windows.Forms;
using RemoteViewing.Vnc;

namespace RemoteViewing.Windows.Forms.Server
{
    /// <summary>
    /// Called to determine the screen region to send.
    /// </summary>
    /// <returns>The screen region.</returns>
    public delegate Rectangle VncScreenFramebufferSourceGetBoundsCallback();

    /// <summary>
    /// Provides a framebuffer with pixels copied from the screen.
    /// </summary>
    public class VncScreenFramebufferSource : IVncFramebufferSource
    {
        private Bitmap bitmap;
        private VncFramebuffer framebuffer;
        private string name;
        private VncScreenFramebufferSourceGetBoundsCallback getScreenBounds;

        public float Scale { get; set; } = 1f;

        /// <summary>
        /// Initializes a new instance of the <see cref="VncScreenFramebufferSource"/> class.
        /// </summary>
        /// <param name="name">The framebuffer name. Many VNC clients set their titlebar to this name.</param>
        /// <param name="screen">The bounds of the screen region.</param>
        public VncScreenFramebufferSource(string name, Screen screen, float scale = 1f)
        {
            if (screen == null)
            {
                throw new ArgumentNullException(nameof(screen));
            }

            this.Scale = scale;

            this.name = name ?? throw new ArgumentNullException(nameof(name));
            this.getScreenBounds = () => screen.Bounds;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VncScreenFramebufferSource"/> class.
        /// Screen region bounds are determined by a callback.
        /// </summary>
        /// <param name="name">The framebuffer name. Many VNC clients set their titlebar to this name.</param>
        /// <param name="getBoundsCallback">A callback supplying the bounds of the screen region to copy.</param>
        public VncScreenFramebufferSource(string name, VncScreenFramebufferSourceGetBoundsCallback getBoundsCallback, float scale = 1f)
        {
            this.name = name ?? throw new ArgumentNullException(nameof(name));

            this.Scale = scale;
            this.getScreenBounds = getBoundsCallback ?? throw new ArgumentNullException(nameof(getBoundsCallback));
        }

        /// <summary>
        /// Captures the screen.
        /// </summary>
        /// <returns>A framebuffer corresponding to the screen.</returns>
        public VncFramebuffer Capture()
        {
            var bounds = this.getScreenBounds();
            int w = Convert.ToInt32((float)bounds.Width * this.Scale),
                h = Convert.ToInt32((float)bounds.Height * this.Scale);

            if (this.bitmap == null || this.bitmap.Width != w || this.bitmap.Height != h)
            {
                this.bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                this.framebuffer = new VncFramebuffer(this.name, w, h, new VncPixelFormat(32, 24, 8, 16, 8, 8, 8, 0));
            }

            /*Bitmap b = new Bitmap(control.Width, control.Height);
            Graphics g = Graphics.FromImage(b);
            g.CopyFromScreen(control.Parent.RectangleToScreen(control.Bounds).X, control.Parent.RectangleToScreen(control.Bounds).Y, 0, 0, new Size(control.Bounds.Width, control.Bounds.Height), CopyPixelOperation.SourceCopy);

            g.DrawImage(b, 0,0,newWidth, newHeight);*/

            using (var b = new Bitmap(bounds.Width, bounds.Height))
            using (var g2 = Graphics.FromImage(b))
            using (var g = Graphics.FromImage(this.bitmap))
            {
                g2.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
                g.DrawImage(b, 0, 0, w, h);

                lock (this.framebuffer.SyncRoot)
                {
                    VncBitmap.CopyToFramebuffer(
                        this.bitmap,
                        new VncRectangle(0, 0, w, h),
                        this.framebuffer,
                        0,
                        0);
                }
            }

            return this.framebuffer;
        }
    }
}
#endif
