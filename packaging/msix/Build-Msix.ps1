param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$IdentityName,
    [Parameter(Mandatory)][string]$Publisher,
    [Parameter(Mandatory)][string]$PublisherDisplayName,
    [string]$DisplayName = "Lagnostics - Diagn$([char]0x00F3)stico de lag",
    [Parameter(Mandatory)][string]$OutputDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path
$layout = Join-Path $OutputDir "layout"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Path "$layout\Assets" -Force | Out-Null

Copy-Item (Join-Path $PublishDir "*") $layout -Recurse -Force

$source = [System.Drawing.Image]::FromFile((Join-Path $root "DiagnosticoLag\Assets\diagnostico-lag.png"))
try {
    foreach ($asset in @(
        @{ Name = "StoreLogo.png"; Size = 50 },
        @{ Name = "Square44x44Logo.png"; Size = 44 },
        @{ Name = "Square150x150Logo.png"; Size = 150 })) {
        $bitmap = New-Object System.Drawing.Bitmap $asset.Size, $asset.Size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($source, 0, 0, $asset.Size, $asset.Size)
        $graphics.Dispose()
        $bitmap.Save((Join-Path "$layout\Assets" $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
}
finally {
    $source.Dispose()
}

$parts = @($Version.Split("."))
while ($parts.Count -lt 4) { $parts += "0" }
$msixVersion = ($parts[0..3] -join ".")

$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.template.xml") -Raw -Encoding UTF8
$replacements = @{
    "@IDENTITY_NAME@" = $IdentityName
    "@PUBLISHER@" = $Publisher
    "@VERSION@" = $msixVersion
    "@DISPLAY_NAME@" = $DisplayName
    "@PUBLISHER_DISPLAY_NAME@" = $PublisherDisplayName
}
foreach ($key in $replacements.Keys) {
    $manifest = $manifest.Replace($key, [System.Security.SecurityElement]::Escape($replacements[$key]))
}
[IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

$sdkBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$makeAppx = $null
if (Test-Path $sdkBin) {
    $makeAppx = Get-ChildItem $sdkBin -Filter makeappx.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$makeAppx) {
    $toolsDir = Join-Path ([IO.Path]::GetTempPath()) "msix-buildtools"
    if (!(Test-Path $toolsDir)) {
        $zip = Join-Path ([IO.Path]::GetTempPath()) "msix-buildtools.zip"
        Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -OutFile $zip
        Expand-Archive $zip $toolsDir -Force
        Remove-Item $zip -Force
    }
    $makeAppx = Get-ChildItem $toolsDir -Filter makeappx.exe -Recurse |
        Where-Object { $_.Directory.Name -eq "x64" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if (!$makeAppx) { throw "MakeAppx.exe was not found" }

$package = Join-Path $OutputDir "Lagnostics-$Version-x64.msix"
if (Test-Path $package) { Remove-Item $package -Force }
& $makeAppx pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE" }

Remove-Item $layout -Recurse -Force
Write-Host "Created $package"
