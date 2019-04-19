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

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RemoteViewing.Utility;

namespace RemoteViewing.Vnc
{
    /// <summary>
    /// Connects to a remote VNC server and interacts with it.
    /// </summary>
    public class VncInterceptorClient : VncClient
    {
        private VcnInterceptorStream ms;

        /// <summary>
        /// Initializes a new instance of the <see cref="VncInterceptorClient"/> class.
        /// </summary>
        public VncInterceptorClient(CancellationToken token)
            : base()
        {
            this.ms = new VcnInterceptorStream(token);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VncInterceptorClient"/> class.
        /// </summary>
        /// <param name="passwordChallenge">
        /// A <see cref="IVncPasswordChallenge"/> which can generate password challenges.
        /// </param>
        public VncInterceptorClient(CancellationToken token, IVncPasswordChallenge passwordChallenge)
            : base(passwordChallenge)
        {
            this.ms = new VcnInterceptorStream(token);
        }

        public void Connect(VncClientConnectOptions options = null)
        {
            options = options ?? new VncClientConnectOptions();
            options.Password = new char[] { 'e', 'm', 'p', 't', 'y', };
            options.OnDemandMode = false;

            this.Connect(this.ms, options);
        }

        public void Write(byte[] buff)
        {
            this.ms.WriteBuffer(buff, 0, buff.Length);
        }

        public void Write(byte[] buff, int length)
        {
            this.ms.WriteBuffer(buff, 0, length);
        }

        public void Write(byte[] buff, int offset, int length)
        {
            this.ms.WriteBuffer(buff, offset, length);
        }

        public override void Dispose()
        {
            this.ms.Close();
            base.Dispose();
        }
    }
}
