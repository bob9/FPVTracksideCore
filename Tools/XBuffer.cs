using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tools
{

    public class XBuffer<T> : IDisposable
    {
        private T[] buffers;
        public T[] Buffers { get { return buffers; } }

        private bool[] writtenNotRead;

        private int writeIndex;
        private int readIndex;
        private int lastID;

        public XBuffer(params T[] buf)
        {
            if (buf.Length < 2)
            {
                throw new NotSupportedException();
            }

            buffers = buf;
            writtenNotRead = new bool[buf.Length];
            writeIndex = 1;
            readIndex = 0;
        }

        public XBuffer(short count, params object[] args)
        {
            buffers = new T[count];
            writtenNotRead = new bool[count];
            writeIndex = 1;
            readIndex = 0;

            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = (T)Activator.CreateInstance(typeof(T), args);
            }
        }

        public void Dispose()
        {
            if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
            {
                for (int i = 0; i < buffers.Length; i++)
                {
                    if (buffers[i] != null)
                    {
                        ((IDisposable)buffers[i]).Dispose();
                        buffers[i] = default(T);
                    }
                }
            }
        }

        public bool GetWritable(out T t)
        {
            t = buffers[writeIndex];
            return !writtenNotRead[writeIndex];
        }

        public void WriteOne(T t)
        {
            int nextIndex = (writeIndex + 1) % buffers.Length;

            // Never overtake read..
            if (nextIndex == readIndex)
            {
                // Dump the frame :(
                return;
            }

            buffers[writeIndex] = t;
            writtenNotRead[writeIndex] = true;
            writeIndex = nextIndex;
        }

        public bool ReadOne(out T t, int id)
        {
            if (id == lastID)
            {
                t = buffers[readIndex];
                return true;
            }

            for (int i = 0; i < buffers.Length; i++)
            {
                int index = (readIndex + i) % buffers.Length;

                if (writtenNotRead[index])
                {
                    readIndex = index;
                    t = buffers[index];
                    writtenNotRead[index] = false;
                    lastID = id;
                    return true;
                }
            }

            t = default(T);
            return false;
        }

        /// <summary>
        /// Reads the most recent frame, skipping any buffered frames to minimize latency
        /// </summary>
        public bool ReadLatest(out T t, int id)
        {
            if (id == lastID)
            {
                t = buffers[readIndex];
                return true;
            }

            // Find the most recent written frame by searching backwards from writeIndex
            int latestIndex = -1;
            for (int i = 1; i <= buffers.Length; i++)
            {
                int index = (writeIndex - i + buffers.Length) % buffers.Length;
                if (writtenNotRead[index])
                {
                    latestIndex = index;
                    break;
                }
            }

            if (latestIndex >= 0)
            {
                // Mark all older frames as read to skip them
                for (int i = 0; i < buffers.Length; i++)
                {
                    if (i != latestIndex && writtenNotRead[i])
                    {
                        writtenNotRead[i] = false;
                    }
                }

                readIndex = latestIndex;
                t = buffers[latestIndex];
                writtenNotRead[latestIndex] = false;
                lastID = id;
                return true;
            }

            t = default(T);
            return false;
        }

        public void Debug()
        {
            Console.WriteLine("Read " + readIndex + " Write " + writeIndex + " b " + string.Join(", ", writtenNotRead.Select(r => r ? "1" : "0")));
        }
    }
}
