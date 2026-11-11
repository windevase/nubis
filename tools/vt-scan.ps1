# VirusTotal 검사 결과 확인 (PowerShell 7 이상 — GitHub Actions 의 pwsh)
#   업로드만 하고 끝내지 않고, 분석이 끝날 때까지 기다려 탐지 엔진 수·엔진 이름·진단명을 출력한다.
#   build.yml(릴리스)·vt-scan.yml(수동 재검사)에서 쓴다.
#
#   $env:VT_API_KEY = '...'
#   ./tools/vt-scan.ps1 -Path publish/Nubis.exe, publish/Nubis.dll
#   ./tools/vt-scan.ps1 -Path publish/Nubis.exe -Rescan    # 이미 VT 에 있는 파일도 최신 엔진으로 다시 분석
#
# 무료 공개 API 한도(분당 4회)에 맞춰 호출 사이를 16초 띄운다. 키 값은 출력하지 않는다.
param(
    [Parameter(Mandatory)][string[]]$Path,
    [switch]$Rescan,
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) { throw "PowerShell 7 이상(pwsh)에서 실행하세요." }
if (-not $env:VT_API_KEY) { throw "VT_API_KEY 환경변수가 없습니다." }

$api = "https://www.virustotal.com/api/v3"
$headers = @{ "x-apikey" = $env:VT_API_KEY }
$script:lastCall = [datetime]::MinValue

function Invoke-Vt([string]$Method, [string]$Uri, $Form = $null) {
    # 분당 4회 한도
    $wait = 16 - ((Get-Date) - $script:lastCall).TotalSeconds
    if ($wait -gt 0) { Start-Sleep -Seconds ([math]::Ceiling($wait)) }
    $script:lastCall = Get-Date
    $p = @{ Method = $Method; Uri = $Uri; Headers = $headers; SkipHttpErrorCheck = $true; StatusCodeVariable = "code"; TimeoutSec = 1800 }
    if ($Form) { $p.Form = $Form }
    $r = Invoke-RestMethod @p
    if ($code -eq 429) { Write-Host "  API 한도 초과 — 60초 대기"; Start-Sleep 60; return Invoke-Vt $Method $Uri $Form }
    [pscustomobject]@{ Code = [int]$code; Body = $r }
}

function Send-File([string]$File) {
    # 32MB 를 넘으면 전용 업로드 주소를 받아서 올린다 (포터블 zip 등)
    $uploadUri = "$api/files"
    if ((Get-Item $File).Length -gt 32MB) {
        $u = Invoke-Vt GET "$api/files/upload_url"
        if ($u.Code -ne 200) { throw "upload_url 실패: HTTP $($u.Code)" }
        $uploadUri = $u.Body.data
    }
    # 업로드는 curl 로 — Invoke-RestMethod -Form 의 multipart(filename* 등)를 대용량 업로드 주소가 HTTP 400 으로 거부했다.
    # API 키는 명령줄에 남지 않게 헤더 파일로 넘긴다
    $hdr = New-TemporaryFile; $resp = New-TemporaryFile
    try {
        [IO.File]::WriteAllText($hdr, "x-apikey: $($env:VT_API_KEY)")
        $script:lastCall = Get-Date
        $code = & curl -sS -o $resp -w "%{http_code}" -H "@$hdr" -F "file=@$((Resolve-Path $File).Path)" $uploadUri
        $body = Get-Content $resp -Raw | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($code -ne "200") { throw "업로드 실패: HTTP $code $($body.error.message)" }
        $body.data.id
    } finally { Remove-Item $hdr, $resp -ErrorAction SilentlyContinue }
}

function Wait-Analysis([string]$Id) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ((Get-Date) -lt $deadline) {
        $a = Invoke-Vt GET "$api/analyses/$Id"
        if ($a.Code -eq 200 -and $a.Body.data.attributes.status -eq "completed") { return }
        Write-Host "  분석 중... ($($a.Body.data.attributes.status))"
        Start-Sleep 30
    }
    Write-Warning "  분석이 $TimeoutMinutes 분 안에 끝나지 않았습니다 — 마지막 결과로 보고합니다."
}

$rows = @()
foreach ($file in $Path) {
    if (-not (Test-Path $file)) { Write-Warning "없는 파일: $file"; continue }
    $sha = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    $size = [math]::Round((Get-Item $file).Length / 1MB, 1)
    Write-Host "`n▶ $file ($size MB) $sha"

    $f = Invoke-Vt GET "$api/files/$sha"
    if ($f.Code -eq 404) {
        Write-Host "  VT 에 없음 — 업로드"
        Wait-Analysis (Send-File $file)
    } elseif ($f.Code -eq 200 -and $Rescan) {
        Write-Host "  VT 에 있음 — 다시 분석 요청"
        $r = Invoke-Vt POST "$api/files/$sha/analyse"
        if ($r.Code -eq 200) { Wait-Analysis $r.Body.data.id }
    } elseif ($f.Code -ne 200) {
        throw "조회 실패: HTTP $($f.Code) $($f.Body.error.message)"
    } else {
        Write-Host "  VT 에 있음 — 기존 결과 사용 (-Rescan 으로 다시 분석)"
    }

    # 분석 직후에는 파일 보고서 반영이 늦을 수 있어 한 번 더 조회
    $f = Invoke-Vt GET "$api/files/$sha"
    $attr = $f.Body.data.attributes
    $stats = $attr.last_analysis_stats
    $hits = @($attr.last_analysis_results.PSObject.Properties.Value |
        Where-Object { $_.category -in "malicious", "suspicious" } |
        Sort-Object engine_name)
    foreach ($h in $hits) { Write-Host ("  ✖ {0,-22} {1,-10} {2}" -f $h.engine_name, $h.category, $h.result) }
    if (-not $hits) { Write-Host "  ✔ 탐지 없음" }

    $rows += [pscustomobject]@{
        File       = $file
        Malicious  = [int]$stats.malicious
        Suspicious = [int]$stats.suspicious
        Undetected = [int]$stats.undetected
        Engines    = ($hits | ForEach-Object { "$($_.engine_name): $($_.result)" }) -join "; "
        Link       = "https://www.virustotal.com/gui/file/$sha"
    }
}

Write-Host ""
$rows | Format-Table File, Malicious, Suspicious, Undetected -AutoSize | Out-String | Write-Host

# GitHub Actions 실행 요약에 표로 남긴다
if ($env:GITHUB_STEP_SUMMARY) {
    $md = @("### VirusTotal", "", "| File | Malicious | Suspicious | Undetected | Engines | Report |", "|---|---|---|---|---|---|")
    foreach ($r in $rows) {
        $md += "| ``$($r.File)`` | $($r.Malicious) | $($r.Suspicious) | $($r.Undetected) | $(if ($r.Engines) { $r.Engines } else { '-' }) | [VirusTotal]($($r.Link)) |"
    }
    $md -join "`n" | Add-Content -Encoding utf8 $env:GITHUB_STEP_SUMMARY
}
