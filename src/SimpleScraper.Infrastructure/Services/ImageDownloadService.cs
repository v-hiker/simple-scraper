using System.Buffers.Binary;
using System.Net;

namespace SimpleScraper.Services;

/// <summary>Downloads to a neighboring temporary file and commits only complete accepted content.</summary>
public sealed class ImageDownloadService
{
    private const long MaximumBytes = 32L * 1024 * 1024;
    private static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private readonly HttpClient _http;
    private readonly CancellationToken _cancellation;

    /// <param name="http">Optional transport. This service does not dispose a caller's HttpClient.</param>
    public ImageDownloadService(HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        _http = http ?? DefaultHttp;
        _cancellation = cancellationToken;
    }

    public Task<bool> DownloadAnyImageAsync(string? url, string destination) => DownloadAsync(url, destination, false);
    public Task<bool> DownloadPngAsync(string? url, string destination) => DownloadAsync(url, destination, true);

    private async Task<bool> DownloadAsync(string? url, string destination, bool png)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await DownloadAttemptAsync(url, destination, png).ConfigureAwait(false); }
            catch (HttpRequestException error) when (attempt < 3 && (error.StatusCode is null || IsTransient(error.StatusCode.Value)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _cancellation).ConfigureAwait(false);
            }
            catch (HttpRequestException) { return false; }
            catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
            {
                if (attempt >= 3) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _cancellation).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> DownloadAttemptAsync(string? url, string destination, bool png)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return false;
        var target = Path.GetFullPath(destination);
        // Artwork already selected by the user is never replaced by a background download.
        if (File.Exists(target)) return true;
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, _cancellation).ConfigureAwait(false);
            if (IsTransient(response.StatusCode)) throw new HttpRequestException("Artwork source is temporarily unavailable.", null, response.StatusCode);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is <= 0 or > MaximumBytes) return false;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true || mediaType?.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) == true) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = await response.Content.ReadAsStreamAsync(_cancellation).ConfigureAwait(false))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, _cancellation).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > MaximumBytes) return false;
                    await output.WriteAsync(buffer.AsMemory(0, count), _cancellation).ConfigureAwait(false);
                }
                if (received == 0 || response.Content.Headers.ContentLength is { } expected && received != expected) return false;
                await output.FlushAsync(_cancellation).ConfigureAwait(false);
            }
            if (png && !ValidPng(await File.ReadAllBytesAsync(staging, _cancellation).ConfigureAwait(false))) return false;
            _cancellation.ThrowIfCancellationRequested();
            File.Move(staging, target, overwrite: false);
            return true;
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool ValidPng(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 45 || !bytes[..8].SequenceEqual(PngSignature)) return false;
        var offset = 8;
        var imageHeader = false;
        var imageData = false;
        while (bytes.Length - offset >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > int.MaxValue || length > bytes.Length - offset - 12) return false;
            var payload = bytes.Slice(offset + 4, checked((int)length + 4));
            var type = payload[..4];
            if (BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset + 8 + (int)length, 4)) != Crc32(payload)) return false;
            if (!imageHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return false;
                var header = payload[4..];
                if (BinaryPrimitives.ReadUInt32BigEndian(header[..4]) == 0 || BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4)) == 0 || header[10] != 0 || header[11] != 0 || header[12] > 1) return false;
                imageHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8)) return false;
            if (type.SequenceEqual("IDAT"u8)) imageData = true;
            offset += (int)length + 12;
            if (type.SequenceEqual("IEND"u8)) return length == 0 && imageData && offset == bytes.Length;
        }
        return false;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        }
        return ~crc;
    }
}
