using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PadP2P.App.Services;

internal sealed class UpdateCheckService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Cooanyh/wlangohost/releases/latest";
    private const long MaximumResponseBytes = 262_144;
    private static readonly HttpClient Client = CreateClient();
    private readonly object _sync = new();
    private Task<UpdateRelease>? _inFlight;

    public static Version CurrentVersion
    {
        get
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 2, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    public Task<UpdateRelease> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        Task<UpdateRelease> request;
        lock (_sync)
        {
            if (_inFlight is null || _inFlight.IsCompleted)
            {
                _inFlight = FetchLatestReleaseAsync();
                _ = _inFlight.ContinueWith(
                    completed => { _ = completed.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            request = _inFlight;
        }

        return request.WaitAsync(cancellationToken);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 1,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate |
                                     System.Net.DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WlanGO/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<UpdateRelease> FetchLatestReleaseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        using var response = await Client.GetAsync(
                LatestReleaseApi,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("The GitHub release response was larger than expected.");

        await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var boundedContent = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var bytesRead = await responseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (bytesRead == 0) break;
            if (boundedContent.Length + bytesRead > MaximumResponseBytes)
                throw new InvalidDataException("The GitHub release response was larger than expected.");

            await boundedContent.WriteAsync(buffer.AsMemory(0, bytesRead), timeout.Token).ConfigureAwait(false);
        }

        boundedContent.Position = 0;
        using var release = await JsonDocument.ParseAsync(
                boundedContent,
                new JsonDocumentOptions { MaxDepth = 16 },
                timeout.Token)
            .ConfigureAwait(false);

        if (!release.RootElement.TryGetProperty("tag_name", out var tagProperty) ||
            tagProperty.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The GitHub release response did not contain a version tag.");

        var tag = tagProperty.GetString()?.Trim() ?? string.Empty;
        var versionText = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidDataException("The latest GitHub release tag is not a stable version.");

        return new UpdateRelease(tag, new Version(version.Major, version.Minor, Math.Max(version.Build, 0)));
    }
}

internal sealed record UpdateRelease(string Tag, Version Version);
