using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemoteViewing.Utility
{
    public class BufferQueue
    {
        private byte[] buff;
        private int head, tail, size;
        private volatile int count;
        private Mutex appendmut, readmut, countmut;

        // Constructor
        public BufferQueue(int size)
        {
            appendmut = new Mutex();
            readmut = new Mutex();
            countmut = new Mutex();
            buff = new byte[size];
            this.size = size;
            head = 0;
            tail = 0;
            count = 0;
        }

        // Get the number of bytes in the buffer
        public int Count { get { return count; } }
        // The maximum size of the buffer
        public int Size { get { return size; } }

        // Append bytes to the buffer
        public void append(byte[] data) { if (data != null) append(data, 0, data.Length); }
        public void append(byte[] data, int offset, int length)
        {
            if (data == null) return;
            if (data.Length < offset + length) { throw new Exception("array index out of bounds. offset + length extends beyond the length of the array."); }

            appendmut.WaitOne();
            // We need to acquire the mutex so that this.tail doesn't change.
            for (int i = 0; i < length; i++)
                buff[(i + this.tail) % this.size] = data[i + offset];
            this.tail = (length + this.tail) % this.size;
            countmut.WaitOne();
            // We need to acquire the mutex so that this.count doesn't change.
            this.count = this.count + length;
            if (this.count > this.size)
                throw new Exception("Buffer overflow error.");
            countmut.ReleaseMutex();
            appendmut.ReleaseMutex();
        }

        // Read bytes from the buffer
        public string read()
        {
            byte[] data = new byte[size];
            read(data);
            return System.Text.ASCIIEncoding.ASCII.GetString(data);
        }
        public int read(byte[] data) { if (data != null) return read(data, 0, data.Length); else return 0; }
        public int read(byte[] data, int offset, int length)
        {
            if (data == null) return 0;
            if (data.Length < offset + length) throw new Exception("array index out of bounds. offset + length extends beyond the length of the array.");

            int readlength = 0;

            // We need to acquire the mutex so that this.head doesn't change.
            readmut.WaitOne();

            for (int i = 0; i < length; i++)
            {
                if (i == count) break;
                data[i + offset] = buff[(i + head) % this.size];
                readlength++;
            }
            this.head = (readlength + this.head) % this.size;

            // We need to acquire the mutex so that this.count doesn't change.
            countmut.WaitOne();
            this.count = this.count - readlength;
            countmut.ReleaseMutex();
            readmut.ReleaseMutex();
            return readlength;
        }

        // Peek at the buffer
        public string peek()
        {
            byte[] data = new byte[size];
            peek(data);
            return System.Text.ASCIIEncoding.ASCII.GetString(data);
        }
        public int peek(byte[] data) { if (data != null) return peek(data, 0, data.Length); else return 0; }
        public int peek(byte[] data, int offset, int length)
        {
            if (data == null) return 0;
            if (data.Length < offset + length) throw new Exception("array index out of bounds. offset + length extends beyond the length of the array.");

            int readlength = 0;

            // We need to acquire the mutex so that this.head doesn't change.
            readmut.WaitOne();

            for (int i = 0; i < length; i++)
            {
                if (i == count) break;
                data[i + offset] = buff[(i + head) % this.size];
                readlength++;
            }

            readmut.ReleaseMutex();
            return readlength;
        }

        public void clear()
        {
            readmut.WaitOne();
            appendmut.WaitOne();
            countmut.WaitOne();
            this.head = 0;
            this.tail = 0;
            this.count = 0;
            countmut.ReleaseMutex();
            appendmut.ReleaseMutex();
            readmut.ReleaseMutex();
        }
    }
}
