#requires -version 5.1
<#
.SYNOPSIS
Dumper Export 1.1.0: saves only the exported TXT, using built-in Windows APIs.
.DESCRIPTION
The service decrypts and decompresses bounded archive parts. No EXE, DLL,
module, encrypted chunk or credential file is downloaded to disk.
#>
[CmdletBinding()]
param([string]$OutputPath)

& {
    param($ChosenPath)
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version 2
    $api = 'https://www.screenshare.cn/api/dumper/archives'
    $credential = $null; $secret = $null; $output = $null
    $ownedPath = $null; $complete = $false; $hash = $null
    $oldTls = [Net.ServicePointManager]::SecurityProtocol

    function Get-Hex([byte[]]$Bytes) { [BitConverter]::ToString($Bytes).Replace('-', '').ToLowerInvariant() }
    function Get-BytesHash([byte[]]$Bytes) {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { Get-Hex ($algorithm.ComputeHash($Bytes)) } finally { $algorithm.Dispose() }
    }
    function Get-ArchiveData([string]$Suffix, [int]$Limit) {
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            $response = $null; $stream = $null; $memory = $null; $request = $null
            $retry = $false; $delay = [int][Math]::Pow(2, $attempt)
            try {
                $request = [Net.WebRequest]::Create($api + $Suffix)
                $request.Method = 'GET'
                $request.AllowAutoRedirect = $false
                $request.Timeout = 180000
                $request.ReadWriteTimeout = 30000
                $request.Headers['Authorization'] = 'Bearer ' + $credential
                $request.Headers['Cache-Control'] = 'no-store'
                $request.UserAgent = 'UndefinedDumperExport/1.1.0'
                $watch = [Diagnostics.Stopwatch]::StartNew()
                $response = $request.GetResponse()
                if ([int]$response.StatusCode -ne 200) { throw 'Unexpected response or redirect; export stopped.' }
                if ($response.ContentLength -gt $Limit) { throw 'Response exceeds the allowed size.' }
                $stream = $response.GetResponseStream()
                $memory = New-Object IO.MemoryStream
                $buffer = New-Object byte[] 65536
                while (($count = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($memory.Length + $count -gt $Limit) { throw 'Response exceeds the allowed size.' }
                    if ($watch.Elapsed.TotalSeconds -gt 180) { throw [IO.IOException]::new('Request timed out.') }
                    $memory.Write($buffer, 0, $count)
                }
                if ($response.ContentLength -ge 0 -and $memory.Length -ne $response.ContentLength) { throw [IO.IOException]::new('Response ended early.') }
                return @{ Bytes = $memory.ToArray(); Hash = $response.Headers['X-USD-Plain-SHA256']; Index = $response.Headers['X-USD-Part-Index'] }
            } catch [Net.WebException] {
                $failure = $_.Exception.Response
                if ($null -ne $failure) {
                    try {
                        $status = [int]$failure.StatusCode
                        $retry = $status -eq 408 -or $status -eq 429 -or $status -ge 500
                        $retryAfter = $failure.Headers['Retry-After']
                        $seconds = 0; $date = [DateTimeOffset]::MinValue
                        if ([int]::TryParse($retryAfter, [ref]$seconds)) { $delay = [Math]::Min(300, [Math]::Max($delay, $seconds)) }
                        elseif ([DateTimeOffset]::TryParse($retryAfter, [ref]$date)) { $delay = [Math]::Min(300, [Math]::Max($delay, [int][Math]::Ceiling(($date - [DateTimeOffset]::UtcNow).TotalSeconds))) }
                    } finally { $failure.Close() }
                    if (-not $retry) { throw "Export denied (HTTP $status). Check the restore credential and your archive permissions." }
                } else { $retry = $true }
            } catch [IO.IOException] { $retry = $true }
            finally {
                if ($null -ne $stream) { $stream.Dispose() }
                if ($null -ne $memory) { $memory.Dispose() }
                if ($null -ne $response) { $response.Close() }
                if ($null -ne $request) { $request.Headers.Remove('Authorization'); $request.Abort() }
            }
            if ($retry -and $attempt -lt 3) {
                Write-Host 'Connection interrupted or service busy. Retrying...'
                Start-Sleep -Seconds $delay
            }
        }
        throw 'Export request failed after four attempts. No completed export was saved.'
    }
    function Get-ArchiveJson([string]$Suffix, [int]$Limit) {
        $result = Get-ArchiveData $Suffix $Limit
        [Text.Encoding]::UTF8.GetString($result.Bytes) | ConvertFrom-Json
    }
    function Test-Integer($Value, [long]$Minimum, [long]$Maximum) {
        return $Value -is [ValueType] -and $Value -ge $Minimum -and $Value -le $Maximum -and [Math]::Truncate([double]$Value) -eq $Value
    }
    try {
        [Net.ServicePointManager]::SecurityProtocol = $oldTls -bor [Net.SecurityProtocolType]::Tls12
        Write-Host 'Dumper Export 1.1.0 - no additional programs required.'
        Write-Host 'Paste the restore credential or the complete recovery information.'
        $secret = Read-Host 'Restore credential (hidden)' -AsSecureString
        $pointer = [IntPtr]::Zero
        try {
            $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
            $credential = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer).Trim()
        } finally { if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) } }
        if ($credential -cmatch '^usd-restore-v1:[0-9a-fA-F-]{36}:(usd_[A-Za-z0-9_-]{43})$') { $credential = $Matches[1]; $Matches.Clear() }
        if ($credential -cnotmatch '^usd_[A-Za-z0-9_-]{43}$') { throw 'Invalid restore credential format.' }
        $resolved = Get-ArchiveJson '/resolve' 4096
        if (-not $resolved.ok -or $resolved.archiveId -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$') { throw 'Invalid archive resolution response.' }
        $id = $resolved.archiveId
        $manifest = Get-ArchiveJson "/$id/export" (16 * 1024 * 1024)
        if (-not $manifest.ok -or $manifest.version -ne 2 -or $manifest.archiveId -ne $id -or
            -not (Test-Integer $manifest.partCount 1 100000) -or @($manifest.parts).Count -ne $manifest.partCount -or
            -not (Test-Integer $manifest.totalPlainBytes 0 1677721600000) -or $manifest.plaintextSha256 -notmatch '^[0-9a-f]{64}$') { throw 'Invalid export manifest.' }
        [long]$expectedSize = 0
        for ($i = 0; $i -lt $manifest.partCount; $i++) {
            $part = $manifest.parts[$i]
            if ($part.index -ne $i -or -not (Test-Integer $part.plainBytes 0 16777216)) { throw 'Invalid export part metadata.' }
            $expectedSize += $part.plainBytes
        }
        if ($expectedSize -ne $manifest.totalPlainBytes) { throw 'Export size does not match the manifest.' }
        if ([string]::IsNullOrWhiteSpace($ChosenPath)) { $ChosenPath = Read-Host 'Output .txt path (Enter = Desktop)' }
        if ([string]::IsNullOrWhiteSpace($ChosenPath)) { $ChosenPath = Join-Path ([Environment]::GetFolderPath('Desktop')) ("Dumper-$id-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt') }
        if ($ChosenPath -match "[`r`n]" -or [IO.Path]::GetExtension($ChosenPath) -ine '.txt') { throw 'Choose a single .txt file path.' }
        $finalPath = [IO.Path]::GetFullPath($ChosenPath)
        if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($finalPath))) { throw 'The output folder must already exist.' }
        # CreateNew prevents overwrites and races. No .partial, archive or cache files.
        $output = [IO.File]::Open($finalPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $ownedPath = $finalPath
        $hash = [Security.Cryptography.SHA256]::Create()
        [long]$written = 0
        for ($i = 0; $i -lt $manifest.partCount; $i++) {
            $part = $manifest.parts[$i]
            $received = Get-ArchiveData "/$id/export/parts/$i" ([int]$part.plainBytes)
            [byte[]]$bytes = $received.Bytes
            if ($bytes.Length -ne $part.plainBytes -or $received.Index -cne [string]$i -or
                $received.Hash -notmatch '^[0-9a-f]{64}$' -or (Get-BytesHash $bytes) -ine $received.Hash) { throw 'Export part integrity verification failed.' }
            $null = $hash.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
            $output.Write($bytes, 0, $bytes.Length)
            $written += $bytes.Length
            [Array]::Clear($bytes, 0, $bytes.Length)
            Write-Host ("Verified part {0}/{1}" -f ($i + 1), $manifest.partCount)
        }
        $null = $hash.TransformFinalBlock((New-Object byte[] 0), 0, 0)
        $actualHash = Get-Hex $hash.Hash
        if ($written -ne $expectedSize -or $actualHash -ine $manifest.plaintextSha256) { throw 'Complete export integrity verification failed.' }
        $output.Flush($true)
        $output.Dispose(); $output = $null
        $complete = $true
        Write-Host "Saved: $finalPath"
        Write-Host "Bytes: $written"
        Write-Host "SHA-256: $actualHash"
    } finally {
        $credential = $null
        if ($null -ne $secret) { $secret.Dispose() }
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $hash) { $hash.Dispose() }
        if (-not $complete -and $null -ne $ownedPath) { [IO.File]::Delete($ownedPath) }
        [Net.ServicePointManager]::SecurityProtocol = $oldTls
    }
} $OutputPath
