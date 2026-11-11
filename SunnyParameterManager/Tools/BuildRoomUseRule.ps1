param(
    [Parameter(Mandatory = $true)][string]$SourceMd,
    [Parameter(Mandatory = $true)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceMd).Path
$lines = Get-Content -LiteralPath $source -Encoding UTF8
$section = $false
$buffer = ''
$terms = [System.Collections.Generic.List[object]]::new()

function Add-Row([string]$line) {
    if ($line -notmatch '^\|\s*(.*?)\s*\|\s*([CERUZ])\s*\|\s*(.*?)\s*\|\s*([0-9]{5})\s*\|$') {
        throw "사전 행을 읽지 못했습니다: $line"
    }
    $name = $Matches[1].Trim()
    $use = $Matches[2]
    $examples = $Matches[3]
    $code = $Matches[4]
    $cases = [System.Collections.Generic.List[string]]::new()
    $token = ''
    $depth = 0
    foreach ($char in $examples.ToCharArray()) {
        if ($char -eq '(') { $depth++ }
        if ($char -eq ')') { $depth-- }
        if ($char -eq ',' -and $depth -le 0) {
            if ($token.Trim().Length -gt 0) { $cases.Add($token.Trim()) }
            $token = ''
        } else { $token += $char }
    }
    if ($token.Trim().Length -gt 0) { $cases.Add($token.Trim()) }
    $terms.Add([ordered]@{ Name = $name; Code = $code; Use = $use; Cases = @($cases) })
}

foreach ($line in $lines) {
    if ($line -match '^## 부록 A\.') { $section = $true; continue }
    if (-not $section -or $line -match '^\| 세분류명 ' -or $line -match '^\|---') { continue }
    if ($line -match '^\|') {
        if ($buffer.Length -gt 0) { throw "완료되지 않은 사전 행: $buffer" }
        $buffer = $line.Trim()
    } elseif ($buffer.Length -gt 0) {
        $buffer += ' ' + $line.Trim()
    } else { continue }
    if ($buffer -match '\|\s*[0-9]{5}\s*\|$') {
        Add-Row $buffer
        $buffer = ''
    }
}
if ($buffer.Length -gt 0 -or $terms.Count -ne 283 -or ($terms.Code | Select-Object -Unique).Count -ne 283) {
    throw "실 종류 사전 수가 맞지 않습니다. 읽은 행 $($terms.Count)개 (기대 283개)."
}
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$rule = [ordered]@{
    Version = 11
    Name = '용도구분+공간정보엑셀검토'
    Rules = @()
    RoomUse = [ordered]@{
        Source = [IO.Path]::GetFileName($source)
        SourceSha256 = $hash
        AuditFields = @('실명', '번호', '기존실번호', '지상지하', '층')
        Terms = @($terms)
        SemanticTerms = @(
            [ordered]@{ Phrase = '입출하대'; Use = 'R'; Basis = '작업절차의 하역장(R)에 해당하는 작업 공간' },
            [ordered]@{ Phrase = '캐노피'; Use = 'Z'; Basis = '작업절차의 외부공간 캐노피(Z)' },
            [ordered]@{ Phrase = '관제탑'; Use = 'R'; Basis = '사람이 근무하는 관제 공간' },
            [ordered]@{ Phrase = '초소'; Use = 'R'; Basis = '사람이 근무하는 경비 공간' }
        )
    }
}
$folder = Split-Path -Parent $OutputFile
if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
if (Test-Path -LiteralPath $OutputFile) { throw "기존 룰셋은 덮어쓰지 않습니다: $OutputFile" }
$rule | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputFile -Encoding UTF8
Write-Output "Room 용도 룰셋 생성: $($terms.Count)개 사전 / $OutputFile"
