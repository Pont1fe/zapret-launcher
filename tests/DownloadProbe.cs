// In-memory transport: no network, DNS, VPN or installed files are touched.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

public static class DownloadProbe
{
    sealed class WaitingStream : Stream
    {
        readonly TaskCompletionSource<int> read = new TaskCompletionSource<int>();
        public void Finish() { read.TrySetResult(0); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        {
            cancellation.Register(() => read.TrySetCanceled());
            return read.Task;
        }
        protected override void Dispose(bool disposing) { read.TrySetException(new ObjectDisposedException("test stream")); base.Dispose(disposing); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    sealed class Handler : HttpMessageHandler
    {
        readonly Stream stream;
        public Handler(Stream stream) { this.stream = stream; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }
    }

    public static async Task<bool> Run(Assembly assembly, string file, bool userCancellation)
    {
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var util = assembly.GetType("ZapretLauncher.Util");
        var field = util.GetField("Http", flags);
        object original = field.GetValue(null);
        var stream = new WaitingStream();
        using (var client = new HttpClient(new Handler(stream)))
        using (var cancellation = new CancellationTokenSource())
        {
            client.Timeout = TimeSpan.FromMilliseconds(userCancellation ? 10000 : 100);
            if (userCancellation) cancellation.CancelAfter(100);
            field.SetValue(null, client);
            try
            {
                var release = Activator.CreateInstance(assembly.GetType("ZapretLauncher.Release"));
                release.GetType().GetField("Url").SetValue(release, "https://example.invalid/fake");
                Task task = (Task)util.GetMethod("Download", flags).Invoke(null, new object[] { release, file, cancellation.Token });
                bool timely = await Task.WhenAny(task, Task.Delay(1500)) == task;
                stream.Finish();
                bool failed = false;
                try { await task; } catch { failed = true; }
                return timely && failed && !File.Exists(file);
            }
            finally { field.SetValue(null, original); }
        }
    }
}
