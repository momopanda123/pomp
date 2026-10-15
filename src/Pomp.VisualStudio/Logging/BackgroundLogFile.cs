using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace Pomp.VisualStudio.Logging
{
    /// <summary>
    /// Appends log lines to a rolling file on ONE dedicated background thread, so that logging never does file I/O on the calling
    /// thread. That thread is often the Visual Studio UI thread, and a directory check, a rotation or an append can stall on a slow
    /// disk, a virus scanner or a redirected profile.
    /// <para>
    /// <see cref="Enqueue"/> is non-blocking and never throws. The queue is bounded: while it is full new lines are dropped and
    /// counted, and once the writer has emptied the queue a single line reports how many were lost. The file is appended to and
    /// rotated exactly as the synchronous logger did (a file above the size limit becomes <c>name.1.ext</c>, an older one is replaced).
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> only signals the writer to write what is still queued and return; it never waits for it, because it
    /// runs on the UI thread. The thread is a background thread and cannot keep the process alive.
    /// </para>
    /// </summary>
    internal sealed class BackgroundLogFile : IDisposable
    {
        /// <summary>Lines appended per file write: one write (and one rotation check) per batch, not per line.</summary>
        private const int MaxLinesPerWrite = 256;

        private readonly string filePath;
        private readonly long maxFileBytes;
        private readonly int capacity;
        private readonly Func<int, string> describeDropped;
        private readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();

        // Never disposed: a producer racing with Dispose must still be able to signal it. A wait handle holds no resource that outlives the object.
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private int queued;
        private int dropped;
        private int stopping;
        private int wakePending;

        /// <param name="filePath">The log file; its directory is created on demand.</param>
        /// <param name="maxFileBytes">A file larger than this is rolled over before the next write.</param>
        /// <param name="capacity">Most lines waiting to be written; further lines are dropped and counted.</param>
        /// <param name="describeDropped">Formats the report line for a number of dropped lines (without a line break).</param>
        public BackgroundLogFile(string filePath, long maxFileBytes, int capacity, Func<int, string> describeDropped)
        {
            this.filePath = filePath;
            this.maxFileBytes = maxFileBytes;
            this.capacity = Math.Max(1, capacity);
            this.describeDropped = describeDropped;

            new Thread(WriterLoop) { IsBackground = true, Name = "Pomp log writer" }.Start();
        }

        /// <summary>Queues one formatted line (no trailing line break). Never blocks on file I/O and never throws.</summary>
        public void Enqueue(string line)
        {
            if (Volatile.Read(ref stopping) != 0)
            {
                return;
            }

            if (Interlocked.Increment(ref queued) > capacity)
            {
                Interlocked.Decrement(ref queued);
                Interlocked.Increment(ref dropped);
            }
            else
            {
                queue.Enqueue(line);
            }

            Wake();
        }

        /// <summary>Asks the writer to finish what is queued and stop. Does not wait for it.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopping, 1) != 0)
            {
                return;
            }

            Wake();
        }

        /// <summary>
        /// Signals the writer. One signal per writer round is enough (no kernel call per line): the writer clears the flag before it
        /// drains the queue, so a line enqueued after that always signals again and none is left behind.
        /// </summary>
        private void Wake()
        {
            if (Interlocked.Exchange(ref wakePending, 1) == 0)
            {
                wake.Set();
            }
        }

        private void WriterLoop()
        {
            var batch = new StringBuilder();
            while (true)
            {
                wake.WaitOne();
                Interlocked.Exchange(ref wakePending, 0);

                try
                {
                    while (WriteBatch(batch))
                    {
                    }
                }
                catch (Exception)
                {
                    // Logging must never throw, and an exception escaping this thread would end the process. The lines of this round are lost.
                }

                if (Volatile.Read(ref stopping) != 0 && queue.IsEmpty)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Writes up to <see cref="MaxLinesPerWrite"/> queued lines and, once the queue is empty, the report of the lines that were
        /// dropped while it was full. False when there was nothing to write.
        /// </summary>
        private bool WriteBatch(StringBuilder batch)
        {
            batch.Clear();

            var lines = 0;
            while (lines < MaxLinesPerWrite && queue.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref queued);
                batch.Append(line).Append(Environment.NewLine);
                lines++;
            }

            // Space is available again (the queue is empty): report, once, what was lost while it was full.
            if (queue.IsEmpty)
            {
                var lost = Interlocked.Exchange(ref dropped, 0);
                if (lost > 0)
                {
                    batch.Append(DescribeDropped(lost)).Append(Environment.NewLine);
                }
            }

            if (batch.Length == 0)
            {
                return false;
            }

            Append(batch.ToString());
            return true;
        }

        private string DescribeDropped(int lost)
        {
            try
            {
                return describeDropped(lost);
            }
            catch (Exception)
            {
                return lost + " log lines were dropped.";
            }
        }

        private void Append(string text)
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath)!;
                Directory.CreateDirectory(directory);

                var info = new FileInfo(filePath);
                if (info.Exists && info.Length > maxFileBytes)
                {
                    var rolled = Path.Combine(directory, Path.GetFileNameWithoutExtension(filePath) + ".1" + Path.GetExtension(filePath));
                    try
                    {
                        if (File.Exists(rolled))
                        {
                            File.Delete(rolled);
                        }

                        File.Move(filePath, rolled);
                    }
                    catch (IOException)
                    {
                        // Another VS instance holds the file; keep appending.
                    }
                }

                File.AppendAllText(filePath, text, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Logging must never throw (disk full, locked file, ...).
            }
        }
    }
}
