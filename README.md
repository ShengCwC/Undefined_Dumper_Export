# Dumper 独立归档导出 · v1.1.0

公开仓库：https://github.com/ShengCwC/Undefined_Dumper_Export

无需打开 Dumper，无需管理员权限，无需安装运行库。支持 Windows PowerShell 5.1 和 PowerShell 7。

## 一条命令运行

在 PowerShell 中粘贴：

```powershell
irm https://cdn.screenshare.cn/dumper-export/Export-Dumper.ps1 | iex
```

输入后台的 `usd_...` 恢复凭证，或“一键复制恢复信息”得到的整行 `usd-restore-v1:...`。输入隐藏，不要把凭证写进命令。再输入一个已有文件夹中的 `.txt` 路径；直接按 Enter 保存到桌面。看到 `Saved` 和 SHA-256 才表示完整导出成功。

固定版本入口：

```powershell
irm https://cdn.screenshare.cn/dumper-export/v1.1.0/Export-Dumper.ps1 | iex
```

GitHub 备用入口：

```powershell
irm https://raw.githubusercontent.com/ShengCwC/Undefined_Dumper_Export/v1.1.0/Export-Dumper.ps1 | iex
```

如已自行保存脚本，可使用 `-OutputPath 'D:\Exports\archive.txt'` 指定输出文件。

## 本机会保存什么

v1.1.0 不下载、缓存或启动额外 EXE/DLL，不安装模块，不生成密钥、凭证、压缩包、分片或 `.partial` 文件。上述远程入口把脚本文本取到内存执行；脚本主动写入磁盘的只有目标 TXT。旧版已经存在的组件缓存不会被使用，也不会自动删除。

“不保存额外文件”不等于“不发生下载”：脚本文本和导出数据仍通过 HTTPS 传到本机。PowerShell/Windows 自身的日志、分页文件及组织审计策略不受脚本控制，因此不承诺系统层面的绝对无磁盘痕迹。

脚本绝不覆盖已有文件。正常失败或取消时删除本次未完成的 TXT；断电或强制终止可能留下不完整 TXT，必须以成功提示和最终校验为准。导出中途失败后重新运行从头开始，无跨进程断点续传。

## 原理、凭证和完整性

1. 用恢复凭证向官方服务查询绑定的归档；整行恢复信息中的编号不覆盖服务端绑定关系。
2. 服务端从现有存储逐片读取加密归档，在内存中完成 SHA-256、AES-256-GCM 验证和 Zstandard 解压；每片最多 16 MiB。
3. 脚本用 Windows 自带网络和 SHA-256 功能接收明文分片，校验每片长度/摘要，再写入同一个 TXT；结束时验证总长度和完整文件 SHA-256。保留原始字节、BOM、换行和 HWID。

解密现在发生在服务端内存，明文通过 HTTPS 返回本机。每次请求仍检查临时恢复凭证、有效期、撤销状态、账号状态、当前权限和归档归属。凭证只放在 `https://www.screenshare.cn/api/dumper/archives/` 的 Authorization 请求头中；不进入 URL、命令行、文件、GitHub 或公共 CDN 请求。数据接口禁止缓存，拒绝重定向，不重新扫描本机进程。

连接失败、408/429/5xx 最多尝试 4 次，尊重有上限的 Retry-After。请求有连接/读取超时和体积上限。归档密文链、分片身份和认证标签由服务端验证；完整明文摘要仍由本机独立验证。

## CDN 与版本

所有资源保留在 `cdn.screenshare.cn/dumper-export/`，域名根路径不绑定或跳转到此脚本。新版发布只更新无版本入口并增加 `v1.1.0/`，旧版本资源保持原样。

v1.1.0 Release 仅包含脚本与 `SHA256SUMS`，不再提供或依赖辅助程序。`-GitHubOnly`、`-DownloadOnly` 是 v1.0.x 的组件下载参数，新版已移除。仓库中的 C# 源码、旧 Release 和对应测试保留为旧版实现参考，新入口不会调用它们。

远程无版本入口会接受后续代码更新；希望固定代码时使用版本地址，并可对照 Release 的 SHA-256 审阅脚本。

## 验证

公开仓库运行：

```powershell
node tests/export-script.test.mjs ./Export-Dumper.ps1
```

测试在 Windows PowerShell 5.1 和 PowerShell 7 中各覆盖正常导出、整行恢复信息、空归档、重试、拒绝授权、拒绝重定向、损坏/截断/超长响应、撤销凭证、已有文件保护与只产生 TXT。测试使用本机合成数据，保留的测试结果不含凭证。

协议：`GET /api/dumper/archives/resolve`，随后 `GET /api/dumper/archives/{id}/export` 获取 v2 清单，`GET /api/dumper/archives/{id}/export/parts/{index}` 获取已验证的明文分片。所有请求都要求有效恢复凭证；不向脚本返回解密密钥。
