using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace ZeepTwitchChat.Chat.Emotes
{
    /// <summary>
    /// Downloads emote images on background threads and hands the bytes back on the Unity
    /// main thread, where textures may be created.
    ///
    /// Deliberately uses <see cref="HttpWebRequest"/> rather than UnityWebRequest: no
    /// coroutines, no Unity API on the worker thread, nothing to remember to dispose.
    /// </summary>
    public sealed class EmoteImageStore : IDisposable
    {
        private const int MaxConcurrent = 3;
        private const int TimeoutMilliseconds = 10000;

        /// <summary>
        /// Largest image the mod will accept. Twitch emotes peak around 112px and animated GIFs
        /// at a few hundred kilobytes, so this is generous; it exists so a hostile or broken
        /// response cannot exhaust memory.
        /// </summary>
        private const int MaxImageBytes = 4 * 1024 * 1024;

        private readonly object gate = new object();
        private readonly Dictionary<string, bool> requested = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Queue<KeyValuePair<string, string>> queue = new Queue<KeyValuePair<string, string>>();
        private readonly List<DownloadedImage> completed = new List<DownloadedImage>();
        private readonly List<Thread> workers = new List<Thread>();
        private readonly int maxEntries;

        private bool running = true;

        public EmoteImageStore(int maxEntries)
        {
            this.maxEntries = Math.Max(16, maxEntries);
        }

        /// <summary>Number of images downloaded so far, for the log.</summary>
        public int Downloaded { get; private set; }

        /// <summary>Number of images that failed, for the log.</summary>
        public int Failed { get; private set; }

        /// <summary>Queues an emote for download. Repeat calls for the same name are ignored.</summary>
        public void Request(string name, string url)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                return;
            }

            // Only the provider CDNs, over https: ids and 7TV host urls come from remote data.
            if (!EmoteUrlPolicy.IsAllowed(url, EmoteUrlKind.Image))
            {
                return;
            }

            lock (gate)
            {
                if (!running || requested.Count >= maxEntries || requested.ContainsKey(name))
                {
                    return;
                }

                requested[name] = true;
                queue.Enqueue(new KeyValuePair<string, string>(name, url));

                if (workers.Count < MaxConcurrent)
                {
                    Thread worker = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = "ZeepTwitchChat emote " + workers.Count
                    };
                    workers.Add(worker);
                    worker.Start();
                }
                else
                {
                    Monitor.Pulse(gate);
                }
            }
        }

        /// <summary>True when this emote has already been queued or fetched.</summary>
        public bool IsRequested(string name)
        {
            lock (gate)
            {
                return requested.ContainsKey(name);
            }
        }

        /// <summary>True when the cache still has room for another emote.</summary>
        public bool AcceptsMore
        {
            get
            {
                lock (gate)
                {
                    return running && requested.Count < maxEntries;
                }
            }
        }

        /// <summary>
        /// Takes one completed download. Returns false when nothing is ready, so the caller
        /// can yield between textures on the main thread.
        /// </summary>
        public bool TryTakeCompleted(out string name, out byte[] bytes)
        {
            lock (gate)
            {
                if (completed.Count == 0)
                {
                    name = null;
                    bytes = null;
                    return false;
                }

                DownloadedImage image = completed[0];
                completed.RemoveAt(0);
                name = image.Name;
                bytes = image.Bytes;
                Downloaded++;
                return true;
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                KeyValuePair<string, string> item;

                lock (gate)
                {
                    while (running && queue.Count == 0)
                    {
                        Monitor.Wait(gate, 500);
                    }

                    if (!running)
                    {
                        return;
                    }

                    item = queue.Dequeue();
                }

                byte[] bytes = null;
                try
                {
                    bytes = Fetch(item.Value);
                }
                catch (Exception)
                {
                    // Network hiccups are expected; the emote simply stays as text.
                }

                lock (gate)
                {
                    if (!running)
                    {
                        return;
                    }

                    if (bytes == null || bytes.Length == 0)
                    {
                        Failed++;
                    }
                    else
                    {
                        completed.Add(new DownloadedImage(item.Key, bytes));
                    }
                }
            }
        }

        private static byte[] Fetch(string url)
        {
            if (!EmoteUrlPolicy.TryNormalise(url, EmoteUrlKind.Image, out Uri uri))
            {
                return null;
            }

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "ZeepTwitchChat/1.3 (+https://mod.io/g/zeepkist)";
            request.AllowAutoRedirect = false;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return null;
                }

                return ReadCapped(stream, MaxImageBytes);
            }
        }

        /// <summary>
        /// Reads at most <paramref name="limit"/> bytes and gives up beyond that, so a huge or
        /// endless response cannot grow the heap.
        /// </summary>
        private static byte[] ReadCapped(Stream stream, int limit)
        {
            byte[] chunk = new byte[16384];

            using (MemoryStream buffer = new MemoryStream())
            {
                while (true)
                {
                    int read = stream.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                    {
                        return buffer.ToArray();
                    }

                    if (buffer.Length + read > limit)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                running = false;
                Monitor.PulseAll(gate);
                queue.Clear();
                completed.Clear();
            }

            for (int i = 0; i < workers.Count; i++)
            {
                if (workers[i].IsAlive)
                {
                    workers[i].Join(500);
                }
            }

            workers.Clear();
        }

        private struct DownloadedImage
        {
            public DownloadedImage(string name, byte[] bytes)
            {
                Name = name;
                Bytes = bytes;
            }

            public string Name { get; }

            public byte[] Bytes { get; }
        }
    }
}
