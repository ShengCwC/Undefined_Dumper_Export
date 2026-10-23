using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Undefined.StringDumper.App.Services;

namespace Undefined.StringDumper.Export;

// Read-only client: credentials are sent only to the existing official archive API.
internal sealed class ArchiveReader : IDumperArchiveReader, IDisposable
{
    internal const string Origin = "https://www.screenshare.cn/";
    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ArchiveReader() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    internal ArchiveReader(HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = new HttpClient(handler) { BaseAddress = new Uri(Origin), Timeout = Timeout.InfiniteTimeSpan };
        this.delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    internal async Task<string> ResolveAsync(string credential, string? archiveId, CancellationToken token)
    {
        // Bundle IDs are hints; the server resolves the token's current authorized scope.
        _ = archiveId;
        var bytes = await GetAsync(credential, "api/dumper/archives/resolve", 64 * 1024, token);
        using var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
            !document.RootElement.TryGetProperty("archiveId", out var id) || !Guid.TryParse(id.GetString(), out var parsed))
            throw new InvalidDataException("服务器未返回有效归档编号。请更新脚本后重试。");
        return parsed.ToString("D");
    }

    public async Task<DumperArchiveManifest> DownloadManifestAsync(string credential, string archiveId, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(credential, PathFor(archiveId) + "/manifest", 16 * 1024 * 1024, cancellationToken);
        var manifest = JsonSerializer.Deserialize(bytes, ArchiveJsonContext.Default.DumperArchiveManifest);
        if (manifest is null || manifest.Kind != "undefined-string-dumper-archive" || manifest.Version != 1)
            throw new InvalidDataException("归档清单格式无效。");
        return manifest;
    }

    public async Task<byte[]> GetRestoreKeyAsync(string credential, string archiveId, CancellationToken cancellationToken = default)
    {
        var bytes = await GetAsync(credential, PathFor(archiveId) + "/key", 64 * 1024, cancellationToken);
        try
        {
            var envelope = JsonSerializer.Deserialize(bytes, ArchiveJsonContext.Default.DumperArchiveEnvelope);
            if (envelope?.Ok != true) throw new InvalidDataException("服务器未提供恢复密钥。");
            var normalized = envelope.DataKey.Replace('-', '+').Replace('_', '/');
            var key = Convert.FromBase64String(normalized.PadRight((normalized.Length + 3) / 4 * 4, '='));
            if (key.Length == 32) return key;
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("恢复密钥长度无效。");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    public async Task<Stream> DownloadPartAsync(string credential, string archiveId, int partIndex, CancellationToken cancellationToken = default) =>
        new MemoryStream(await GetAsync(credential, PathFor(archiveId) + $"/parts/{partIndex}", 17 * 1024 * 1024, cancellationToken), writable: false);

    private static string PathFor(string archiveId) => $"api/dumper/archives/{Guid.Parse(archiveId):D}";

    private async Task<byte[]> GetAsync(string credential, string path, int limit, CancellationToken cancellationToken)
    {
        if (credential.Length != 47 || !credential.StartsWith("usd_", StringComparison.Ordinal) ||
            credential[4..].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
            throw new ArgumentException("请输入有效的 Dumper 恢复凭证或完整恢复信息。");
        for (var attempt = 0; ; attempt++)
        {
            var pause = TimeSpan.FromSeconds(Math.Min(30, 2 << attempt));
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
                request.Headers.UserAgent.ParseAdd("UndefinedDumperExport/1.0.0");
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                    throw new InvalidDataException("归档服务发生跳转，已停止发送凭证，请更新脚本。");
                if (!response.IsSuccessStatusCode)
                {
                    var retry = response.Headers.RetryAfter;
                    var retryWait = retry?.Delta ?? (retry?.Date - DateTimeOffset.UtcNow);
                    if (retryWait.HasValue) pause = TimeSpan.FromSeconds(Math.Clamp(retryWait.Value.TotalSeconds, 1, 300));
                    if (attempt < 4 && (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                        throw new RetryableResponseException();
                    var errorBytes = await ReadBoundedAsync(response.Content, 64 * 1024, deadline.Token);
                    DumperArchiveEnvelope? error = null;
                    try { error = JsonSerializer.Deserialize(errorBytes, ArchiveJsonContext.Default.DumperArchiveEnvelope); } catch (JsonException) { }
                    throw new ArchiveApiException(response.StatusCode, error?.Code ?? "HTTP_ERROR", error?.ExpectedArchiveId ?? "");
                }
                return await ReadBoundedAsync(response.Content, limit, deadline.Token);
            }
            catch (Exception ex) when (attempt < 4 && !cancellationToken.IsCancellationRequested &&
                ex is RetryableResponseException or HttpRequestException or IOException or OperationCanceledException && ex is not InvalidDataException)
            {
                Console.Error.WriteLine($"连接中断或服务繁忙，{pause.TotalSeconds:0} 秒后重试 ({attempt + 1}/4)…");
                await delay(pause, cancellationToken);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("服务器响应超出大小限制。");
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("服务器响应超出大小限制。");
            output.Write(buffer, 0, read);
        }
        if (content.Headers.ContentLength is long length && length != output.Length) throw new EndOfStreamException();
        return output.ToArray();
    }

    public void Dispose() => http.Dispose();
    private sealed class RetryableResponseException : Exception;
}

internal sealed class ArchiveApiException(HttpStatusCode status, string code, string expectedArchiveId)
    : Exception(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ? $"恢复凭证已过期、被撤销或无权限 (HTTP {(int)status})。请在后台重新生成恢复凭证。"
        : $"归档服务返回 HTTP {(int)status}。请确认归档已封存且恢复凭证有效。")
{
    internal string Code { get; } = code;
    internal string ExpectedArchiveId { get; } = expectedArchiveId;
}
