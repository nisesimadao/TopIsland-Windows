param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$blurOutput = Join-Path $output 'BlurHost'

Get-Process TopIsland,TopIsland.BlurHost -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

if (Test-Path $output) {
    Remove-Item $output -Recurse -Force
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $blurOutput -Force | Out-Null

Push-Location $root
try {
    dotnet publish 'TopIsland\TopIsland.csproj' `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -o $output
    if ($LASTEXITCODE -ne 0) { throw 'TopIsland publish failed.' }

    dotnet publish 'TopIsland.BlurHost\TopIsland.BlurHost.csproj' `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -p:Platform=x64 `
        -o $blurOutput
    if ($LASTEXITCODE -ne 0) { throw 'TopIsland.BlurHost publish failed.' }

    # Native Skia symbols are useful for local debugging but add ~90 MB to the
    # redistributable layout. Keep the installed build lean.
    Get-ChildItem $output -Recurse -Filter '*.pdb' -File | Remove-Item -Force
}
finally {
    Pop-Location
}

Write-Output "Published TopIsland to $output"
Write-Output "Published blur helper to $blurOutput"
