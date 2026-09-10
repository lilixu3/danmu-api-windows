namespace DanmuApi.App.Services;

public sealed class GithubHttpClientResources : IDisposable
{
    private readonly HttpClientHandler _apiHandler;
    private readonly HttpClientHandler _downloadHandler;
    private bool _disposed;

    public GithubHttpClientResources()
    {
        _apiHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate,
        };
        _downloadHandler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate,
        };
        Api = new HttpClient(_apiHandler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Download = new HttpClient(_downloadHandler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public HttpClient Api { get; }
    public HttpClient Download { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Api.Dispose();
        Download.Dispose();
        _apiHandler.Dispose();
        _downloadHandler.Dispose();
    }
}
