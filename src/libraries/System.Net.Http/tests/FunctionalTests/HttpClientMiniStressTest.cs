// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http.Functional.Tests
{
    public class HttpClientMiniStress
    {
        private static bool HttpStressEnabled => Environment.GetEnvironmentVariable("HTTP_STRESS") == "1";

        [ConditionalTheory(nameof(HttpStressEnabled))]
        [MemberData(nameof(StressOptions))]
        public void SingleClient_ManyRequests_Sync(int numRequests, int dop, HttpCompletionOption completionOption)
        {
            string responseText = CreateResponse("abcdefghijklmnopqrstuvwxyz");
            using (var client = new HttpClient())
            {
                Parallel.For(0, numRequests, new ParallelOptions { MaxDegreeOfParallelism = dop, TaskScheduler = new ThreadPerTaskScheduler() }, _ =>
                {
                    CreateServerAndMakeRequest(client, completionOption, responseText);
                });
            }
        }

        [ConditionalTheory(nameof(HttpStressEnabled))]
        public async Task SingleClient_ManyRequests_Async(int numRequests, int dop, HttpCompletionOption completionOption)
        {
            string responseText = CreateResponse("abcdefghijklmnopqrstuvwxyz");
            using (var client = new HttpClient())
            {
                await ForCountAsync(numRequests, dop, i => CreateServerAndMakeRequestAsync(client, completionOption, responseText));
            }
        }

        [ConditionalTheory(nameof(HttpStressEnabled))]
        [MemberData(nameof(StressOptions))]
        public void ManyClients_ManyRequests(int numRequests, int dop, HttpCompletionOption completionOption)
        {
            string responseText = CreateResponse("abcdefghijklmnopqrstuvwxyz");
            Parallel.For(0, numRequests, new ParallelOptions { MaxDegreeOfParallelism = dop, TaskScheduler = new ThreadPerTaskScheduler() }, _ =>
            {
                using (var client = new HttpClient())
                {
                    CreateServerAndMakeRequest(client, completionOption, responseText);
                }
            });
        }

        [ConditionalTheory(nameof(HttpStressEnabled))]
        [InlineData(1000000)]
        public void CreateAndDestroyManyClients(int numClients)
        {
            for (int i = 0; i < numClients; i++)
            {
                new HttpClient().Dispose();
            }
        }

        [ConditionalTheory(nameof(HttpStressEnabled))]
        [InlineData(5000)]
        public async Task MakeAndFaultManyRequests(int numRequests)
        {
            using (HttpClient client = new HttpClient())
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                client.Timeout = Timeout.InfiniteTimeSpan;

                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.Listen(numRequests);

                var ep = (IPEndPoint)server.LocalEndPoint;
                Task<string>[] tasks = 
                    (from i in Enumerable.Range(0, numRequests)
                     select client.GetStringAsync($"http://{ep.Address}:{ep.Port}"))
                     .ToArray();

                Assert.All(tasks, t => Assert.Equal(TaskStatus.WaitingForActivation, t.Status));

                server.Dispose();

                foreach (Task<string> task in tasks)
                {
                    await Assert.ThrowsAnyAsync<HttpRequestException>(() => task);
                }
            }
        }

        public static IEnumerable<object[]> StressOptions()
        {
            foreach (int numRequests in new[] { 5000 }) // number of requests
                foreach (int dop in new[] { 1, 32 }) // number of threads
                    foreach (var completionoption in new[] { HttpCompletionOption.ResponseContentRead, HttpCompletionOption.ResponseHeadersRead })
                        yield return new object[] { numRequests, dop, completionoption };
        }

        private static void CreateServerAndMakeRequest(HttpClient client, HttpCompletionOption completionOption, string responseText)
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.Listen(1);

                var ep = (IPEndPoint)server.LocalEndPoint;
                Task<HttpResponseMessage> getAsync = client.GetAsync($"http://{ep.Address}:{ep.Port}", completionOption);

                using (Socket s = server.AcceptAsync().GetAwaiter().GetResult())
                using (var stream = new NetworkStream(s, ownsSocket: false))
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    while (!string.IsNullOrEmpty(reader.ReadLine())) ;
                    writer.Write(responseText);
                    writer.Flush();
                    s.Shutdown(SocketShutdown.Send);
                }

                getAsync.GetAwaiter().GetResult().Dispose();
            }
        }

        private static async Task CreateServerAndMakeRequestAsync(HttpClient client, HttpCompletionOption completionOption, string responseText)
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.Listen(1);

                var ep = (IPEndPoint)server.LocalEndPoint;
                Task<HttpResponseMessage> getAsync = client.GetAsync($"http://{ep.Address}:{ep.Port}", completionOption);

                using (Socket s = await server.AcceptAsync().ConfigureAwait(false))
                using (var stream = new NetworkStream(s, ownsSocket: false))
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    while (!string.IsNullOrEmpty(reader.ReadLine())) ;
                    await writer.WriteAsync(responseText).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                    s.Shutdown(SocketShutdown.Send);
                }

                (await getAsync.ConfigureAwait(false)).Dispose();
            }
        }

        [OuterLoop] // could take several seconds under load
        [Fact]
        public void UnreadResponseMessage_Collectible()
        {
            using (var client = new HttpClient())
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                server.Listen(1);

                var ep = (IPEndPoint)server.LocalEndPoint;
                Func<Task<WeakReference>> getAsync = async () =>
                    new WeakReference(await client.GetAsync($"http://{ep.Address}:{ep.Port}", HttpCompletionOption.ResponseHeadersRead));
                Task<WeakReference> wrt = getAsync();

                using (Socket s = server.AcceptAsync().GetAwaiter().GetResult())
                using (var stream = new NetworkStream(s, ownsSocket: false))
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                using (var writer = new StreamWriter(stream, Encoding.ASCII))
                {
                    while (!string.IsNullOrEmpty(reader.ReadLine())) ;
                    writer.Write(CreateResponse(new string('a', 32 * 1024)));
                    writer.Flush();

                    WeakReference wr = wrt.GetAwaiter().GetResult();
                    Assert.True(SpinWait.SpinUntil(() =>
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        return !wr.IsAlive;
                    }, 10 * 1000), "Response object should have been collected");
                }
            }
        }

        private static string CreateResponse(string asciiBody) =>
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {asciiBody.Length}\r\n\r\n{asciiBody}\r\n";

        private static Task ForCountAsync(int count, int dop, Func<int, Task> bodyAsync)
        {
            var sched = new ThreadPerTaskScheduler();
            int nextAvailableIndex = 0;
            return Task.WhenAll(Enumerable.Range(0, dop).Select(_ => Task.Factory.StartNew(async delegate
            {
                int index;
                while ((index = Interlocked.Increment(ref nextAvailableIndex) - 1) < count)
                {
                    try { await bodyAsync(index); }
                    catch
                    {
                        Volatile.Write(ref nextAvailableIndex, count); // avoid any further iterations
                        throw;
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.None, sched).Unwrap()));
        }

        private sealed class ThreadPerTaskScheduler : TaskScheduler
        {
            protected override void QueueTask(Task task) =>
                Task.Factory.StartNew(() => TryExecuteTask(task), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

            protected override IEnumerable<Task> GetScheduledTasks() => null;
        }
    }
}
