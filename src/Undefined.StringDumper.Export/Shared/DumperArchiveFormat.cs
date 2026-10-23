using System.Text;

namespace Undefined.StringDumper.App.Services;

internal static class DumperArchiveFormat
{
    internal static ReadOnlySpan<byte> PartMagic => "USDC0001"u8;
    internal static byte[] BuildAdditionalData(string archiveId, int partIndex, long plainBytes) =>
        Encoding.UTF8.GetBytes(FormattableString.Invariant($"usd-part:v1:{archiveId}:{partIndex}:{plainBytes}"));
}
