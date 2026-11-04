param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
    $project = 'src/QuickNote/QuickNote.csproj'
    $packages = @()
    foreach ($kind in @('Portable', 'Small')) {
        $destination = "artifacts/$kind"
        if (Test-Path $destination) { Remove-Item $destination -Recurse -Force }
        if ($kind -eq 'Portable') {
            dotnet publish $project -c Release -r $Runtime --self-contained true -p:QuickNoteFlavor=$kind -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $destination
        } else {
            dotnet publish $project -c Release -r $Runtime --self-contained false -p:QuickNoteFlavor=$kind -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $destination
        }
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
        Copy-Item "$destination/QuickNote.exe" "artifacts/QuickNote-$Runtime-$kind.exe"
        $asset = Get-Item "artifacts/QuickNote-$Runtime-$kind.exe"
        $packages += @{ flavor = $kind; size = $asset.Length; sha256 = (Get-FileHash $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        Copy-Item README.md "$destination/使用说明.md"
        $zip = "artifacts/QuickNote-$Runtime-$kind.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        Compress-Archive -Path "$destination/*" -DestinationPath $zip
    }
    Get-ChildItem "artifacts/QuickNote-$Runtime-*.exe" | ForEach-Object {
        $digest = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$digest  $($_.Name)"
    } | Set-Content artifacts/SHA256SUMS.txt -Encoding utf8NoBOM
    $version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    @{ schema = 1; version = "$version"; notes = (Get-Content RELEASE_NOTES.md -Raw -Encoding utf8); packages = $packages } |
        ConvertTo-Json -Depth 4 | Set-Content artifacts/update.json -Encoding utf8NoBOM
} finally { Pop-Location }
