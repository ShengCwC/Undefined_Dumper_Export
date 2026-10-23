#requires -version 5.1
<#
.SYNOPSIS
Export an authorized Dumper cloud archive without opening the desktop application.
.DESCRIPTION
Downloads a fixed, SHA-256-pinned standalone helper. Credentials are prompted
securely and passed via stdin, never command-line arguments or a file.
#>
[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$GitHubOnly,
    [switch]$DownloadOnly
)

& {
    param($ChosenPath, $DirectGitHub, $OnlyDownload)
    $ErrorActionPreference = 'Stop'
    if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
        throw 'This exporter requires 64-bit Windows 10/11.'
    }
    $version = '1.0.0'
    $expectedHash = '8279c7af483c7e5ad9030573f161e7f512e1bf186ebad2238a71df806e58fa37'
    $fileName = 'UndefinedDumperExport.exe'
    $cdn = "https://cdn.screenshare.cn/dumper-export/v$version/$fileName"
    $github = "https://github.com/ShengCwC/Undefined_Dumper_Export/releases/download/v$version/$fileName"
    $cache = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "UndefinedStringDumper/Exporter/$version"
    $null = New-Item -ItemType Directory -Path $cache -Force
    $executable = Join-Path $cache $fileName
    $valid = (Test-Path -LiteralPath $executable -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -eq $expectedHash)
    $oldTls = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $oldTls -bor [Net.SecurityProtocolType]::Tls12
        if (-not $valid) {
            $sources = @($cdn, $github)
            if ($DirectGitHub) { $sources = @($github) }
            foreach ($source in $sources) {
                $temporary = Join-Path $cache (([Guid]::NewGuid().ToString('N')) + '.download')
                try {
                    Write-Host "Downloading Dumper Export $version..."
                    $oldProgress = $ProgressPreference
                    try {
                        $ProgressPreference = 'SilentlyContinue'
                        Invoke-WebRequest -UseBasicParsing -Uri $source -OutFile $temporary -TimeoutSec 180
                    } finally { $ProgressPreference = $oldProgress }
                    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $expectedHash) {
                        throw 'Downloaded file failed SHA-256 verification.'
                    }
                    Move-Item -LiteralPath $temporary -Destination $executable -Force
                    $valid = $true
                    break
                } catch {
                    Write-Warning 'Download failed or integrity check failed; trying the next official source.'
                } finally {
                    if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
                }
            }
        }
        if (-not $valid) { throw 'Cannot download the verified exporter. Check your connection and try again.' }
        if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'Cached exporter integrity check failed.'
        }
        if ($OnlyDownload) { Write-Output $executable; return }
        Write-Host 'Paste the restore credential or full recovery information from the dashboard.'
        $secret = Read-Host 'Restore credential (hidden)' -AsSecureString
        if ([string]::IsNullOrWhiteSpace($ChosenPath)) {
            $ChosenPath = Read-Host 'Output .txt path (Enter = Desktop)'
        }
        if ($ChosenPath -match "[`r`n]") { throw 'Output path must be a single line.' }
        $pointer = [IntPtr]::Zero
        $process = $null
        try {
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
            if ($plain -match "[`r`n]") { throw 'Paste the single-line recovery information.' }
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.FileName = $executable
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $start.StandardInputEncoding = New-Object Text.UTF8Encoding($false)
            $process = [Diagnostics.Process]::Start($start)
            $process.StandardInput.WriteLine($plain)
            $plain = $null
            $process.StandardInput.WriteLine($ChosenPath)
            $process.StandardInput.Close()
            while (-not $process.WaitForExit(200)) { }
            if ($process.ExitCode -ne 0) { throw "Export failed (exit code $($process.ExitCode)). See the message above." }
        } finally {
            $plain = $null
            if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
            if ($null -ne $secret) { $secret.Dispose() }
            if ($null -ne $process) { $process.Dispose() }
        }
    } finally { [Net.ServicePointManager]::SecurityProtocol = $oldTls }
} $OutputPath $GitHubOnly $DownloadOnly
