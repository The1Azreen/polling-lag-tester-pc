# Builds a single-file PollLagTester.exe into .\publish
#   .\build.ps1                 -> needs the .NET 8 (ASP.NET Core) runtime on the target PC, ~2 MB
#   .\build.ps1 -SelfContained  -> runs anywhere on Windows x64, no runtime needed, larger file
param([switch]$SelfContained)

$ErrorActionPreference = 'Stop'
$sc = if ($SelfContained) { 'true' } else { 'false' }
# Compression only applies to self-contained builds; it roughly halves the exe size.
$compress = if ($SelfContained) { '-p:EnableCompressionInSingleFile=true' } else { '' }
dotnet publish "$PSScriptRoot\src\PollLagTester\PollLagTester.csproj" -c Release -r win-x64 `
  --self-contained $sc -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true $compress `
  -o "$PSScriptRoot\publish"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "`nBuilt: $PSScriptRoot\publish\PollLagTester.exe"
