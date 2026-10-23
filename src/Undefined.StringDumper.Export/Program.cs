using System.Security.Cryptography;
using System.Text;
using Undefined.StringDumper.App.Services;

namespace Undefined.StringDumper.Export;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        if (args.Length == 1 && args[0] == "--version") { Console.WriteLine("UndefinedDumperExport 1.0.0"); return 0; }
        if (args.Length != 0) { Console.Error.WriteLine("无需命令行参数。直接运行并按提示输入，或通过标准输入依次传入恢复信息、输出路径。"); return 2; }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            Console.WriteLine("Undefined Dumper 归档导出 v1.0.0（无需管理员权限）");
            Console.WriteLine("输入后台的完整恢复信息，或仅输入 usd_ 开头的恢复凭证。");
            Console.Write("恢复凭证（输入隐藏）: ");
            var input = ReadSecret();
            var credential = input.Trim();
            string? requestedId = null;
            if (DumperRecoveryInput.TryParseBundle(input, out var parsedCredential, out var parsedId))
            { credential = parsedCredential; requestedId = parsedId; }
            using var reader = new ArchiveReader();
            var archiveId = await reader.ResolveAsync(credential, requestedId, stop.Token);
            Console.WriteLine($"归档编号: {archiveId}");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(desktop)) desktop = Environment.CurrentDirectory;
            var defaultPath = Path.Combine(desktop, $"USS_Restored_{Guid.Parse(archiveId):N}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.txt");
            Console.WriteLine($"默认保存到: {defaultPath}");
            Console.Write("输出文件路径（按 Enter 使用默认路径）: ");
            var chosen = Console.ReadLine()?.Trim().Trim('"');
            var destination = Path.GetFullPath(string.IsNullOrWhiteSpace(chosen) ? defaultPath : chosen);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("目标已存在。请换一个文件名；脚本不会覆盖已有文件。");
            await new DumperArchiveRestoreService(reader).RestoreAsync(credential, archiveId, destination,
                new ConsoleProgress(), stop.Token, overwrite: false);
            await using var file = File.OpenRead(destination);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, stop.Token)).ToLowerInvariant();
            Console.WriteLine($"导出成功: {destination}\n大小: {file.Length:N0} 字节\nSHA-256: {hash}");
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { Console.Error.WriteLine("已取消导出，未完成文件已清理。"); return 130; }
        catch (Exception ex) when (ex is ArchiveApiException or IOException or ArgumentException or CryptographicException or System.Text.Json.JsonException or FormatException)
        { Console.Error.WriteLine($"导出失败: {ex.Message}"); return 1; }
        catch (Exception)
        { Console.Error.WriteLine("导出失败。请检查网络和可用磁盘空间后重试；已有文件不会被覆盖。"); return 1; }
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString(); }
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar) && value.Length < 4096) value.Append(key.KeyChar);
        }
    }

    private sealed class ConsoleProgress : IProgress<DumperArchiveTransferProgress>
    {
        public void Report(DumperArchiveTransferProgress value) =>
            Console.WriteLine($"进度: {value.CompletedParts}/{value.TotalParts} 分片 ({value.Fraction:P0}) — {value.Detail}");
    }
}
