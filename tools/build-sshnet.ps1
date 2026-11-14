# SSH.NET 포크 패키지 다시 만들기
#
# Nubis 는 SSH.NET 에 패치 하나(third_party\SSH.NET\nubis-sftp-exec.patch)를 얹어 쓴다.
#   SftpClient.SftpServerCommand — "sftp" 서브시스템 대신 명령(sudo sftp-server 등)을 실행해 SFTP 를 연다.
#   sudo su - 로 root 가 된 터미널을 SFTP 패널이 root 권한으로 따라가는 데 쓴다.
# 결과 패키지(third_party\SSH.NET\packages\*.nupkg)는 저장소에 들어 있고 nuget.config 가 그 폴더를 원본으로 쓴다.
# SSH.NET 을 올릴 때: -Tag 와 -Version 을 바꿔 실행 → 패치가 안 맞으면 고친 뒤 패치 파일을 다시 뽑는다
#   (git -C <작업 폴더> diff > third_party\SSH.NET\nubis-sftp-exec.patch) → Nubis.csproj 의 버전 변경.
param(
    [string]$Tag = '2026.0.0',
    # 2026.0.0 보다 높아야 한다 — '2026.0.0-nubis' 처럼 쓰면 NuGet 이 2026.0.0 보다 낮은 버전으로 보고
    # 2026.0.0 에서 고친 취약점(GHSA-mggc-4xg6-vcxf, < 2026.0.0) 경고를 낸다
    [string]$Version = '2026.0.0.1-nubis'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$patch = Join-Path $root 'third_party\SSH.NET\nubis-sftp-exec.patch'
$outDir = Join-Path $root 'third_party\SSH.NET\packages'
$work = Join-Path ([IO.Path]::GetTempPath()) "nubis-sshnet-$Tag"

$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' } else { $env:DOTNET_ROOT = Split-Path $dotnet }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
# 테스트 폴더에 긴 경로가 있어 core.longpaths 없이는 체크아웃이 실패한다
git -c core.longpaths=true clone --quiet --depth 1 --branch $Tag https://github.com/sshnet/SSH.NET.git $work
if ($LASTEXITCODE) { throw "SSH.NET $Tag 를 받지 못했습니다." }
git -C $work apply --whitespace=nowarn $patch
if ($LASTEXITCODE) { throw '패치가 맞지 않습니다. 작업 폴더에서 직접 고친 뒤 패치 파일을 다시 뽑으세요.' }

# 버전: version.json 에 포크 버전을 넣고, 얕은 클론이라 NBGV 의 git 높이 계산은 끈다
$vj = Join-Path $work 'version.json'
$json = [IO.File]::ReadAllText($vj) -replace '"version":\s*"[^"]+"', ('"version": "' + $Version + '"')
# 패키지 버전도 네 자리까지 (기본은 세 자리라 2026.0.0.1 의 마지막 자리가 빠진다)
$json = $json -replace '"semVer":\s*2\.0', '"semVer": 2.0, "precision": "revision"'
[IO.File]::WriteAllText($vj, $json)
$env:NBGV_GitEngine = 'Disabled'

& $dotnet pack (Join-Path $work 'src\Renci.SshNet\Renci.SshNet.csproj') -c Release -o (Join-Path $work 'out') `
    -p:TargetFrameworks=net10.0 -p:EnablePackageValidation=false -p:IncludeSymbols=false -p:PublicRelease=true
if ($LASTEXITCODE) { throw 'SSH.NET 빌드 실패' }

New-Item -ItemType Directory -Force $outDir | Out-Null
Copy-Item (Join-Path $work "out\SSH.NET.$Version.nupkg") $outDir -Force
Write-Host "만듦: $outDir\SSH.NET.$Version.nupkg"
# 같은 버전으로 다시 만들었으면 NuGet 캐시의 예전 것을 지워야 반영된다
Write-Host "같은 버전을 다시 만든 경우: Remove-Item -Recurse `"$env:USERPROFILE\.nuget\packages\ssh.net\$($Version.ToLower())`""
