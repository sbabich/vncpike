#region License
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
using RemoteViewing.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteViewing.Vnc.Server
{
    /// <summary>
    /// Serves a VNC client with framebuffer information and receives keyboard and mouse interactions.
    /// </summary>
    public class VncServerSession : IVncServerSession
    {
        private ILog logger;
        private IVncPasswordChallenge passwordChallenge;
        private VncStream c = new VncStream();
        private VncEncoding[] clientEncoding = new VncEncoding[0];
        private VncPixelFormat clientPixelFormat;
        private int clientWidth;
        private int clientHeight;
        private Version clientVersion;
        private VncServerSessionOptions options;
        private IVncFramebufferCache fbuAutoCache;
        private List<Rectangle> fbuRectangles = new List<Rectangle>();
        private object fbuSync = new object();
        private IVncFramebufferSource fbSource;
        private double maxUpdateRate;
        private Utility.PeriodicThread requester;
        private object specialSync = new object();
        private Thread threadMain;
        private bool securityNegotiated = false;
        private MemoryStream _zlibMemoryStream;
        private DeflateStream _zlibDeflater;

        public string PasswordLooker { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="VncServerSession"/> class.
        /// </summary>
        public VncServerSession()
            : this(new VncPasswordChallenge(), null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VncServerSession"/> class.
        /// </summary>
        /// <param name="passwordChallenge">
        /// The <see cref="IVncPasswordChallenge"/> to use to generate password challenges.
        /// </param>
        /// <param name="logger">
        /// The logger to use when logging diagnostic messages.
        /// </param>
        public VncServerSession(IVncPasswordChallenge passwordChallenge, ILog logger)
        {
            if (passwordChallenge == null)
            {
                throw new ArgumentNullException(nameof(passwordChallenge));
            }

            this.passwordChallenge = passwordChallenge;
            this.logger = logger;
            this.MaxUpdateRate = 15;
        }

        /// <summary>
        /// Occurs when the VNC client provides a password.
        /// Respond to this event by accepting or rejecting the password.
        /// </summary>
        public event EventHandler<PasswordProvidedEventArgs> PasswordProvided;

        /// <summary>
        /// Occurs when the client requests access to the desktop.
        /// It may request exclusive or shared access -- this event will relay that information.
        /// </summary>
        public event EventHandler<CreatingDesktopEventArgs> CreatingDesktop;

        /// <summary>
        /// Occurs when the VNC client has successfully connected to the server.
        /// </summary>
        public event EventHandler Connected;

        /// <summary>
        /// Occurs when the VNC client has failed to connect to the server.
        /// </summary>
        public event EventHandler ConnectionFailed;

        /// <summary>
        /// Occurs when the VNC client is disconnected.
        /// </summary>
        public event EventHandler Closed;

        /// <summary>
        /// Occurs when the framebuffer needs to be captured.
        /// If you have not called <see cref="VncServerSession.SetFramebufferSource"/>, alter the framebuffer
        /// in response to this event.
        ///
        /// <see cref="VncServerSession.FramebufferUpdateRequestLock"/> is held automatically while this event is raised.
        /// </summary>
        public event EventHandler FramebufferCapturing;

        /// <summary>
        /// Occurs when the framebuffer needs to be updated.
        /// If you do not set <see cref="FramebufferUpdatingEventArgs.Handled"/>,
        /// <see cref="VncServerSession"/> will determine the updated regions itself.
        ///
        /// <see cref="VncServerSession.FramebufferUpdateRequestLock"/> is held automatically while this event is raised.
        /// </summary>
        public event EventHandler<FramebufferUpdatingEventArgs> FramebufferUpdating;

        /// <summary>
        /// Occurs when a key has been pressed or released.
        /// </summary>
        public event EventHandler<KeyChangedEventArgs> KeyChanged;

        /// <summary>
        /// Occurs on a mouse movement, button click, etc.
        /// </summary>
        public event EventHandler<PointerChangedEventArgs> PointerChanged;

        /// <summary>
        /// Occurs when the clipboard changes on the remote client.
        /// If you are implementing clipboard integration, use this to set the local clipboard.
        /// </summary>
        public event EventHandler<RemoteClipboardChangedEventArgs> RemoteClipboardChanged;

        /// <summary>
        /// Gets the protocol version of the client.
        /// </summary>
        public Version ClientVersion
        {
            get { return this.clientVersion; }
        }

        /// <summary>
        /// Gets the framebuffer for the VNC session.
        /// </summary>
        public VncFramebuffer Framebuffer
        {
            get;
            private set;
        }

        /// <inheritdoc/>
        public FramebufferUpdateRequest FramebufferUpdateRequest
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets or sets the <see cref="IVncPasswordChallenge"/> to use when authenticating clients.
        /// </summary>
        public IVncPasswordChallenge PasswordChallenge
        {
            get
            {
                return this.passwordChallenge;
            }

            set
            {
                if (this.securityNegotiated)
                {
                    throw new InvalidOperationException("You cannot change the password challenge once the security has been negotiated");
                }

                this.passwordChallenge = value;
            }
        }

        /// <summary>
        /// Gets or sets the <see cref="ILog"/> logger to use when logging.
        /// </summary>
        public ILog Logger
        {
            get { return this.logger; }
            set { this.logger = value; }
        }

        /// <summary>
        /// Gets a lock which should be used before performing any framebuffer updates.
        /// </summary>
        public object FramebufferUpdateRequestLock
        {
            get { return this.fbuSync; }
        }

        /// <summary>
        /// Gets a value indicating whether the server is connected to a client.
        /// </summary>
        /// <value>
        /// <c>true</c> if the server is connected to a client.
        /// </value>
        public bool IsConnected
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets or sets the max rate to send framebuffer updates at, in frames per second.
        /// </summary>
        /// <remarks>
        /// The default is 15.
        /// </remarks>
        public double MaxUpdateRate
        {
            get
            {
                return this.maxUpdateRate;
            }

            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        "Max update rate must be positive.",
                        (Exception)null);
                }

                this.maxUpdateRate = value;
            }
        }

        /// <summary>
        /// Gets or sets user-specific data.
        /// </summary>
        /// <remarks>
        /// Store anything you want here.
        /// </remarks>
        public object UserData
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a function which initializes a new <see cref="IVncFramebufferCache"/> for use by
        /// this <see cref="VncServerSession"/>.
        /// </summary>
        public Func<VncFramebuffer, ILog, IVncFramebufferCache> CreateFramebufferCache
        { get; set; } = (framebuffer, log) => new VncFramebufferCache(framebuffer, log);

        /// <summary>
        /// Closes the connection with the remote client.
        /// </summary>
        public void Close()
        {
            var thread = this.threadMain;
            this.c.Close();
            if (thread != null)
            {
                thread.Join();
            }
        }

        /// <summary>
        /// Starts a session with a VNC client.
        /// </summary>
        /// <param name="stream">The stream containing the connection.</param>
        /// <param name="options">Session options, if any.</param>
        public void Connect(Stream stream, VncServerSessionOptions options = null)
        {
            Throw.If.Null(stream, "stream");

            lock (this.c.SyncRoot)
            {
                this.Close();

                this.options = options ?? new VncServerSessionOptions();
                this.c.Stream = stream;

                this.threadMain = new Thread(this.ThreadMain);
                this.threadMain.IsBackground = true;
                this.threadMain.Start();
            }
        }

        /// <summary>
        /// Starts a session with a VNC client.
        /// </summary>
        /// <param name="stream">The stream containing the connection.</param>
        /// <param name="options">Session options, if any.</param>
        public void ConnectNoThread(Stream stream, VncServerSessionOptions options = null)
        {
            Throw.If.Null(stream, "stream");

            lock (this.c.SyncRoot)
            {
                this.Close();

                this.options = options ?? new VncServerSessionOptions();
                this.c.Stream = stream;

                this.ThreadMain();
            }
        }

        /// <summary>
        /// Tells the client to play a bell sound.
        /// </summary>
        public void Bell()
        {
            lock (this.c.SyncRoot)
            {
                if (!this.IsConnected)
                {
                    return;
                }

                this.c.SendByte((byte)2);
            }
        }

        /// <summary>
        /// Notifies the client that the local clipboard has changed.
        /// If you are implementing clipboard integration, use this to set the remote clipboard.
        /// </summary>
        /// <param name="data">The contents of the local clipboard.</param>
        public void SendLocalClipboardChange(string data)
        {
            Throw.If.Null(data, "data");

            lock (this.c.SyncRoot)
            {
                if (!this.IsConnected)
                {
                    return;
                }

                this.c.SendByte((byte)3);
                this.c.Send(new byte[3]);
                this.c.SendString(data, true);
            }
        }

        /// <summary>
        /// Sets the framebuffer source.
        /// </summary>
        /// <param name="source">The framebuffer source, or <see langword="null"/> if you intend to handle the framebuffer manually.</param>
        public void SetFramebufferSource(IVncFramebufferSource source)
        {
            this.fbSource = source;
        }

        /// <summary>
        /// Notifies the framebuffer update thread to check for recent changes.
        /// </summary>
        public void FramebufferChanged()
        {
            this.requester.Signal();
        }

        /// <inheritdoc/>
        public void FramebufferManualBeginUpdate()
        {
            this.fbuRectangles.Clear();
        }

        /// <summary>
        /// Queues an update corresponding to one region of the framebuffer being copied to another.
        /// </summary>
        /// <param name="target">
        /// The updated <see cref="VncRectangle"/>.
        /// </param>
        /// <param name="sourceX">
        /// The X coordinate of the source.
        /// </param>
        /// <param name="sourceY">
        /// The Y coordinate of the source.
        /// </param>
        /// <remarks>
        /// Do not call this method without holding <see cref="VncServerSession.FramebufferUpdateRequestLock"/>.
        /// </remarks>
        public void FramebufferManualCopyRegion(VncRectangle target, int sourceX, int sourceY)
        {
            if (!this.clientEncoding.Contains(VncEncoding.CopyRect))
            {
                var source = new VncRectangle(sourceX, sourceY, target.Width, target.Height);
                var region = VncRectangle.Union(source, target);

                if (region.Area > source.Area + target.Area)
                {
                    this.FramebufferManualInvalidate(new[] { source, target });
                }
                else
                {
                    this.FramebufferManualInvalidate(region);
                }

                return;
            }

            var contents = new byte[4];
            VncUtility.EncodeUInt16BE(contents, 0, (ushort)sourceX);
            VncUtility.EncodeUInt16BE(contents, 2, (ushort)sourceY);
            this.AddRegion(target, VncEncoding.CopyRect, contents);
        }

        /// <inheritdoc/>
        public void FramebufferManualInvalidateAll()
        {
            this.FramebufferManualInvalidate(new VncRectangle(0, 0, this.Framebuffer.Width, this.Framebuffer.Height));
        }

        /// <inheritdoc/>
        public void FramebufferManualInvalidate(VncRectangle region)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            List<long> times = new List<long>();

            var fb = this.Framebuffer;
            var cpf = this.clientPixelFormat;
            region = VncRectangle.Intersect(region, new VncRectangle(0, 0, this.clientWidth, this.clientHeight));
            if (region.IsEmpty)
            {
                return;
            }

            times.Add(sw.ElapsedMilliseconds);

            int x = region.X, y = region.Y, w = region.Width, h = region.Height, bpp = cpf.BytesPerPixel;
            var contents = new byte[w * h * bpp];

            times.Add(sw.ElapsedMilliseconds);

            VncPixelFormat.Copy(
                fb.GetBuffer(),
                fb.Width,
                fb.Stride,
                fb.PixelFormat,
                region,
                contents,
                w,
                w * bpp,
                cpf);

            times.Add(sw.ElapsedMilliseconds);

            if (this.clientEncoding.Contains(VncEncoding.Zlib))
            {
                contents = ZLibHelper.CompressData(contents);

                times.Add(sw.ElapsedMilliseconds);
                this.AddRegion(region, VncEncoding.Zlib, contents);

                times.Add(sw.ElapsedMilliseconds);
            }
            else
            {
                this.AddRegion(region, VncEncoding.Raw, contents);
            }
        }

        /// <inheritdoc/>
        public void FramebufferManualInvalidate(VncRectangle[] regions)
        {
            Throw.If.Null(regions, "regions");
            foreach (var region in regions)
            {
                this.FramebufferManualInvalidate(region);
            }
        }

        int all_sent_bytes_session = 0;
        Stopwatch all_sw = new Stopwatch();

        /// <inheritdoc/>s
        public bool FramebufferManualEndUpdate()
        {
            if (!this.all_sw.IsRunning)
            {
                this.all_sw.Start();
            }

            long t0 = sw.ElapsedMilliseconds;

            var fb = this.Framebuffer;
            if (this.clientWidth != fb.Width || this.clientHeight != fb.Height)
            {
                if (this.clientEncoding.Contains(VncEncoding.PseudoDesktopSize))
                {
                    var region = new VncRectangle(0, 0, fb.Width, fb.Height);
                    this.AddRegion(region, VncEncoding.PseudoDesktopSize, new byte[0]);
                    this.clientWidth = this.Framebuffer.Width;
                    this.clientHeight = this.Framebuffer.Height;
                }
            }

            if (this.fbuRectangles.Count == 0)
            {
                return false;
            }

            this.FramebufferUpdateRequest = null;

            lock (this.c.SyncRoot)
            {
                this.c.Send(new byte[2] { 0, 0 });
                this.c.SendUInt16BE((ushort)this.fbuRectangles.Count);

                int all_sent_bytes = 0;

                int num = this.fbuRectangles.Count;
                foreach (var rectangle in this.fbuRectangles)
                {
                    List<byte> info = new List<byte>();

                    // this.c.SendRectangle(rectangle.Region);
                    var buffer = new byte[8];
                    VncUtility.EncodeUInt16BE(buffer, 0, (ushort)rectangle.Region.X);
                    VncUtility.EncodeUInt16BE(buffer, 2, (ushort)rectangle.Region.Y);
                    VncUtility.EncodeUInt16BE(buffer, 4, (ushort)rectangle.Region.Width);
                    VncUtility.EncodeUInt16BE(buffer, 6, (ushort)rectangle.Region.Height);
                    info.AddRange(buffer);

                    // this.c.SendUInt32BE((uint)rectangle.Encoding);
                    info.AddRange(VncUtility.EncodeUInt32BE((uint)rectangle.Encoding));

                    //if (rectangle.Encoding == VncEncoding.Zlib)
                    {
                        // this.c.SendUInt32BE((uint)rectangle.Contents.Length);
                        info.AddRange(VncUtility.EncodeUInt32BE((uint)rectangle.Contents.Length));
                    }

                    //Console.WriteLine($"{num--}) {rectangle.Region} {rectangle.Contents.Length}");

                    // this.c.Send(rectangle.Contents);
                    info.AddRange(rectangle.Contents);
                    this.c.Send(info.ToArray());

                    int bytesDesired = rectangle.Region.Width * rectangle.Region.Height * this.Framebuffer.PixelFormat.BytesPerPixel;

                    all_sent_bytes += rectangle.Contents.Length;
                    this.all_sent_bytes_session += rectangle.Contents.Length;
                }

                t0 = sw.ElapsedMilliseconds - t0;
                double rate = (this.all_sent_bytes_session / 1024) / (this.all_sw.ElapsedMilliseconds / 1000.0);
                Console.WriteLine($"Send size {all_sent_bytes} B | Rects={this.fbuRectangles.Count} | Rate={rate.ToString("0.0")} [kBps] " + t0 + "ms");

                if (this.all_sw.ElapsedMilliseconds > 10000)
                {
                    this.all_sw.Restart();
                    this.all_sent_bytes_session = 0;
                }

                this.fbuRectangles.Clear();
                return true;
            }
        }

        /// <summary>
        /// Raises the <see cref="PasswordProvided"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected virtual void OnPasswordProvided(PasswordProvidedEventArgs e)
        {
            var ev = this.PasswordProvided;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="CreatingDesktop"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected virtual void OnCreatingDesktop(CreatingDesktopEventArgs e)
        {
            var ev = this.CreatingDesktop;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="Connected"/> event.
        /// </summary>
        protected virtual void OnConnected()
        {
            var ev = this.Connected;
            if (ev != null)
            {
                ev(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raises the <see cref="ConnectionFailed"/> event.
        /// </summary>
        protected virtual void OnConnectionFailed()
        {
            var ev = this.ConnectionFailed;
            if (ev != null)
            {
                ev(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raises the <see cref="Closed"/> event.
        /// </summary>
        protected virtual void OnClosed()
        {
            var ev = this.Closed;
            if (ev != null)
            {
                ev(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raises the <see cref="FramebufferCapturing"/> event.
        /// </summary>
        protected virtual void OnFramebufferCapturing()
        {
            var ev = this.FramebufferCapturing;
            if (ev != null)
            {
                ev(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Raises the <see cref="FramebufferUpdating"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected virtual void OnFramebufferUpdating(FramebufferUpdatingEventArgs e)
        {
            var ev = this.FramebufferUpdating;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="KeyChanged"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected void OnKeyChanged(KeyChangedEventArgs e)
        {
            var ev = this.KeyChanged;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="PointerChanged"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected void OnPointerChanged(PointerChangedEventArgs e)
        {
            var ev = this.PointerChanged;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="RemoteClipboardChanged"/> event.
        /// </summary>
        /// <param name="e">
        /// The event arguments.
        /// </param>
        protected virtual void OnRemoteClipboardChanged(RemoteClipboardChangedEventArgs e)
        {
            var ev = this.RemoteClipboardChanged;
            if (ev != null)
            {
                ev(this, e);
            }
        }

        private void ThreadMain()
        {
            this.requester = new PeriodicThread();

            try
            {
                this.InitFramebufferEncoder();

                AuthenticationMethod[] methods = this.NegotiateVersion();
                this.NegotiateSecurity(methods);
                this.NegotiateDesktop();
                this.NegotiateEncodings();

                this.requester.Start(
                    () => this.FramebufferSendChanges(),
                    () => this.MaxUpdateRate,
                    false);

                this.IsConnected = true;
                this.logger?.Log(LogLevel.Info, () => "The client has connected successfully");

                this.OnConnected();

                while (true)
                {
                    var command = (VncMessageType)this.c.ReceiveByte();

                    this.logger?.Log(LogLevel.Info, () => $"Received the {command} command.");

                    switch (command)
                    {
                        case VncMessageType.SetPixelFormat://0
                            this.HandleSetPixelFormat();
                            break;

                        case VncMessageType.SetEncodings://2
                            this.HandleSetEncodings();
                            break;

                        case VncMessageType.FrameBufferUpdateRequest://3
                            this.HandleFramebufferUpdateRequest();
                            break;

                        case VncMessageType.KeyEvent://4
                            this.HandleKeyEvent();
                            break;

                        case VncMessageType.PointerEvent://5
                            this.HandlePointerEvent();
                            break;

                        case VncMessageType.ClientCutText://6
                            this.HandleReceiveClipboardData();
                            break;

                        default:
                            VncStream.Require(
                                false,
                                "Unsupported command.",
                                VncFailureReason.UnrecognizedProtocolElement);

                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                this.logger?.Log(LogLevel.Error, () => $"VNC server session stopped due to: {exception.Message}");
            }

            this.c.Close();
            this.requester.Stop();

            this.c.Stream = null;
            if (this.IsConnected)
            {
                this.IsConnected = false;
                this.OnClosed();
            }
            else
            {
                this.OnConnectionFailed();
            }
        }

        private AuthenticationMethod[] NegotiateVersion()
        {
            AuthenticationMethod[] methods = new AuthenticationMethod[0];
            this.logger?.Log(LogLevel.Info, () => "Negotiating the version.");

            this.c.SendVersion(new Version(3, 8));

            this.clientVersion = this.c.ReceiveVersion();
            if (this.clientVersion == new Version(3, 8))
            {
                methods = new[]
                {
                    this.options.AuthenticationMethod == AuthenticationMethod.Password
                        ? AuthenticationMethod.Password : AuthenticationMethod.None,
                };
            }

            var supportedMethods = $"Supported autentication method are {string.Join(" ", methods)}";

            this.logger?.Log(LogLevel.Info, () => $"The client version is {this.clientVersion}");
            this.logger?.Log(LogLevel.Info, () => supportedMethods);

            return methods;
        }

        private void NegotiateSecurity(AuthenticationMethod[] methods)
        {
            //Security

            this.logger?.Log(LogLevel.Info, () => "Negotiating security");

            this.c.SendByte((byte)methods.Length);

            VncStream.Require(
                methods.Length > 0,
                "Client is not allowed in.",
                VncFailureReason.NoSupportedAuthenticationMethods);

            foreach (var method in methods)
            {
                this.c.SendByte((byte)method);
            }

            var selectedMethod = (AuthenticationMethod)this.c.ReceiveByte();

            VncStream.Require(
                methods.Contains(selectedMethod),
                "Invalid authentication method.",
                VncFailureReason.UnrecognizedProtocolElement);

            //Authentication
            //_negotiate_authentication
            bool success = true;

            //_negotiate_std_vnc_auth
            if (selectedMethod == AuthenticationMethod.Password)
            {
                var challenge = this.passwordChallenge.GenerateChallenge();
                using (new Utility.AutoClear(challenge))
                {
                    this.c.Send(challenge);

                    var response = this.c.Receive(16);
                    using (new Utility.AutoClear(response))
                    {
                        var e = new PasswordProvidedEventArgs(this.passwordChallenge, challenge, response);
                        this.OnPasswordProvided(e);
                        success = e.IsAuthenticated || (response[0] == 0 && response[15] == 0);
                    }
                }
            }

            //SecurityResult
            //_handle_security_result
            this.c.SendUInt32BE(success ? 0 : (uint)1);

            VncStream.Require(
                success,
                "Failed to authenticate.",
                VncFailureReason.AuthenticationFailed);

            this.logger?.Log(LogLevel.Info, () => "The user authenticated successfully.");
            this.securityNegotiated = true;
        }

        private void NegotiateDesktop()
        {
            //ClientInitialisation
            this.logger?.Log(LogLevel.Info, () => "Negotiating desktop settings");

            byte shareDesktopSetting = this.c.ReceiveByte();
            bool shareDesktop = shareDesktopSetting != 0;

            //ServerInitialisation
            //_negotiate_server_init

            var e = new CreatingDesktopEventArgs(shareDesktop);
            this.OnCreatingDesktop(e);

            var fbSource = this.fbSource;
            this.Framebuffer = fbSource != null ? fbSource.Capture() : null;

            VncStream.Require(
                this.Framebuffer != null,
                "No framebuffer. Make sure you've called SetFramebufferSource. It can be set to a VncFramebuffer.",
                VncFailureReason.SanityCheckFailed);

            this.clientPixelFormat = this.Framebuffer.PixelFormat;
            this.clientWidth = this.Framebuffer.Width;
            this.clientHeight = this.Framebuffer.Height;
            this.fbuAutoCache = null;

            var info = new List<byte>();

            info.AddRange(VncUtility.EncodeUInt16BE((ushort)this.Framebuffer.Width));
            info.AddRange(VncUtility.EncodeUInt16BE((ushort)this.Framebuffer.Height));
            //this.c.SendUInt16BE((ushort)this.Framebuffer.Width);
            //this.c.SendUInt16BE((ushort)this.Framebuffer.Height);

            var pixelFormat = new byte[VncPixelFormat.Size];
            this.Framebuffer.PixelFormat.Encode(pixelFormat, 0);

            info.AddRange(pixelFormat);
            //this.c.Send(pixelFormat);

            var servername_and_looker_password = $@"{this.Framebuffer.Name}";
            servername_and_looker_password += !string.IsNullOrEmpty(this.PasswordLooker)
                ? $@"\r\n{this.PasswordLooker}"
                : string.Empty;

            var encodedString = VncStream.EncodeString(servername_and_looker_password);
            info.AddRange(VncUtility.EncodeUInt32BE((uint)encodedString.Length));
            info.AddRange(encodedString);
            //this.c.SendString(servername_and_looker_password, true);

            this.c.Send(info.ToArray());

            this.logger?.Log(LogLevel.Info, () => $"The desktop {this.Framebuffer.Name} has initialized with pixel format {this.clientPixelFormat}; the screen size is {this.clientWidth}x{this.clientHeight}");

            byte[] buff = this.c.Receive(20);
        }

        private void NegotiateEncodings()
        {

            this.logger?.Log(LogLevel.Info, () => "Negotiating encodings");

            this.clientEncoding = new VncEncoding[0]; // Default to no encodings.

            byte[] bi2 = this.c.Receive(2);
            ushort len = this.c.ReceiveUInt16BE();

            List<VncEncoding> enc_list = new List<VncEncoding>();
            for (int i = 0; i < len; i++)
            {
                uint enc = this.c.ReceiveUInt32BE();
                enc_list.Add((VncEncoding)enc);
            }

            this.clientEncoding = enc_list.ToArray<VncEncoding>();

            //this.clientEncoding = new VncEncoding[0];

            this.logger?.Log(LogLevel.Info, () => $"Supported encodings method are {string.Join(" ", this.clientEncoding)}");
        }

        private void HandleSetPixelFormat()
        {
            this.c.Receive(3);

            var pixelFormat = this.c.Receive(VncPixelFormat.Size);
            this.clientPixelFormat = VncPixelFormat.Decode(pixelFormat, 0);
        }

        private void HandleSetEncodings()
        {
            this.c.Receive(1);

            int encodingCount = this.c.ReceiveUInt16BE();
            VncStream.SanityCheck(encodingCount <= 0x1ff);
            var clientEncoding = new VncEncoding[encodingCount];
            for (int i = 0; i < clientEncoding.Length; i++)
            {
                uint encoding = this.c.ReceiveUInt32BE();
                clientEncoding[i] = (VncEncoding)encoding;
            }

            this.clientEncoding = clientEncoding;
        }

        private void HandleFramebufferUpdateRequest()
        {
            var incremental = this.c.ReceiveByte() != 0;
            var region = this.c.ReceiveRectangle();

            lock (this.FramebufferUpdateRequestLock)
            {
                this.logger?.Log(LogLevel.Info, () => $"Received a FramebufferUpdateRequest command for {region}");

                region = VncRectangle.Intersect(region, new VncRectangle(0, 0, this.Framebuffer.Width, this.Framebuffer.Height));

                if (region.IsEmpty)
                {
                    return;
                }

                this.FramebufferUpdateRequest = new FramebufferUpdateRequest(incremental, region);
                this.FramebufferChanged();
            }
        }

        private void HandleKeyEvent()
        {
            var pressed = this.c.ReceiveByte() != 0;
            this.c.Receive(2);
            var keysym = (KeySym)this.c.ReceiveUInt32BE();

            this.OnKeyChanged(new KeyChangedEventArgs(keysym, pressed));
        }

        private void HandlePointerEvent()
        {
            int pressedButtons = this.c.ReceiveByte();
            int x = this.c.ReceiveUInt16BE();
            int y = this.c.ReceiveUInt16BE();

            this.OnPointerChanged(new PointerChangedEventArgs(x, y, pressedButtons));
        }

        private void HandleReceiveClipboardData()
        {
            this.c.Receive(3); // padding

            var clipboard = this.c.ReceiveString(0xffffff);

            this.OnRemoteClipboardChanged(new RemoteClipboardChangedEventArgs(clipboard));
        }

        private Stopwatch sw = new Stopwatch();
        private float frames = 0;

        private bool FramebufferSendChanges()
        {
            if (!sw.IsRunning)
                this.sw.Start();

            long t0 = sw.ElapsedMilliseconds;
            long t01 = 0;

            var e = new FramebufferUpdatingEventArgs();

            lock (this.FramebufferUpdateRequestLock)
            {
                if (this.FramebufferUpdateRequest != null)
                {
                    var fbSource = this.fbSource;
                    if (fbSource != null)
                    {
                        var newFramebuffer = fbSource.Capture();

                        t01 = sw.ElapsedMilliseconds - t0;
                        if (newFramebuffer != null && newFramebuffer != this.Framebuffer)
                        {
                            this.Framebuffer = newFramebuffer;
                        }
                    }

                    long t02 = sw.ElapsedMilliseconds - t0;

                    this.OnFramebufferCapturing();

                    this.OnFramebufferUpdating(e);

                    long t03 = sw.ElapsedMilliseconds - t0;

                    if (!e.Handled)
                    {
                        if (this.fbuAutoCache == null || this.fbuAutoCache.Framebuffer != this.Framebuffer)
                        {
                            this.fbuAutoCache = this.CreateFramebufferCache(this.Framebuffer, this.logger);
                        }

                        e.Handled = true;
                        e.SentChanges = this.fbuAutoCache.RespondToUpdateRequest(this);
                    }

                    this.frames++;
                    float fps = (this.frames / Convert.ToSingle(this.sw.ElapsedMilliseconds)) * 1000f;
                    long t04 = sw.ElapsedMilliseconds - t0;

                    Console.WriteLine($"FPS = {fps.ToString("0.0")} T={t01}ms T={t02}ms T={t03}ms T={t04}ms");

                    if (this.sw.ElapsedMilliseconds > 5000)
                    {
                        this.sw.Restart();
                        this.frames = 0L;
                    }
                }
            }

            return e.SentChanges;
        }

        private void AddRegion(VncRectangle region, VncEncoding encoding, byte[] contents)
        {
            this.fbuRectangles.Add(new Rectangle() { Region = region, Encoding = encoding, Contents = contents });

            // Avoid the overflow of updated rectangle count.
            // NOTE: EndUpdate may implicitly add one for desktop resizing.
            if (this.fbuRectangles.Count >= ushort.MaxValue - 1)
            {
                this.FramebufferManualEndUpdate();
                this.FramebufferManualBeginUpdate();
            }
        }

        private void InitFramebufferEncoder()
        {
            this.logger?.Log(LogLevel.Info, () => "Initializing the frame buffer encoder");
            _zlibMemoryStream = new MemoryStream();
            _zlibDeflater = null;
            this.logger?.Log(LogLevel.Info, () => "Initialized the frame buffer encoder");
        }

        private struct Rectangle
        {
            public VncRectangle Region;
            public VncEncoding Encoding;
            public byte[] Contents;
        }
    }
}
