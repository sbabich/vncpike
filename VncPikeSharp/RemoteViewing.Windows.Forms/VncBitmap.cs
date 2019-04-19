using System;
using System.Drawing;
using System.Drawing.Imaging;
using RemoteViewing.Vnc;

namespace RemoteViewing.Windows.Forms
{
    /// <summary>
    /// Helps with Windows Forms bitmap conversion.
    /// </summary>
    public static class VncBitmap
    {
        /// <summary>
        /// Copies a region of a bitmap into the framebuffer.
        /// </summary>
        /// <param name="source">The bitmap to read.</param>
        /// <param name="sourceRectangle">The bitmap region to copy.</param>
        /// <param name="target">The framebuffer to copy into.</param>
        /// <param name="targetX">The leftmost X coordinate of the framebuffer to draw to.</param>
        /// <param name="targetY">The topmost Y coordinate of the framebuffer to draw to.</param>
        public static unsafe void CopyToFramebuffer(
            Bitmap source,
            VncRectangle sourceRectangle,
            VncFramebuffer target,
            int targetX,
            int targetY)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            if (sourceRectangle.IsEmpty)
            {
                return;
            }

            var winformsRect = new Rectangle(sourceRectangle.X, sourceRectangle.Y, sourceRectangle.Width, sourceRectangle.Height);
            var data = source.LockBits(winformsRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                fixed (byte* framebufferData = target.GetBuffer())
                {
                    VncPixelFormat.Copy(
                        data.Scan0,
                        data.Stride,
                        new VncPixelFormat(),
                        sourceRectangle,
                        (IntPtr)framebufferData,
                        target.Stride,
                        target.PixelFormat,
                        targetX,
                        targetY);
                }
            }
            finally
            {
                source.UnlockBits(data);
            }
        }

        /// <summary>
        /// Copies a region of the framebuffer into a bitmap.
        /// </summary>
        /// <param name="source">The framebuffer to read.</param>
        /// <param name="sourceRectangle">The framebuffer region to copy.</param>
        /// <param name="target">The bitmap to copy into.</param>
        /// <param name="targetX">The leftmost X coordinate of the bitmap to draw to.</param>
        /// <param name="targetY">The topmost Y coordinate of the bitmap to draw to.</param>
        public static unsafe void CopyFromFramebuffer(
            VncFramebuffer source,
            VncRectangle sourceRectangle,
            Bitmap target,
            int targetX,
            int targetY)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var winformsRect = new Rectangle(targetX, targetY, sourceRectangle.Width, sourceRectangle.Height);
            var data = target.LockBits(winformsRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try
            {
                VncPixelFormat.CopyFromFramebuffer(source, sourceRectangle, data.Scan0, data.Stride, targetX, targetY);
            }
            finally
            {
                target.UnlockBits(data);
            }
        }
    }
}
