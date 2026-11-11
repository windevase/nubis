# Nubis 빌드/배포 스크립트
#   .\build.ps1          → Debug 빌드 후 실행
#   .\build.ps1 test     → 자동 테스트 (tests\Nubis.Tests)
#   .\build.ps1 publish  → publish\ 에 폴더형 게시 (자체 포함, .NET 설치 불필요: Nubis.exe + Nubis.dll + 런타임 DLL + wwwroot\)
#                          + dist\Nubis-<버전>-portable-win-x64.zip (압축 풀고 바로 실행) 과 SHA256SUMS.txt
#   .\build.ps1 publish -Version 0.2.0   → 버전 지정 (기본: Nubis.csproj 의 <Version>)
param([string]$Mode = "run", [string]$Version = "")

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# 사용자 폴더 설치 SDK 우선, 없으면 PATH 의 dotnet
$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" } else { $env:DOTNET_ROOT = Split-Path $dotnet }

$proj = Join-Path $PSScriptRoot "RemoteClient\Nubis.csproj"

if ($Mode -eq "test") {
    & $dotnet test (Join-Path $PSScriptRoot "tests\Nubis.Tests")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} elseif ($Mode -eq "publish") {
    if (-not $Version) { $Version = ([xml](Get-Content $proj -Raw -Encoding UTF8)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
    # 단일 파일(PublishSingleFile)은 쓰지 않는다 — 번들 exe 를 백신·샌드박스가 드로퍼로 오탐하는 일이 잦았다.
    # 일반 .NET 앱과 같은 폴더형(apphost Nubis.exe + Nubis.dll + 런타임 DLL)으로 게시한다.
    $out = Join-Path $PSScriptRoot "publish"
    # 기존 폴더에 다시 게시하면 SDK 증분 정리가 wwwroot\·고지문을 지우고 다시 복사하지 않는다 → 항상 비우고 게시
    # (실행 중인 publish\Nubis.exe 가 있으면 여기서 실패한다 — 앱을 닫고 다시 실행)
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:Version=$Version -o $out
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Get-ChildItem $out -Filter *.xml | Remove-Item

    # 포터블 zip: 압축을 풀면 Nubis\ 폴더 하나 (Nubis.exe + DLL + wwwroot\ + 문서)
    $dist = Join-Path $PSScriptRoot "dist"
    $stage = Join-Path $dist "Nubis"
    $zipName = "Nubis-$Version-portable-win-x64.zip"
    $zip = Join-Path $dist $zipName
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item (Join-Path $out "*") $stage -Recurse
    Copy-Item (Join-Path $PSScriptRoot "LICENSE"), (Join-Path $PSScriptRoot "README.md") $stage
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $stage -DestinationPath $zip
    Remove-Item $stage -Recurse -Force

    # 앱 고유 파일(Nubis.exe·Nubis.dll)과 zip 의 해시. 런타임 DLL 은 Microsoft 서명 파일 그대로다
    $sums = @($zip, (Join-Path $out "Nubis.exe"), (Join-Path $out "Nubis.dll")) | ForEach-Object {
        "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLower())  $(Split-Path $_ -Leaf)"
    }
    [IO.File]::WriteAllLines((Join-Path $dist "SHA256SUMS.txt"), $sums)
    $files = @(Get-ChildItem $out -Recurse -File)
    Write-Host "`n완료: $out\Nubis.exe (폴더 전체를 함께 배포 — 파일 $($files.Count)개, $([math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB)) MB)"
    Write-Host "포터블: $zip ($([math]::Round((Get-Item $zip).Length / 1MB)) MB)"
    $sums | ForEach-Object { Write-Host "  $_" }
} else {
    & $dotnet build $proj -c Debug
    Start-Process (Join-Path $PSScriptRoot "RemoteClient\bin\Debug\net10.0-windows\Nubis.exe")
}
