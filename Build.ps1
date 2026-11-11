param([string]$OutputPath = '', [switch]$IncludePrivateMedia, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $projectInfo = [xml][IO.File]::ReadAllText((Join-Path $projectRoot 'MemoryShell/MemoryShell.csproj'))
    $version = @($projectInfo.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if ($version -notmatch '^[0-9A-Za-z.-]+$') { throw 'Invalid project version.' }
    $OutputPath = "Release/Your-Memories-Shell-$version-win-x64"
}
$destination = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputPath))
if (Test-Path -LiteralPath $destination) { throw "目录已存在：$destination" }
if (Test-Path -LiteralPath "$destination.zip") { throw "压缩包已存在：$destination.zip" }
$stage = Join-Path ([IO.Path]::GetDirectoryName($destination)) ('.build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    $project = Join-Path $projectRoot 'MemoryShell/MemoryShell.csproj'
    & dotnet clean $project -c Release -v:q
    if ($LASTEXITCODE -ne 0) { throw '清理构建缓存失败。' }
    $arguments = @('publish', $project, '-c', 'Release', '--self-contained', 'true', '-o', $stage,
        '-p:PublishSingleFile=true', '-p:IncludeAllContentForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none', '-p:DebugSymbols=false', '-p:CopyOutputSymbolsToPublishDirectory=false', '-v:minimal')
    if ($IncludePrivateMedia) { $arguments += '-p:IncludePrivateMedia=true' }
    if ($NoRestore) { $arguments += '--no-restore' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
    Move-Item -LiteralPath (Join-Path $stage 'MemoryShell.exe') -Destination (Join-Path $stage 'Your-Memories-Shell.exe')
    Get-ChildItem -LiteralPath $stage -Filter '*.pdb' -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stage }
    $notices = New-Object Text.StringBuilder
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -File | Sort-Object Name) {
        [void]$notices.AppendLine($file.Name)
        [void]$notices.AppendLine([IO.File]::ReadAllText($file.FullName))
    }
    [IO.File]::WriteAllText((Join-Path $stage 'THIRD_PARTY_LICENSES.txt'), $notices.ToString())
    [IO.Directory]::Move($stage, $destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($destination, "$destination.zip")
    Write-Host "程序：$destination"
    Write-Host "压缩包：$destination.zip"
} finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
        if ($resolved -eq [IO.Path]::GetFullPath($stage) -and [IO.Path]::GetDirectoryName($resolved) -eq [IO.Path]::GetDirectoryName($destination)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
