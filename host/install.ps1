# Area Fullscreen 도우미 프로그램을 빌드하고 Chrome 에 등록한다. (관리자 권한 필요 없음)
$ErrorActionPreference = "Stop"

$hostName = "com.areafullscreen.host"
$extensionId = "jeblbgpopdkdlglillcdelanpgbhfhdi"  # extension/manifest.json 의 key 로 정해지는 ID

$binDir = Join-Path $PSScriptRoot "bin"
$exe = Join-Path $binDir "AreaFullscreenHost.exe"
$manifest = Join-Path $binDir "$hostName.json"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force $binDir | Out-Null
& $csc /nologo /optimize /target:winexe "/out:$exe" `
    /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    (Join-Path $PSScriptRoot "AreaFullscreenHost.cs")
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

$json = [ordered]@{
    name            = $hostName
    description     = "Area Fullscreen helper"
    path            = $exe
    type            = "stdio"
    allowed_origins = @("chrome-extension://$extensionId/")
} | ConvertTo-Json
[IO.File]::WriteAllText($manifest, $json)  # Chrome 이 BOM 을 싫어하므로 BOM 없는 UTF-8 로 저장

reg.exe add "HKCU\Software\Google\Chrome\NativeMessagingHosts\$hostName" /ve /t REG_SZ /d "$manifest" /f | Out-Null
if ($LASTEXITCODE -ne 0) { throw "레지스트리 등록 실패" }

Write-Host "설치 완료: $exe"
