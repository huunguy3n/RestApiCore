# Chay Garnet (tuong thich Redis) cho may dev.
# Cai mot lan: dotnet tool install -g garnet-server --version 2.2.3
# Production KHONG dung file nay: phai co --auth Password, bind dung IP noi bo, chay dang Windows Service.
param(
    [int]$Port = 6379,
    [string]$Memory = "1g",
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA "Garnet\dev")
)

$checkpointDir = Join-Path $DataDir "checkpoint"
New-Item -ItemType Directory -Force $checkpointDir | Out-Null

# --lua: rate limit dung script Lua (mac dinh Garnet tat)
# -c / -r / --checkpoint-freq: luu checkpoint moi 60s, khoi dong lai thi khoi phuc
garnet-server `
    --bind 127.0.0.1 `
    --port $Port `
    -m $Memory `
    --lua true `
    -c $checkpointDir `
    -r `
    --checkpoint-freq 60 `
    --logger-level Warning
