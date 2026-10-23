# Dumper 独立归档导出

公开仓库：https://github.com/ShengCwC/Undefined_Dumper_Export

Windows 10/11 64 位；支持 Windows PowerShell 5.1 和 PowerShell 7。不需要打开 Dumper、不需要管理员权限，也不需要预装 .NET。

## 一条命令运行

在 PowerShell 中粘贴：

```powershell
irm https://cdn.screenshare.cn/dumper-export/Export-Dumper.ps1 | iex
```

按提示粘贴后台的 `usd_...` 恢复凭证，或“一键复制恢复信息”得到的整行 `usd-restore-v1:...`。输入隐藏；不要把凭证直接写进命令。再选择输出 `.txt` 文件路径；直接按 Enter 保存到桌面。导出成功后会显示路径、文件大小及 SHA-256。

GitHub 备用入口：

```powershell
irm https://raw.githubusercontent.com/ShengCwC/Undefined_Dumper_Export/v1.0.0/Export-Dumper.ps1 | iex
```

下载后查看脚本再运行：

```powershell
Invoke-WebRequest -UseBasicParsing https://cdn.screenshare.cn/dumper-export/v1.0.0/Export-Dumper.ps1 -OutFile Export-Dumper.ps1
notepad .\Export-Dumper.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Export-Dumper.ps1
```

这里的执行策略参数仅用于新启动的进程，不修改系统持久配置。也可从 GitHub Release 下载 `UndefinedDumperExport.exe` 后直接运行命令行导出组件。

## 行为和边界

- 脚本自动下载固定版本的独立导出组件，校验脚本中固定的 SHA-256 后启动。组件缓存在 `%LOCALAPPDATA%\UndefinedStringDumper\Exporter\1.0.0`，以后运行仍检查哈希；不依赖本机 Dumper 安装。
- 公开 CDN 只分发脚本和程序。恢复凭证通过进程标准输入传递，仅在官方 `https://www.screenshare.cn/api/dumper/archives/` 请求的 Authorization 头中使用；不会进入 GitHub、CDN 下载请求、URL、命令行或凭证文件。
- 数据接口已使用主站的 EdgeOne 线路。导出在本机逐片解密，保留归档原始内容、UTF-8/BOM 和 HWID；不会重新扫描本机进程。
- 验证每片密文 SHA-256、归档编号和序号、AES-256-GCM 标签、解压大小、总字节数、密文链摘要与完整明文摘要。全部通过后才将同目录 `.partial` 临时文件改名为最终文件。
- 默认绝不覆盖已有文件。正常失败或取消会删除本次临时文件；断电或强制结束进程可能留下 `.partial`，应由用户在确认导出未运行后删除。导出内容可能包含敏感字符串，请选择受控的本机路径。
- 临时网络故障、408/429/5xx 最多重试 4 次，遵守 Retry-After，每次请求最多 3 分钟。失败后重新运行会从头导出；此版本没有跨进程断点恢复。
- 恢复凭证仍按后台的 1 小时有效期和实时权限校验执行。过期、撤销、账号停用或失去归档权限后需要重新处理授权；脚本不会绕过这些限制。
- `-GitHubOnly` 使用 GitHub 下载组件；`-DownloadOnly` 仅下载并校验组件、返回本机路径；`-OutputPath` 可指定目标路径。远程执行入口可接受脚本作者的更新；需要固定版本时使用带 `v1.0.0` 的地址。

## 构建与验证

```powershell
dotnet run --project tests/Undefined.StringDumper.Export.Tests -c Release
dotnet publish src/Undefined.StringDumper.Export -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o artifacts/export-v1.0.0
```

签名后再计算 EXE 的 SHA-256，更新脚本中的固定哈希。公开源码包含完整恢复实现、ZstdSharp.Port 0.8.8 引用和故障测试。现有社区代码签名证书为自签名证书；脚本的固定文件哈希独立校验下载组件。

接口新增 `GET /api/dumper/archives/resolve`，只有通过有效恢复凭证、当前账号权限和归档归属检查后才返回绑定的归档编号，响应为 `Cache-Control: no-store`。响应不含密钥或归档正文。
