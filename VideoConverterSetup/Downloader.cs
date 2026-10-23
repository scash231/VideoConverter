using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VideoConverterSetup;

/// <summary>Streams a download to disk with progress and an optional SHA-256 check.</summary>
public static class Downloader
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VideoConverterSetup/1.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>
    /// Downloads <paramref name="uri"/> to <paramref name="target"/> (written as target.partial first, moved into place
    /// only when complete and, if <paramref name="sha256"/> is set, verified). <paramref name="progress"/> gets
    /// (bytes read, total bytes or the estimate).
    /// </summary>
    public static async Task DownloadAsync(Uri uri, string target, string? sha256, long expectedSize,
        IProgress<(long read, long total)>? progress, CancellationToken ct)
    {
        var partial = target + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Log.Write($"GET {uri} -> {target}");

        try
        {
            if (uri.IsFile)
            {
                // local releases (testing with /manifest=<path>)
                await CopyWithProgressAsync(File.OpenRead(uri.LocalPath), partial, new FileInfo(uri.LocalPath).Length, progress, ct).ConfigureAwait(false);
            }
            else
            {
                using var res = await SendWithTimeoutAsync(uri, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                    throw new SetupException($"Download failed: {(int)res.StatusCode} {res.ReasonPhrase}\n{uri}");
                var total = res.Content.Headers.ContentLength ?? expectedSize;
                var stream = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await CopyWithProgressAsync(stream, partial, total, progress, ct).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(sha256))
            {
                var actual = HashFile(partial);
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write($"checksum mismatch for {uri}: expected {sha256}, got {actual}");
                    throw new SetupException($"The downloaded file {Path.GetFileName(target)} is damaged (checksum mismatch). Please try again.");
                }
            }

            ReplaceFile(partial, target);
        }
        catch (HttpRequestException ex)
        {
            throw new SetupException($"Could not connect to {uri.Host}. Check your internet connection.\n({ex.InnerException?.Message ?? ex.Message})", ex);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(Uri uri, CancellationToken ct)
    {
        // timeout only for getting the headers; the body may take as long as it takes (stall check in the copy loop)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SetupException($"{uri.Host} did not answer. Check your internet connection and try again.");
        }
    }

    private static async Task CopyWithProgressAsync(Stream source, string path, long total,
        IProgress<(long read, long total)>? progress, CancellationToken ct)
    {
        using (source)
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long read = 0;
            var lastReport = DateTime.MinValue;
            while (true)
            {
                // a stalled connection (no data for 60s) fails instead of hanging forever
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(TimeSpan.FromSeconds(60));
                int n;
                try
                {
                    n = await source.ReadAsync(buffer, 0, buffer.Length, stall.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is OperationCanceledException || ex is IOException))
                {
                    throw new SetupException("The download stopped. Check your internet connection and try again.", ex);
                }
                if (n == 0) break;
                await file.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
                read += n;
                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds > 80)
                {
                    lastReport = now;
                    progress?.Report((read, Math.Max(total, read)));
                }
            }
            progress?.Report((read, Math.Max(total, read)));
        }
    }

    public static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        var hash = sha.ComputeHash(fs);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    /// <summary>Moves <paramref name="source"/> over <paramref name="target"/>, retrying while the old file is still locked.</summary>
    public static void ReplaceFile(string source, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(source, target);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(250);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new SetupException($"{Path.GetFileName(target)} is in use and can't be replaced. Close VideoConverter and try again.", ex);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
