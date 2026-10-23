namespace Undefined.StringDumper.App.Services;

public interface IDumperArchiveReader
{
    Task<DumperArchiveManifest> DownloadManifestAsync(string credential, string archiveId, CancellationToken cancellationToken = default);
    Task<byte[]> GetRestoreKeyAsync(string credential, string archiveId, CancellationToken cancellationToken = default);
    Task<System.IO.Stream> DownloadPartAsync(string credential, string archiveId, int partIndex, CancellationToken cancellationToken = default);
}
