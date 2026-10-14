# Builds the web version as one folder of static files, ready for GitHub Pages or any web server: the page (web/),
# the engine worker and the WebAssembly runtime (published from src/GameFerry.Web.Engine), and .nojekyll so Pages
# serves the _framework folder. Needs the .NET 10 SDK with the wasm-tools workload.
param(
    [string] $Output = 'site',
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$site = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $root $Output }
$marker = Join-Path $site '.gameferry-site'

# Only ever clear a folder this script made.
if (Test-Path -LiteralPath $site) {
    if (-not (Test-Path -LiteralPath $marker)) { throw "$site exists and was not made by this script; choose another -Output." }
    Remove-Item -LiteralPath $site -Recurse -Force
}

$engine = Join-Path ([IO.Path]::GetTempPath()) "gameferry-engine-$([guid]::NewGuid().ToString('N'))"
try {
    dotnet publish (Join-Path $root 'src/GameFerry.Web.Engine/GameFerry.Web.Engine.csproj') -c $Configuration -o $engine
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the web engine failed.' }

    New-Item -ItemType Directory -Path $site | Out-Null
    New-Item -ItemType File -Path $marker | Out-Null
    New-Item -ItemType File -Path (Join-Path $site '.nojekyll') | Out-Null
    Copy-Item -Path (Join-Path $root 'web/*') -Destination $site -Recurse

    # Pages can't serve the pre-compressed copies, so leave them out.
    $wwwroot = Join-Path $engine 'wwwroot'
    Get-ChildItem -LiteralPath $wwwroot -Recurse -File | Where-Object { $_.Extension -notin '.br', '.gz' } | ForEach-Object {
        $target = Join-Path $site $_.FullName.Substring($wwwroot.Length + 1)
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}
finally {
    if (Test-Path -LiteralPath $engine) { Remove-Item -LiteralPath $engine -Recurse -Force }
}

$size = (Get-ChildItem -LiteralPath $site -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Web version built in {0} ({1:N1} MB)." -f $site, ($size / 1MB))
