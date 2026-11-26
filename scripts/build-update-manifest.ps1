param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$resolvedPublishDir = [System.IO.Path]::GetFullPath($PublishDir).TrimEnd('\', '/')
$resolvedZipPath = [System.IO.Path]::GetFullPath($ZipPath)
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)

if (-not (Test-Path -LiteralPath $resolvedPublishDir -PathType Container)) {
    throw "Publish directory not found: $resolvedPublishDir"
}

if (-not (Test-Path -LiteralPath $resolvedZipPath -PathType Leaf)) {
    throw "ZIP archive not found: $resolvedZipPath"
}

function Get-FileSha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            $hash = $sha.ComputeHash($fs)
            return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
        }
        finally {
            $fs.Dispose()
        }
    }
    finally {
        $sha.Dispose()
    }
}

$entries = New-Object 'System.Collections.Generic.List[object]'
$seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)

$fs = [System.IO.File]::OpenRead($resolvedZipPath)
try {
    $br = New-Object System.IO.BinaryReader($fs, [System.Text.Encoding]::UTF8, $true)
    try {
        $fileLen = $fs.Length
        if ($fileLen -lt 22) {
            throw "ZIP archive is too small ($fileLen bytes)."
        }

        $minOffset = [Math]::Max([int64]0, $fileLen - [int64]65557)
        $eocdOffset = [int64]-1
        $bufLen = [int]($fileLen - $minOffset)
        $tail = New-Object byte[] $bufLen
        $fs.Position = $minOffset
        $readTotal = 0
        while ($readTotal -lt $bufLen) {
            $n = $fs.Read($tail, $readTotal, $bufLen - $readTotal)
            if ($n -le 0) { break }
            $readTotal += $n
        }

        for ($i = $bufLen - 22; $i -ge 0; $i--) {
            if ($tail[$i] -eq 0x50 -and $tail[$i + 1] -eq 0x4B -and $tail[$i + 2] -eq 0x05 -and $tail[$i + 3] -eq 0x06) {
                $commentLen = [System.BitConverter]::ToUInt16($tail, $i + 20)
                if (($minOffset + $i + 22 + $commentLen) -eq $fileLen) {
                    $eocdOffset = $minOffset + $i
                    break
                }
            }
        }

        if ($eocdOffset -lt 0) {
            throw "Could not locate ZIP End of Central Directory record."
        }

        $fs.Position = $eocdOffset + 10
        $totalEntries = $br.ReadUInt16()
        $cdSize = $br.ReadUInt32()
        $cdOffset = [int64]$br.ReadUInt32()

        if ($totalEntries -eq 0xFFFF -or $cdOffset -eq 0xFFFFFFFF) {
            throw "ZIP64 archives are not supported by the delta manifest generator."
        }

        $fs.Position = $cdOffset
        for ($idx = 0; $idx -lt $totalEntries; $idx++) {
            $sig = $br.ReadUInt32()
            if ($sig -ne 0x02014B50) {
                throw "Invalid ZIP central directory signature at entry $idx."
            }

            $null = $br.ReadUInt16() # version made by
            $null = $br.ReadUInt16() # version needed
            $null = $br.ReadUInt16() # flags
            $compressionMethod = $br.ReadUInt16()
            $null = $br.ReadUInt16() # mod time
            $null = $br.ReadUInt16() # mod date
            $null = $br.ReadUInt32() # crc32
            $compressedSize = [int64]$br.ReadUInt32()
            $uncompressedSize = [int64]$br.ReadUInt32()
            $nameLen = $br.ReadUInt16()
            $extraLen = $br.ReadUInt16()
            $commentLen = $br.ReadUInt16()
            $null = $br.ReadUInt16() # disk number
            $null = $br.ReadUInt16() # internal attrs
            $null = $br.ReadUInt32() # external attrs
            $localHeaderOffset = [int64]$br.ReadUInt32()

            $nameBytes = $br.ReadBytes($nameLen)
            $entryName = ([System.Text.Encoding]::UTF8.GetString($nameBytes) -replace '\\', '/').TrimStart('/')
            $nextCdPos = $fs.Position + [int64]$extraLen + [int64]$commentLen

            if (-not [string]::IsNullOrWhiteSpace($entryName) -and -not $entryName.EndsWith('/')) {
                if ($entryName.Contains('..') -or [System.IO.Path]::IsPathRooted($entryName)) {
                    throw "Unsafe archive path detected: $entryName"
                }

                if ($compressionMethod -ne 0 -and $compressionMethod -ne 8) {
                    throw "Unsupported compression method ($compressionMethod) for $entryName"
                }

                $fs.Position = $localHeaderOffset
                $localSig = $br.ReadUInt32()
                if ($localSig -ne 0x04034B50) {
                    throw "Invalid local file header signature for $entryName"
                }

                $fs.Position = $localHeaderOffset + 26
                $localNameLen = [int64]$br.ReadUInt16()
                $localExtraLen = [int64]$br.ReadUInt16()
                $dataOffset = $localHeaderOffset + 30 + $localNameLen + $localExtraLen

                $diskPath = Join-Path $resolvedPublishDir ($entryName -replace '/', [System.IO.Path]::DirectorySeparatorChar)
                if (-not (Test-Path -LiteralPath $diskPath -PathType Leaf)) {
                    throw "Published file missing for archive entry: $entryName"
                }

                $fileInfo = Get-Item -LiteralPath $diskPath
                if ($fileInfo.Length -ne $uncompressedSize) {
                    throw "Size mismatch for $entryName (disk=$($fileInfo.Length), zip=$uncompressedSize)"
                }

                $fileHash = Get-FileSha256Lower -Path $diskPath
                if ($seenPaths.Add($entryName)) {
                    $entries.Add([ordered]@{
                        path              = $entryName
                        size              = $uncompressedSize
                        sha256            = $fileHash
                        dataOffset        = $dataOffset
                        compressedSize    = $compressedSize
                        compressionMethod = [int]$compressionMethod
                    })
                }
            }

            $fs.Position = $nextCdPos
        }
    }
    finally {
        $br.Dispose()
    }
}
finally {
    $fs.Dispose()
}

$sortedEntries = @($entries.ToArray() | Sort-Object { $_.path })
$zipFileInfo = Get-Item -LiteralPath $resolvedZipPath
$zipHash = Get-FileSha256Lower -Path $resolvedZipPath

$manifest = [ordered]@{
    version      = $Version.TrimStart('v', 'V')
    tag          = $Tag
    zipAsset     = $zipFileInfo.Name
    zipSize      = [int64]$zipFileInfo.Length
    zipSha256    = $zipHash
    fileCount    = $sortedEntries.Count
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    files        = [object[]]$sortedEntries
}

$outParent = Split-Path -Path $resolvedOutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outParent)) {
    [System.IO.Directory]::CreateDirectory($outParent) | Out-Null
}

$json = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($resolvedOutputPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated delta manifest: $resolvedOutputPath ($($sortedEntries.Count) files, ZIP $($zipFileInfo.Length) bytes)"
