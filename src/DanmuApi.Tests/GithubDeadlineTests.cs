using System.Net;
using DanmuApi.Core;

namespace DanmuApi.Tests;

public sealed class GithubDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineIncludesHeadersAndBodyAndAllowsNextRequest(bool stallBody)
    {
        using var handler = new HangingHandler(stallBody);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var remote = new GithubCoreRemote(client, client, new EmptyToken(), new OriginalRoute(), TimeSpan.FromMilliseconds(80));

        var error = await Assert.ThrowsAsync<GithubRemoteException>(() => remote.GetRepositoryAsync(GithubRepositoryReference.Official()).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(GithubFailureKind.Network, error.Kind);
        Assert.Contains("超过", error.Message, StringComparison.Ordinal);
        Assert.True(handler.CancelObserved);

        handler.Hang = false;
        var result = await remote.GetRepositoryAsync(GithubRepositoryReference.Official());
        Assert.Equal("main", result.DefaultBranch);
    }

    [Fact]
    public async Task UserCancellationRemainsCancellation()
    {
        using var handler = new HangingHandler(false);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var remote = new GithubCoreRemote(client, client, new EmptyToken(), new OriginalRoute(), TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remote.GetRepositoryAsync(GithubRepositoryReference.Official(), cancellation.Token));
    }

    private sealed class EmptyToken : IGithubTokenProvider
    {
        public bool IsConfigured => false;
        public string? GetToken() => null;
    }

    private sealed class OriginalRoute : IGithubRoutePreferenceStore
    {
        public GithubRoutePreference Read() => new(GithubProxyCatalog.OriginalId, true);
        public void Confirm(string proxyId) => throw new NotSupportedException();
        public void Invalidate() => throw new NotSupportedException();
    }

    private sealed class HangingHandler(bool stallBody) : HttpMessageHandler
    {
        public bool Hang { get; set; } = true;
        public bool CancelObserved { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!Hang)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"full_name\":\"huangxd-/danmu_api\",\"default_branch\":\"main\"}"),
                };
            }
            if (stallBody)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream(this)) };
            }
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { CancelObserved = true; throw; }
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class HangingStream(HangingHandler handler) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { handler.CancelObserved = true; throw; }
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
