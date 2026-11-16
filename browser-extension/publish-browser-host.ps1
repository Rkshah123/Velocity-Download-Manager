param([string]$OutputDirectory = "$PSScriptRoot\..\artifacts\browser-host")
$project = Join-Path $PSScriptRoot "..\src\Velocity.BrowserHost\Velocity.BrowserHost.csproj"
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Published native host to $OutputDirectory"
