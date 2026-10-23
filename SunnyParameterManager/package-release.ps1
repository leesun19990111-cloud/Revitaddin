param(
    [Parameter(Mandatory=$true)][ValidateSet('Create','Verify')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [Parameter(Mandatory=$true)][string]$ZipPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadHashes = Get-Content -LiteralPath (Join-Path $OutputPath 'ParameterManager/files.sha256.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$expected = @('SunnyTools_Install.exe','SunnyTools_Install.pdb','먼저 읽어주세요.md','룰셋 파일 안내.md','연습용.sunnyrules.json','빌드정보.json','ParameterManager/files.sha256.json') + @($payloadHashes.PSObject.Properties.Name | ForEach-Object { 'ParameterManager/' + $_ })
foreach ($name in $expected) {
    if ($name.Contains('..') -or $name.StartsWith('/') -or $name.Contains('\')) { throw "배포 경로 오류: $name" }
    if (-not (Test-Path -LiteralPath (Join-Path $OutputPath $name) -PathType Leaf)) { throw "배포 파일 없음: $name" }
}
if ($Mode -eq 'Create') {
    # UTF-8 이름 플래그를 함께 기록한다. 시스템 한글 코드페이지에 의존하지 않는다.
    $stream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
    try {
        foreach ($name in $expected) {
            $entry = $archive.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $source = [System.IO.File]::OpenRead((Join-Path $OutputPath $name))
            $destination = $entry.Open()
            try { $source.CopyTo($destination) } finally { $destination.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
$stream = [System.IO.File]::OpenRead($ZipPath)
$archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read, $false, [System.Text.Encoding]::UTF8)
try {
    $actual = @($archive.Entries | ForEach-Object { $_.FullName })
    if ($actual.Count -ne $expected.Count -or @($actual | Select-Object -Unique).Count -ne $expected.Count -or @(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) { throw 'ZIP 구성 불일치' }
    foreach ($entry in $archive.Entries) {
        $source = $entry.Open()
        $hasher = [System.Security.Cryptography.SHA256]::Create()
        try { $hash = [System.BitConverter]::ToString($hasher.ComputeHash($source)).Replace('-','') }
        finally { $source.Dispose(); $hasher.Dispose() }
        $diskHash = (Get-FileHash -LiteralPath (Join-Path $OutputPath $entry.FullName) -Algorithm SHA256).Hash
        if ($hash -ne $diskHash) { throw "ZIP 해시 불일치: $($entry.FullName)" }
    }
} finally { $archive.Dispose(); $stream.Dispose() }
Write-Output "ZIP 구성·해시 검증 완료: $($expected.Count)개 파일"
