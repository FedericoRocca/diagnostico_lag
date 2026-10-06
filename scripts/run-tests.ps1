$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $applicationBin = Join-Path $repositoryRoot "DiagnosticoLag\bin"
    $testBin = Join-Path $repositoryRoot "DiagnosticoLag.Tests\bin"
    $testResults = Join-Path $repositoryRoot "TestResults"

    foreach ($path in @($applicationBin, $testBin, $testResults)) {
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    dotnet clean DiagnosticoLag.sln --configuration Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Debug clean failed with exit code $LASTEXITCODE"
    }

    dotnet clean DiagnosticoLag.sln --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Release clean failed with exit code $LASTEXITCODE"
    }

    foreach ($path in @($applicationBin, $testBin)) {
        if (Test-Path $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }

    dotnet restore DiagnosticoLag.sln
    if ($LASTEXITCODE -ne 0) {
        throw "Restore failed with exit code $LASTEXITCODE"
    }

    dotnet test DiagnosticoLag.sln `
        --configuration Release `
        --no-restore `
        --logger "console;verbosity=normal" `
        --logger "trx;LogFileName=tests.trx" `
        --results-directory $testResults
    if ($LASTEXITCODE -ne 0) {
        throw "Automated tests failed with exit code $LASTEXITCODE. Review the failing test names and details above."
    }
}
finally {
    Pop-Location
}
