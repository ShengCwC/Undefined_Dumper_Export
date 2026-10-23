using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Undefined.StringDumper.App.Services;
using Undefined.StringDumper.Export;
using ZstdSharp;

var root = Path.Combine(Path.GetTempPath(), "usd-export-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    foreach (var mode in new[] { "success", "retry", "truncated-once", "cipher-hash", "gcm-tag", "plain-hash", "chain-hash", "part-order", "wrong-archive", "existing", "cancel", "unauthorized", "redirect", "oversize" })
    {
        using var fixture = new Fixture(mode);
        using var reader = new ArchiveReader(fixture, (_, _) => Task.CompletedTask);
        var target = Path.Combine(root, mode + ".txt");
        if (mode == "existing") await File.WriteAllTextAsync(target, "KEEP");
        using var cancel = new CancellationTokenSource();
        if (mode == "cancel") cancel.Cancel();
        Exception? failure = null;
        try
        {
            await new DumperArchiveRestoreService(reader).RestoreAsync(Fixture.Credential, fixture.Id, target, cancellationToken: cancel.Token, overwrite: false);
        }
        catch (Exception ex) { failure = ex; }
        if (mode is "success" or "retry" or "truncated-once")
        {
            if (failure is not null) throw new Exception(mode, failure);
            Check((await File.ReadAllBytesAsync(target)).SequenceEqual(fixture.Plaintext), "Exact recovered bytes, including BOM, Unicode and HWID");
            if (mode == "retry") Check(fixture.PartRequests == 3, "Retry the failed part only");
            if (mode == "truncated-once") Check(fixture.PartRequests == 3, "Retry interrupted response body");
        }
        else
        {
            Check(failure is not null, mode + " must fail closed");
            if (mode == "existing") Check(await File.ReadAllTextAsync(target) == "KEEP", "Existing file preserved");
            else Check(!File.Exists(target), "Failed output never promoted");
            if (mode is "unauthorized" or "redirect" or "oversize") Check(fixture.Requests == 1, "Permanent errors not retried");
        }
        Check(!Directory.EnumerateFiles(root, "*.partial").Any(), "Partial file cleaned");
        Console.WriteLine("PASS " + mode); passed++;
    }
    using (var fixture = new Fixture("success"))
    using (var reader = new ArchiveReader(fixture))
    {
        Check(await reader.ResolveAsync(Fixture.Credential, null, default) == fixture.Id, "Credential alone resolves ID");
        Check(await reader.ResolveAsync(Fixture.Credential, Guid.NewGuid().ToString(), default) == fixture.Id, "Wrong ID corrected by authorized response");
        Check(DumperRecoveryInput.TryParseBundle($"usd-restore-v1:{fixture.Id}:{Fixture.Credential}", out var token, out var id) && token == Fixture.Credential && id == fixture.Id, "Bundle parser");
        Check(!DumperRecoveryInput.TryParseBundle($"usd-restore-v1:{fixture.Id}:usd_invalid", out _, out _), "Invalid bundle rejected");
        try { await reader.ResolveAsync("usd_bad\r\nInjected", null, default); throw new Exception("Invalid token accepted"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS credential-only, wrong-ID correction, recovery bundle and invalid input"); passed += 4;
    }
    Console.WriteLine($"PASS {passed} export scenarios");
}
finally { Directory.Delete(root, recursive: true); }

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }

sealed class Fixture : HttpMessageHandler
{
    internal const string Credential = "usd_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal string Id { get; } = Guid.NewGuid().ToString("D");
    internal byte[] Plaintext { get; }
    internal int Requests { get; private set; }
    internal int PartRequests { get; private set; }
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    private readonly List<byte[]> parts = [];
    private readonly JsonObject manifest;
    private readonly string mode;

    internal Fixture(string mode)
    {
        this.mode = mode;
        byte[][] plainParts = [Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("HWID: USS1-TEST\r\n0x1000 (8): 测试 archive\r\n")).ToArray(), Encoding.UTF8.GetBytes(new string('a', 20000) + "\r\n0x2000: second part\r\n")];
        Plaintext = plainParts.SelectMany(x => x).ToArray();
        var entries = new JsonArray();
        using var chain = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var index = 0; index < plainParts.Length; index++)
        {
            var plain = plainParts[index];
            using var compressor = new Compressor(3);
            var compressed = compressor.Wrap(plain).ToArray();
            var bytes = new byte[72 + compressed.Length];
            "USDC0001"u8.CopyTo(bytes);
            Guid.Parse(Id).ToByteArray().CopyTo(bytes, 8);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), index);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(28), plain.Length);
            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(36), compressed.Length);
            RandomNumberGenerator.Fill(bytes.AsSpan(44, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(bytes.AsSpan(44, 12), compressed, bytes.AsSpan(72), bytes.AsSpan(56, 16), Encoding.UTF8.GetBytes($"usd-part:v1:{Id}:{index}:{plain.Length}"));
            if (mode == "gcm-tag" && index == 1) bytes[56] ^= 1;
            var digest = SHA256.HashData(bytes);
            chain.AppendData(digest);
            entries.Add(new JsonObject { ["index"] = index, ["plainBytes"] = plain.Length, ["cipherBytes"] = bytes.Length, ["ciphertextSha256"] = Convert.ToHexString(digest), ["fileName"] = $"{index}.usdc" });
            if (mode == "cipher-hash" && index == 1) bytes[^1] ^= 1;
            parts.Add(bytes);
        }
        manifest = new JsonObject
        {
            ["kind"] = "undefined-string-dumper-archive", ["version"] = 1, ["archiveId"] = Id, ["partSizeBytes"] = 1024 * 1024,
            ["partCount"] = 2, ["totalPlainBytes"] = Plaintext.Length, ["totalCipherBytes"] = parts.Sum(p => p.Length),
            ["plaintextSha256"] = Convert.ToHexString(SHA256.HashData(Plaintext)), ["ciphertextSha256"] = Convert.ToHexString(chain.GetHashAndReset()), ["parts"] = entries
        };
        if (mode == "plain-hash") manifest["plaintextSha256"] = new string('0', 64);
        if (mode == "chain-hash") manifest["ciphertextSha256"] = new string('0', 64);
        if (mode == "part-order") entries[1]!["index"] = 0;
        if (mode == "wrong-archive") manifest["archiveId"] = Guid.NewGuid().ToString();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Requests++;
        if (request.Method != HttpMethod.Get || request.RequestUri!.GetLeftPart(UriPartial.Authority) != "https://www.screenshare.cn" || request.Headers.Authorization?.Parameter != Credential)
            throw new Exception("Wrong auth, destination or method");
        var path = request.RequestUri.AbsolutePath;
        HttpResponseMessage response;
        if (mode == "unauthorized") response = Json(new { code = "DUMPER_TOKEN_INVALID" }, HttpStatusCode.Unauthorized);
        else if (mode == "redirect") { response = new(HttpStatusCode.Found); response.Headers.Location = new Uri("https://example.com/"); }
        else if (mode == "oversize") { response = new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) }; response.Content.Headers.ContentLength = 32 * 1024 * 1024; }
        else if (path == "/api/dumper/archives/resolve") response = Json(new { ok = true, archiveId = Id });
        else if (!path.StartsWith($"/api/dumper/archives/{Id}/", StringComparison.Ordinal)) response = Json(new { code = "DUMPER_TOKEN_ARCHIVE_MISMATCH", expectedArchiveId = Id }, HttpStatusCode.Forbidden);
        else if (path.EndsWith("/manifest")) response = new(HttpStatusCode.OK) { Content = new StringContent(manifest.ToJsonString(), Encoding.UTF8, "application/json") };
        else if (path.EndsWith("/key")) response = Json(new { ok = true, dataKey = Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_') });
        else
        {
            PartRequests++;
            var bytes = parts[int.Parse(path.Split('/')[^1])];
            if (mode == "retry" && PartRequests == 1) response = new(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) } };
            else if (mode == "truncated-once" && PartRequests == 1)
            { response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes[..^1]) }; response.Content.Headers.ContentLength = bytes.Length; }
            else response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        return Task.FromResult(response);
    }

    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
}
