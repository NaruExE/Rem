@echo off
setlocal
title REM Safe Updater
set "REM_SCRIPT_DIR=%~dp0"
set "REM_SELF=%~f0"

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$c=[IO.File]::ReadAllText($env:REM_SELF,[Text.Encoding]::UTF8); $m='#__REM_PS_BEGIN__'; $i=$c.LastIndexOf($m); if($i -lt 0){Write-Error 'Corrupt updater script'; exit 1}; & ([scriptblock]::Create($c.Substring($i+$m.Length))) $env:REM_SCRIPT_DIR"
set "EXIT_CODE=%errorlevel%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Update failed ^(exit code %EXIT_CODE%^). Press any key to exit...
    pause >nul
    exit /b %EXIT_CODE%
)

echo Press any key to close this window...
pause >nul
exit /b 0

#__REM_PS_BEGIN__
param([string]$ScriptDir)

$ErrorActionPreference = 'Stop'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}
catch {
}

Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoOwner = 'NaruExE'
$RepoName = 'Rem'
$LatestApi = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
$UserDataDir = Join-Path $env:LOCALAPPDATA 'REM'
$UpdateRoot = Join-Path $UserDataDir 'Updates'
$StagingDir = Join-Path $UpdateRoot ("cmd-stage-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$BackupDir = Join-Path $UpdateRoot ("cmd-backup-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))

function Format-Bytes {
    param([int64]$Bytes)
    if ($Bytes -ge 1048576) { return ('{0:F2} MB' -f ($Bytes / 1048576.0)) }
    if ($Bytes -ge 1024) { return ('{0:F1} KB' -f ($Bytes / 1024.0)) }
    return "$Bytes B"
}

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($FilePath)
        try {
            $hash = $sha.ComputeHash($fs)
            return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
        }
        finally { $fs.Dispose() }
    }
    finally { $sha.Dispose() }
}

function Test-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RootDir,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )
    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return $null }
    $normalized = ($RelativePath -replace '/', '\').TrimStart('\')
    if ([System.IO.Path]::IsPathRooted($normalized) -or $normalized.Contains('..')) { return $null }
    $rootFull = [System.IO.Path]::GetFullPath($RootDir).TrimEnd('\') + '\'
    $targetFull = [System.IO.Path]::GetFullPath((Join-Path $rootFull $normalized))
    if (-not $targetFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $targetFull
}

function Copy-WithRetry {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Dest
    )
    $parent = Split-Path -Path $Dest -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            [System.IO.File]::Copy($Source, $Dest, $true)
            return
        }
        catch {
            if ($attempt -eq 10) { throw }
            Start-Sleep -Milliseconds 400
        }
    }
}

Write-Host '======================================================' -ForegroundColor Cyan
Write-Host '  REM Safe Updater (GitHub Delta / Backup + Rollback)' -ForegroundColor Cyan
Write-Host '======================================================' -ForegroundColor Cyan
Write-Host ''

# 1. Locate REM installation directory
$installDir = $null
if (-not [string]::IsNullOrWhiteSpace($ScriptDir)) {
    $cleanScriptDir = $ScriptDir.TrimEnd('\', '/')
    if (Test-Path -LiteralPath (Join-Path $cleanScriptDir 'REM.exe') -PathType Leaf) {
        $installDir = [System.IO.Path]::GetFullPath($cleanScriptDir)
    }
}

if (-not $installDir) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\REM'),
        (Join-Path $env:LOCALAPPDATA 'REM-App'),
        (Join-Path $env:USERPROFILE 'Desktop\REM'),
        (Join-Path $env:USERPROFILE 'Downloads\REM'),
        'C:\REM'
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $c 'REM.exe') -PathType Leaf) {
            Write-Host "Found REM installation at: $c" -ForegroundColor Yellow
            $ans = Read-Host 'Update this folder? (Y/n)'
            if ([string]::IsNullOrWhiteSpace($ans) -or $ans -match '^[Yy]') {
                $installDir = [System.IO.Path]::GetFullPath($c)
                break
            }
        }
    }
}

while (-not $installDir) {
    $inputPath = Read-Host 'Paste the full folder path that contains REM.exe'
    $inputPath = $inputPath.Trim().Trim('"').TrimEnd('\', '/')
    if (-not [string]::IsNullOrWhiteSpace($inputPath) -and (Test-Path -LiteralPath (Join-Path $inputPath 'REM.exe') -PathType Leaf)) {
        $installDir = [System.IO.Path]::GetFullPath($inputPath)
    }
    else {
        Write-Host 'Could not find REM.exe in that folder. Please try again.' -ForegroundColor Red
    }
}

# Check for dev build output folder
$possibleCsproj = [System.IO.Path]::GetFullPath((Join-Path $installDir '..\..\..\REM.csproj'))
if (Test-Path -LiteralPath $possibleCsproj -PathType Leaf) {
    Write-Host "WARNING: '$installDir' looks like a local source build folder (REM.csproj detected)." -ForegroundColor Yellow
    $confirmDev = Read-Host 'Continue updating this folder anyway? (y/N)'
    if ($confirmDev -notmatch '^[Yy]') {
        Write-Host 'Cancelled by user.'
        exit 0
    }
}

# Check write permissions
$probeFile = Join-Path $installDir ('.rem-write-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    [System.IO.File]::WriteAllText($probeFile, 'ok')
    [System.IO.File]::Delete($probeFile)
}
catch {
    Write-Host "Cannot write to '$installDir'. Please right-click Update-REM.cmd and choose 'Run as administrator'." -ForegroundColor Red
    exit 1
}

$exePath = Join-Path $installDir 'REM.exe'
$localVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath).FileVersion
Write-Host "Target folder  : $installDir"
Write-Host "Current REM    : v$localVersion"
Write-Host "User data safe : $UserDataDir (untouched)"
Write-Host ''

# 2. Fetch latest release info from GitHub
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $true
$http = New-Object System.Net.Http.HttpClient($handler)
$http.Timeout = [TimeSpan]::FromMinutes(15)
$http.DefaultRequestHeaders.UserAgent.ParseAdd('REM-SafeUpdater/1.0')
$http.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')

try {
    Write-Host 'Checking GitHub releases...' -ForegroundColor Gray
    $relResp = $http.GetAsync($LatestApi).GetAwaiter().GetResult()
    if (-not $relResp.IsSuccessStatusCode) {
        $statusCode = [int]$relResp.StatusCode
        if ($statusCode -eq 403 -or $statusCode -eq 429) {
            throw "GitHub API rate limit reached (HTTP $statusCode). Please wait a few minutes and try again."
        }
        throw "GitHub API returned HTTP $statusCode."
    }

    $relJson = $relResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $release = $relJson | ConvertFrom-Json
    $tagName = [string]$release.tag_name
    $remoteVersion = $tagName.TrimStart('v', 'V')

    $zipAsset = $release.assets | Where-Object { $_.name -eq 'REM-win-x64.zip' } | Select-Object -First 1
    if (-not $zipAsset) {
        $zipAsset = $release.assets | Where-Object { $_.name -like '*.zip' } | Select-Object -First 1
    }
    if (-not $zipAsset) {
        throw "Release $tagName does not contain REM-win-x64.zip."
    }

    $manifestAsset = $release.assets | Where-Object { $_.name -eq 'update-manifest.json' } | Select-Object -First 1
    $manifest = $null
    if ($manifestAsset) {
        Write-Host "Downloading update manifest for $tagName..." -ForegroundColor Gray
        $manText = $http.GetStringAsync([string]$manifestAsset.browser_download_url).GetAwaiter().GetResult()
        $manifest = $manText | ConvertFrom-Json
    }

    Write-Host "Latest release : $tagName"
    Write-Host ''

    # 3. Compute delta if manifest is available
    $filesToDownload = New-Object 'System.Collections.Generic.List[object]'
    $filesToRemove = New-Object 'System.Collections.Generic.List[string]'
    $totalRemoteBytes = [int64]0
    $totalDeltaRawBytes = [int64]0

    $scriptSelfFull = ''
    try { $scriptSelfFull = [System.IO.Path]::GetFullPath($env:REM_SELF) } catch { }

    if ($manifest -and $manifest.files) {
        $remotePathSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($entry in $manifest.files) {
            $rel = ([string]$entry.path -replace '\\', '/').TrimStart('/')
            $targetFull = Test-SafeRelativePath -RootDir $installDir -RelativePath $rel
            if (-not $targetFull) {
                throw "Manifest contains invalid relative path: $rel"
            }

            if ($scriptSelfFull -and [string]::Equals($targetFull, $scriptSelfFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $null = $remotePathSet.Add($rel)
            $totalRemoteBytes += [int64]$entry.size

            $needsDownload = $true
            if (Test-Path -LiteralPath $targetFull -PathType Leaf) {
                $fi = Get-Item -LiteralPath $targetFull
                if ($fi.Length -eq [int64]$entry.size) {
                    $localHash = Get-Sha256Lower -FilePath $targetFull
                    if ([string]::Equals($localHash, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $needsDownload = $false
                    }
                }
            }

            if ($needsDownload) {
                $filesToDownload.Add($entry)
                $totalDeltaRawBytes += [int64]$entry.compressedSize
            }
        }

        $localManifestPath = Join-Path $installDir '.rem-manifest.json'
        if (Test-Path -LiteralPath $localManifestPath -PathType Leaf) {
            try {
                $prevManifest = [System.IO.File]::ReadAllText($localManifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
                foreach ($oldEntry in $prevManifest.files) {
                    $oldRel = ([string]$oldEntry.path -replace '\\', '/').TrimStart('/')
                    if (-not $remotePathSet.Contains($oldRel)) {
                        $oldTarget = Test-SafeRelativePath -RootDir $installDir -RelativePath $oldRel
                        if ($oldTarget -and (Test-Path -LiteralPath $oldTarget -PathType Leaf)) {
                            $filesToRemove.Add($oldRel)
                        }
                    }
                }
            }
            catch {
            }
        }

        if ($filesToDownload.Count -eq 0 -and $filesToRemove.Count -eq 0) {
            Write-Host "REM is already up to date ($tagName)!" -ForegroundColor Green
            exit 0
        }

        Write-Host ("Delta summary  : {0} changed/new file(s), {1} removed" -f $filesToDownload.Count, $filesToRemove.Count) -ForegroundColor Green
        Write-Host ("Download size  : ~{0} instead of {1} full ZIP" -f (Format-Bytes $totalDeltaRawBytes), (Format-Bytes ([int64]$zipAsset.size))) -ForegroundColor Green
    }
    else {
        Write-Host ("Release $tagName has no delta manifest; full ZIP ({0}) will be used." -f (Format-Bytes ([int64]$zipAsset.size))) -ForegroundColor Yellow
    }

    # 4. Close any running REM.exe inside the target folder cleanly
    $installNorm = $installDir.TrimEnd('\') + '\'
    $runningRem = @(Get-Process -Name 'REM' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $p = $_.MainModule.FileName
            $p -and $p.StartsWith($installNorm, [System.StringComparison]::OrdinalIgnoreCase)
        }
        catch { $false }
    })

    if ($runningRem.Count -gt 0) {
        Write-Host 'Closing running REM instance(s)...' -ForegroundColor Yellow
        foreach ($proc in $runningRem) {
            try { $null = $proc.CloseMainWindow() } catch { }
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $deadline) {
            $still = @($runningRem | Where-Object { -not $_.HasExited })
            if ($still.Count -eq 0) { break }
            Start-Sleep -Milliseconds 250
        }
        foreach ($proc in $runningRem) {
            if (-not $proc.HasExited) {
                try { $proc.Kill() } catch { }
            }
        }
        Start-Sleep -Milliseconds 300
    }

    # Also stop any soloist.exe running from inside the install dir (rare legacy case)
    Get-Process -Name 'soloist' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $sp = $_.MainModule.FileName
            if ($sp -and $sp.StartsWith($installNorm, [System.StringComparison]::OrdinalIgnoreCase)) {
                $_.Kill()
            }
        }
        catch { }
    }

    # 5. Download & verify staged files
    [System.IO.Directory]::CreateDirectory($StagingDir) | Out-Null
    $zipUrl = [string]$zipAsset.browser_download_url
    $usedRangeDelta = $false

    if ($manifest -and $filesToDownload.Count -gt 0) {
        try {
            $probeReq = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $zipUrl)
            $probeReq.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue(0, 0)
            $probeResp = $http.SendAsync($probeReq, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $probeCode = [int]$probeResp.StatusCode
            $effectiveUri = $probeResp.RequestMessage.RequestUri
            $probeResp.Dispose()

            if ($probeCode -ne 206) {
                throw "Server returned HTTP $probeCode instead of 206 Partial Content."
            }

            Write-Host 'Downloading changed files via HTTP Range delta...' -ForegroundColor Cyan
            $doneCount = 0
            foreach ($entry in $filesToDownload) {
                $doneCount++
                $rel = ([string]$entry.path -replace '\\', '/').TrimStart('/')
                $stageTarget = Test-SafeRelativePath -RootDir $StagingDir -RelativePath $rel
                if (-not $stageTarget) { throw "Invalid path: $rel" }
                [System.IO.Directory]::CreateDirectory((Split-Path -Path $stageTarget -Parent)) | Out-Null

                Write-Host ("  [{0}/{1}] {2} ({3})" -f $doneCount, $filesToDownload.Count, $rel, (Format-Bytes ([int64]$entry.compressedSize)))

                if ([int64]$entry.size -eq 0) {
                    [System.IO.File]::WriteAllBytes($stageTarget, [byte[]]@())
                }
                else {
                    $rangeEnd = [int64]$entry.dataOffset + [int64]$entry.compressedSize - 1
                    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Get, $effectiveUri)
                    $req.Headers.Range = New-Object System.Net.Http.Headers.RangeHeaderValue([int64]$entry.dataOffset, $rangeEnd)
                    $resp = $http.SendAsync($req, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                    try {
                        if ([int]$resp.StatusCode -ne 206) {
                            throw "Range fetch for $rel returned HTTP $([int]$resp.StatusCode)."
                        }

                        $rawStream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
                        $outFs = [System.IO.File]::Create($stageTarget)
                        try {
                            if ([int]$entry.compressionMethod -eq 8) {
                                $deflate = New-Object System.IO.Compression.DeflateStream($rawStream, [System.IO.Compression.CompressionMode]::Decompress)
                                try { $deflate.CopyTo($outFs) } finally { $deflate.Dispose() }
                            }
                            elseif ([int]$entry.compressionMethod -eq 0) {
                                $rawStream.CopyTo($outFs)
                            }
                            else {
                                throw "Unsupported compression method $($entry.compressionMethod) for $rel"
                            }
                        }
                        finally {
                            $outFs.Dispose()
                            $rawStream.Dispose()
                        }
                    }
                    finally {
                        $resp.Dispose()
                    }
                }

                $actualLen = (Get-Item -LiteralPath $stageTarget).Length
                $actualHash = Get-Sha256Lower -FilePath $stageTarget
                if ($actualLen -ne [int64]$entry.size -or -not [string]::Equals($actualHash, [string]$entry.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw "SHA-256 verification failed for $rel!"
                }
            }

            $usedRangeDelta = $true
        }
        catch {
            Write-Host "Delta Range download fell back to full ZIP: $($_.Exception.Message)" -ForegroundColor Yellow
            if (Test-Path -LiteralPath $StagingDir) {
                [System.IO.Directory]::Delete($StagingDir, $true)
                [System.IO.Directory]::CreateDirectory($StagingDir) | Out-Null
            }
        }
    }

    if (-not $usedRangeDelta) {
        $tempZip = Join-Path $UpdateRoot 'REM-win-x64.download.zip'
        [System.IO.Directory]::CreateDirectory($UpdateRoot) | Out-Null
        Write-Host ("Downloading full archive ({0})..." -f (Format-Bytes ([int64]$zipAsset.size))) -ForegroundColor Cyan

        $zipResp = $http.GetAsync($zipUrl, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            $zipResp.EnsureSuccessStatusCode() | Out-Null
            $inStream = $zipResp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $outStream = [System.IO.File]::Create($tempZip)
            try {
                $buffer = New-Object byte[] 65536
                $copied = [int64]0
                $total = [int64]$zipAsset.size
                $lastPct = -10
                while ($true) {
                    $n = $inStream.Read($buffer, 0, $buffer.Length)
                    if ($n -le 0) { break }
                    $outStream.Write($buffer, 0, $n)
                    $copied += $n
                    if ($total -gt 0) {
                        $pct = [int](($copied * 100) / $total)
                        if ($pct -ge $lastPct + 10) {
                            $lastPct = $pct
                            Write-Host ("  {0}% ({1} / {2})" -f $pct, (Format-Bytes $copied), (Format-Bytes $total))
                        }
                    }
                }
            }
            finally {
                $outStream.Dispose()
                $inStream.Dispose()
            }
        }
        finally {
            $zipResp.Dispose()
        }

        try {
            if ($manifest -and $manifest.zipSha256) {
                $actualZipHash = Get-Sha256Lower -FilePath $tempZip
                if (-not [string]::Equals($actualZipHash, [string]$manifest.zipSha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Downloaded ZIP SHA-256 verification failed!'
                }
            }

            $wantedMap = @{}
            if ($manifest) {
                foreach ($w in $filesToDownload) {
                    $wantedMap[([string]$w.path -replace '\\', '/').TrimStart('/')] = $w
                }
            }

            $zipArchive = [System.IO.Compression.ZipFile]::OpenRead($tempZip)
            try {
                foreach ($zEntry in $zipArchive.Entries) {
                    $rel = ($zEntry.FullName -replace '\\', '/').TrimStart('/')
                    if ([string]::IsNullOrWhiteSpace($rel) -or $rel.EndsWith('/')) { continue }
                    if ($manifest -and -not $wantedMap.ContainsKey($rel)) { continue }

                    $stageTarget = Test-SafeRelativePath -RootDir $StagingDir -RelativePath $rel
                    if (-not $stageTarget) { throw "Unsafe path inside ZIP: $rel" }
                    if ($scriptSelfFull -and [string]::Equals((Test-SafeRelativePath -RootDir $installDir -RelativePath $rel), $scriptSelfFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                        continue
                    }

                    [System.IO.Directory]::CreateDirectory((Split-Path -Path $stageTarget -Parent)) | Out-Null
                    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($zEntry, $stageTarget, $true)

                    if ($wantedMap.ContainsKey($rel)) {
                        $expected = $wantedMap[$rel]
                        $actualHash = Get-Sha256Lower -FilePath $stageTarget
                        if (-not [string]::Equals($actualHash, [string]$expected.sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                            throw "SHA-256 verification failed for extracted file: $rel"
                        }
                    }
                }
            }
            finally {
                $zipArchive.Dispose()
            }
        }
        finally {
            if (Test-Path -LiteralPath $tempZip) {
                try { [System.IO.File]::Delete($tempZip) } catch { }
            }
        }
    }

    # 6. Atomic Swap with Backup & Automatic Rollback
    Write-Host ''
    Write-Host 'Applying verified update safely...' -ForegroundColor Cyan
    [System.IO.Directory]::CreateDirectory($BackupDir) | Out-Null
    $backedUp = New-Object 'System.Collections.Generic.List[object]'
    $createdNew = New-Object 'System.Collections.Generic.List[string]'

    try {
        $stagedFiles = @(Get-ChildItem -LiteralPath $StagingDir -Recurse -File)
        $stagePrefix = $StagingDir.TrimEnd('\') + '\'
        foreach ($sf in $stagedFiles) {
            $rel = $sf.FullName.Substring($stagePrefix.Length)
            $dest = Test-SafeRelativePath -RootDir $installDir -RelativePath $rel
            if (-not $dest) { throw "Invalid destination path: $rel" }

            if (Test-Path -LiteralPath $dest -PathType Leaf) {
                $bak = Test-SafeRelativePath -RootDir $BackupDir -RelativePath $rel
                Copy-WithRetry -Source $dest -Dest $bak
                $backedUp.Add([PSCustomObject]@{ Bak = $bak; Dest = $dest })
            }
            else {
                $createdNew.Add($dest)
            }

            Copy-WithRetry -Source $sf.FullName -Dest $dest
        }

        foreach ($remRel in $filesToRemove) {
            $remDest = Test-SafeRelativePath -RootDir $installDir -RelativePath $remRel
            if ($remDest -and (Test-Path -LiteralPath $remDest -PathType Leaf)) {
                $bak = Test-SafeRelativePath -RootDir $BackupDir -RelativePath $remRel
                Copy-WithRetry -Source $remDest -Dest $bak
                $backedUp.Add([PSCustomObject]@{ Bak = $bak; Dest = $remDest })
                [System.IO.File]::Delete($remDest)
            }
        }

        if ($manifest) {
            $localManifestOut = Join-Path $installDir '.rem-manifest.json'
            $manifestPayload = [ordered]@{
                version = $remoteVersion
                tag     = $tagName
                files   = @($manifest.files | ForEach-Object {
                    [ordered]@{
                        path   = ([string]$_.path -replace '\\', '/').TrimStart('/')
                        size   = [int64]$_.size
                        sha256 = [string]$_.sha256
                    }
                })
            } | ConvertTo-Json -Depth 5
            [System.IO.File]::WriteAllText($localManifestOut, $manifestPayload, [System.Text.UTF8Encoding]::new($false))
        }
    }
    catch {
        Write-Host "ERROR applying files: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'Rolling back to previous working files...' -ForegroundColor Yellow
        foreach ($newPath in $createdNew) {
            try { if (Test-Path -LiteralPath $newPath) { [System.IO.File]::Delete($newPath) } } catch { }
        }
        foreach ($pair in $backedUp) {
            try { Copy-WithRetry -Source $pair.Bak -Dest $pair.Dest } catch { }
        }
        throw "Update failed cleanly and previous version was restored: $($_.Exception.Message)"
    }

    Write-Host ''
    Write-Host "SUCCESS! REM updated to $tagName." -ForegroundColor Green
    Write-Host "Launching REM..." -ForegroundColor Cyan
    Start-Process -FilePath $exePath -WorkingDirectory $installDir | Out-Null
}
finally {
    $http.Dispose()
    try { if (Test-Path -LiteralPath $StagingDir) { [System.IO.Directory]::Delete($StagingDir, $true) } } catch { }
    try { if (Test-Path -LiteralPath $BackupDir) { [System.IO.Directory]::Delete($BackupDir, $true) } } catch { }
}
