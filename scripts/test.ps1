<#
.SYNOPSIS
    Сборка и прогон тестов VR.RoutePlanner с параметрами интеграционного стенда.

.DESCRIPTION
    Интеграционные тесты ходят в базу rms_vrp_test на 192.168.89.131 и читают реальный
    растр карты 801. Параметры берутся из окружения, при отсутствии в процессе - из
    пользовательской области (User), чтобы скрипт работал в новой сессии.

    Без -NoDb не заданная строка подключения считается ошибкой: молчаливые Skip
    в интеграционных тестах маскируют дефекты миграций и сохранения ресурсов.

.EXAMPLE
    .\scripts\test.ps1
    .\scripts\test.ps1 -NoDb
    .\scripts\test.ps1 -NoDb -NoBuild
#>
param(
    [switch]$NoDb,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

if ($NoDb) {
    Remove-Item Env:VRP_RMS_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:VRP_MAP_RASTER -ErrorAction SilentlyContinue
    Write-Host 'Режим без базы: интеграционные тесты будут пропущены.' -ForegroundColor Yellow
}
else {
    foreach ($name in 'VRP_RMS_CONNECTION', 'VRP_MAP_RASTER') {
        if (-not [Environment]::GetEnvironmentVariable($name, 'Process')) {
            $fromUser = [Environment]::GetEnvironmentVariable($name, 'User')
            if ($fromUser) { Set-Item "Env:$name" $fromUser }
        }
    }

    if (-not $env:VRP_RMS_CONNECTION) {
        throw 'VRP_RMS_CONNECTION не задана. Задай строку подключения к rms_vrp_test или запусти с -NoDb.'
    }

    if (-not $env:VRP_MAP_RASTER) {
        Write-Host 'VRP_MAP_RASTER не задана: тест реального растра будет пропущен.' -ForegroundColor Yellow
    }
    elseif (-not (Test-Path $env:VRP_MAP_RASTER)) {
        throw "Файл растра не найден: $env:VRP_MAP_RASTER"
    }
}

dotnet tool restore | Out-Null

if (-not $NoBuild) {
    dotnet build VR.RoutePlanner.sln --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась.' }
}

dotnet test VR.RoutePlanner.sln --nologo --no-build
exit $LASTEXITCODE
