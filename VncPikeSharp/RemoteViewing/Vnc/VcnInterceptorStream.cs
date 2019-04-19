using RemoteViewing.Logging;
using RemoteViewing.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteViewing.Vnc
{
    /// <summary>
    /// Перехват VNC обмена
    /// </summary>
    public class VcnInterceptorStream : Stream
    {
        //private Queue<byte[]> _queue = new Queue<byte[]>();
        private MemoryStream _ms = new MemoryStream();
        private BufferQueue _bq = new BufferQueue(1024 * 1024 * 10);
        private CancellationToken token;

        /// <summary>
        /// Gets an <see cref="object"/> that can be used to synchronize access to the <see cref="VcnInterceptorStream"/>.
        /// </summary>
        public object SyncRoot
        {
            get;
            private set;
        } = new object();

        public VcnInterceptorStream(CancellationToken token)
        {
            this.token = token;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return 0L;
        }

        public override void SetLength(long value)
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            /*lock (this._queue)
            {
                if (this._queue.Count == 0)
                {
                    return 0;
                }

                byte[] buff = this._queue.Dequeue();
                Array.Copy(buff, 0, buffer, offset, count);
                return buff.Length;
            }*/

            int delay = 20;

            while (!this.token.IsCancellationRequested)
            {
                //if (this._ms.Length > 0)
                if (this._bq.Count > 0)
                {
                    break;
                }

                try
                {
                    Task.Delay(delay, this.token).Wait();
                }
                catch (Exception)
                {
                    return 0;
                }
            }

            lock (this.SyncRoot)
            {
                return this._bq.read(buffer, offset, count);

                using (MemoryStream msi = new MemoryStream())
                {
                    this._ms.Position = 0;
                    this._ms.CopyTo(msi);
                    msi.Position = 0;
                    int res = msi.Read(buffer, offset, count);
                    this._ms.Position = 0;
                    this._ms.SetLength(0L);
                    msi.CopyTo(this._ms);

                    return res;
                }
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        { }

        public void WriteBuffer(byte[] buffer, int offset, int count)
        {
            /*byte[] buff = new byte[count];
            Array.Copy(buffer, 0, buff, offset, count);
            lock (this._queue)
            {
                this._queue.Enqueue(buff);
            }*/

            lock (this.SyncRoot)
            {
                this._bq.append(buffer, offset, count);
                //this._ms.Write(buffer, offset, count);
                //ConsoleWriter.Go("WRI", buffer, count);
            }
        }
    }
}
