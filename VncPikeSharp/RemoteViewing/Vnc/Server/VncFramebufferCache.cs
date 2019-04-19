﻿#region License
/*
RemoteViewing VNC Client/Server Library for .NET
Copyright (c) 2013 James F. Bellinger <http://www.zer7.com/software/remoteviewing>
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
#endregion

using RemoteViewing.Logging;
using System;

namespace RemoteViewing.Vnc.Server
{
    /// <summary>
    /// Caches the <see cref="VncFramebuffer"/> pixel data and updates them as new
    /// <see cref="VncFramebuffer"/> commands are received.
    /// </summary>
    internal sealed class VncFramebufferCache : IVncFramebufferCache
    {
        // The size of the tiles which will be invalidated. So we're basically
        // dividing the framebuffer in blocks of 32x32 and are invalidating them one at a time.
        private const int TileSize = 64;

        private readonly ILog logger;

        private readonly bool[,] isLineInvalid;

        // We cache the latest framebuffer data as it was sent to the client. When looking for changes,
        // we compare with the framebuffer which is cached here and send the deltas (for each time
        // which was invalidate) to the client.
        private VncFramebuffer cachedFramebuffer;

        private int WidthDiv { get; set; } = 20;
        private int lastArea = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="VncFramebufferCache"/> class.
        /// </summary>
        /// <param name="framebuffer">
        /// The <see cref="VncFramebuffer"/> to cache.
        /// </param>
        /// <param name="logger">
        /// The <see cref="ILog"/> logger to use when logging diagnostic messages.
        /// </param>
        public VncFramebufferCache(VncFramebuffer framebuffer, ILog logger)
        {
            if (framebuffer == null)
            {
                throw new ArgumentNullException(nameof(framebuffer));
            }

            this.Framebuffer = framebuffer;
            this.cachedFramebuffer = new VncFramebuffer(framebuffer.Name, framebuffer.Width, framebuffer.Height, framebuffer.PixelFormat);

            this.logger = logger;
            this.isLineInvalid = new bool[this.WidthDiv, this.Framebuffer.Height];
        }

        /// <summary>
        /// Gets an up-to-date and complete <see cref="VncFramebuffer"/>.
        /// </summary>
        public VncFramebuffer Framebuffer
        {
            get;
            private set;
        }

        /// <summary>
        /// Responds to a <see cref="VncServerSession"/> update request.
        /// </summary>
        /// <param name="session">
        /// The session on which the update request was received.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the operation completed successfully; otherwise,
        /// <see langword="false"/>.
        /// </returns>
        public unsafe bool RespondToUpdateRequest(IVncServerSession session)
        {
            //VncRectangle subregion = default(VncRectangle);

            System.Collections.Generic.List<long> times = new System.Collections.Generic.List<long>();
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            sw.Start();

            var fb = this.Framebuffer;
            var fbr = session.FramebufferUpdateRequest;
            if (fb == null || fbr == null)
            {
                return false;
            }

            times.Add(sw.ElapsedMilliseconds);

            var incremental = fbr.Incremental;
            var region = fbr.Region;
            int bpp = fb.PixelFormat.BytesPerPixel;

            this.logger?.Log(LogLevel.Debug, () => $"Responding to an update request for region {region}.");

            session.FramebufferManualBeginUpdate();


            times.Add(sw.ElapsedMilliseconds);

            int k = 1;
            if (lastArea > 600 * 600)
            {
                k = 8;
            }

            int XDiv = 32 * k;
            int YDiv = 32 * k;

            lastArea = 0;

            System.Collections.Generic.List<VncRectangle> to_change = new System.Collections.Generic.List<VncRectangle>();
            lock (fb.SyncRoot)
            {
                lock (this.cachedFramebuffer.SyncRoot)
                {
                    var actualBuffer = this.Framebuffer.GetBuffer();
                    var bufferedBuffer = this.cachedFramebuffer.GetBuffer();

                    for (var xi = region.X; xi < region.X + region.Width; xi += XDiv)
                    {
                        for (var yi = region.Y; yi < region.Y + region.Height; yi += YDiv)
                        {
                            VncRectangle subregion1 = default(VncRectangle);
                            subregion1.X = xi;
                            subregion1.Y = yi;
                            subregion1.Width = XDiv;
                            subregion1.Height = YDiv;

                            if (subregion1.X + subregion1.Width > this.Framebuffer.Width)
                            {
                                subregion1.Width = this.Framebuffer.Width - subregion1.X;
                            }

                            if (subregion1.Y + subregion1.Height > this.Framebuffer.Height)
                            {
                                subregion1.Height = this.Framebuffer.Height - subregion1.Y;
                            }

                            int length = bpp * subregion1.Width;

                            int bytes_width = subregion1.Width * bpp;
                            bool need_update = false;

                            for (var iy = subregion1.Y; iy < subregion1.Y + subregion1.Height; iy++)
                            {
                                int srcOffset = (iy * this.Framebuffer.Stride) + (bpp * xi);

                                fixed (byte* actualLinePtr = actualBuffer, bufferedLinePtr = bufferedBuffer)
                                {
                                    bool need = NativeMethods.memcmp(actualLinePtr + srcOffset, bufferedLinePtr + srcOffset, (uint)length) != 0;
                                    if (need)
                                    {
                                        need_update = true;
                                    }
                                }

                                if (need_update)
                                {
                                    Buffer.BlockCopy(actualBuffer, srcOffset, bufferedBuffer, srcOffset, length);
                                }
                            }

                            if (need_update)
                            {
                                to_change.Add(subregion1);
                                //session.FramebufferManualInvalidate(subregion1);
                            }
                        }
                    }
                }
            }

            times.Add(sw.ElapsedMilliseconds);

            if (incremental)
            {
                for (int i = 0; i < to_change.Count; i++)
                {
                    var sr = to_change[i];
                    session.FramebufferManualInvalidate(sr);
                    lastArea += sr.Width * sr.Height;
                }
                WidthDiv = to_change.Count;
            }
            else
            {
                WidthDiv = 1;
                session.FramebufferManualInvalidate(region);
            }

            times.Add(sw.ElapsedMilliseconds);

            string s = "";
            foreach (long t in times)
            {
                s += $" => T={t} ";
            }
            Console.WriteLine(s);

            // Take a lock here, as we will modify
            // both buffers heavily in the next block.
            VncRectangle subregion = default(VncRectangle);
            
            // Take a lock here, as we will modify
            // both buffers heavily in the next block.
            /*lock (fb.SyncRoot)
            {
                lock (this.cachedFramebuffer.SyncRoot)
                {
                    var actualBuffer = this.Framebuffer.GetBuffer();
                    var bufferedBuffer = this.cachedFramebuffer.GetBuffer();

                    // In this block, we will determine which rectangles need updating. Right now, we consider
                    // each line at once. It's not a very efficient algorithm, but it works.
                    // We're going to start at the upper-left position of the region, and then we will work our way down,
                    // on a line by line basis, to determine if each line is still valid.
                    // isLineInvalid will indicate, on a line-by-line basis, whether a line is still valid or not.

                    int width_i = region.Width / this.WidthDiv;
                    for (int y = region.Y; y < region.Y + region.Height; y++)
                    {
                        for (int xi = 0; xi < this.WidthDiv; xi++)
                        {
                            int x_i = xi * width_i;
                            subregion.X = region.X;
                            subregion.Y = y;
                            subregion.Width = region.Width;
                            subregion.Height = 1;

                            bool isValid = true;

                            // For a given y, the x pixels are stored sequentially in the array
                            // starting at y * stride (number of bytes per row); for each x
                            // value there are bpp bytes of data (4 for a 32-bit integer); we are looking
                            // for pixels between x and x + w so this translates to
                            // y * stride + bpp * x and y * stride + bpp * (x + w)
                            int srcOffset = (y * this.Framebuffer.Stride) + (bpp * x_i);
                            int length = bpp * width_i;// region.Width;

                            fixed (byte* actualLinePtr = actualBuffer, bufferedLinePtr = bufferedBuffer)
                            {
                                isValid = NativeMethods.memcmp(actualLinePtr + srcOffset, bufferedLinePtr + srcOffset, (uint)length) == 0;
                            }

                            if (!isValid)
                            {
                                try
                                {
                                    Buffer.BlockCopy(actualBuffer, srcOffset, bufferedBuffer, srcOffset, length);
                                }
                                catch
                                {
                                    throw;
                                }
                            }

                            this.isLineInvalid[xi, y - region.Y] = !isValid;
                        }
                    }
                } // lock
            } // lock

            if (incremental)
            {
                // Determine logical group of lines which are invalid. We find the first line which is invalid,
                // create a new region which contains the all invalid lines which immediately follow the current line.
                // If we find a valid line, we'll create a new region.
                int? y = null;

                for (int xi = 0; xi < this.WidthDiv; xi++)
                {
                    int width_i = region.Width / this.WidthDiv;
                    int x_i = xi * width_i;
                    for (int line = 0; line < region.Height; line++)
                    {
                        if (y == null && this.isLineInvalid[xi, line])
                        {
                            y = region.Y + line;
                        }

                        if (y != null && (!this.isLineInvalid[xi, line] || line == region.Height - 1))
                        {
                            // Flush
                            subregion.X = x_i;// region.X;
                            subregion.Y = region.Y + y.Value;
                            subregion.Width = width_i;// region.Width;
                            subregion.Height = line - y.Value + 1;
                            session.FramebufferManualInvalidate(subregion);
                            y = null;
                        }
                    }
                }
            }
            else
            {
                session.FramebufferManualInvalidate(region);
            }*/

            return session.FramebufferManualEndUpdate();
        }
    }
}
