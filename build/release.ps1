# Dựng gói phát hành một folder restApi cho PROD (chạy ở máy build nội bộ, không phải công cụ cho người vận hành):
#   1. dotnet publish WebApi (framework-dependent) và PortalApp (single-file) vào CÙNG một thư mục, bật Obfuscar (-p:Protect=true)
#   2. Sinh integrity.json (SHA-256 mọi .dll/.exe) đúng định dạng Security.Licensing.IntegrityDocument
#   3. SA tải integrity.json lên WebPortal → /cpanel/licenses → "Ký manifest toàn vẹn", đặt integrity.sig nhận được cạnh nó
# Không đưa appsettings.Local.json, restApi.lic hay khóa nào vào gói.
param(
    [string]$Output = (Join-Path $PSScriptRoot "..\out\restApi"),
    [string]$Version = "3.0.0",
    [switch]$NoProtect
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$protect = if ($NoProtect) { "false" } else { "true" }

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

Push-Location $root
try {
    dotnet tool restore | Out-Null
    dotnet publish WebApi\WebApi.csproj -c Release -o $Output -p:Protect=$protect -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "publish WebApi lỗi" }
    dotnet publish PortalApp\PortalApp.csproj -c Release -o $Output -p:Protect=$protect -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { throw "publish PortalApp lỗi" }
}
finally {
    Pop-Location
}

foreach ($secret in @("appsettings.Local.json", "restApi.lic", "integrity.sig")) {
    $path = Join-Path $Output $secret
    if (Test-Path $path) { Remove-Item -Force $path }
}

$files = Get-ChildItem -Path $Output -Recurse -File -Include *.dll, *.exe | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path   = $_.FullName.Substring((Resolve-Path $Output).Path.Length).TrimStart('\').Replace('\', '/')
        sha256 = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    format    = 1
    product   = "Fast.RestApi"
    version   = $Version
    createdAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    files     = @($files)
}
$json = $manifest | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText((Join-Path $Output "integrity.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Đã dựng $Output ($($files.Count) nhị phân). Bước tiếp: ký integrity.json ở WebPortal (/cpanel/licenses) rồi đặt integrity.sig vào cùng thư mục."
