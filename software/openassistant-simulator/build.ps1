param([switch]$WithoutWake)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    if (-not $WithoutWake) {
        if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
            throw "Не найден cargo. Установите Rust MSVC toolchain: https://rustup.rs/"
        }
        cargo build --release --manifest-path ".\WakeDetector\Cargo.toml"
        if ($LASTEXITCODE -ne 0) { throw "Сборка wake-детектора завершилась с ошибкой" }
    }
    dotnet build ".\OpenAssistant.Simulator.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Сборка Windows-клиента завершилась с ошибкой" }
    Write-Host "Готово. Запуск: dotnet run -c Release"
}
finally {
    Pop-Location
}
