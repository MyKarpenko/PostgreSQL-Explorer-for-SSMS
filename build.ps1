<#
.SYNOPSIS
    Builds SsmsPgExplorer.vsix and optionally installs it into SSMS 22.
.EXAMPLE
    .\build.ps1                 # build Release
    .\build.ps1 -Install        # build, then install (SSMS must be closed)
    .\build.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [string]$Configuration = 'Release',
    [string]$SsmsIde = 'C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE'
)
$ErrorActionPreference = 'Stop'
$extId = 'SsmsPgExplorer.7C2E4F19-6B3A-4D8E-A1F0-9E5B3C7D2A64'
$proj = Join-Path $PSScriptRoot 'src\SsmsPgExplorer\SsmsPgExplorer.csproj'
$vsix = Join-Path $PSScriptRoot "src\SsmsPgExplorer\bin\$Configuration\SsmsPgExplorer.vsix"
$installer = Join-Path $SsmsIde 'VSIXInstaller.exe'

function Assert-SsmsClosed {
    if (Get-Process ssms -ErrorAction SilentlyContinue) { throw 'Close SSMS first (VSIXInstaller cannot update a running instance).' }
}

if ($Uninstall) {
    Assert-SsmsClosed
    & $installer /quiet "/uninstall:$extId"
    "Uninstalled (exit $LASTEXITCODE)."
    return
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (install Visual Studio or Build Tools).' }

# Always a full rebuild: the VSSDK's incremental build can keep a stale obj\...\extension.vsixmanifest,
# so the VSIX would carry an old version number even though the source manifest was bumped.
$obj = Join-Path $PSScriptRoot 'src\SsmsPgExplorer\obj'
if (Test-Path $obj) { Get-ChildItem $obj -Recurse -Filter extension.vsixmanifest -ErrorAction SilentlyContinue | Remove-Item -Force }
& $msbuild $proj /restore /t:Rebuild "/p:Configuration=$Configuration" "/p:SsmsIdeDir=$SsmsIde" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }

# Guard: the packaged manifest must carry the source version.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$srcVersion = ([xml](Get-Content (Join-Path $PSScriptRoot 'src\SsmsPgExplorer\source.extension.vsixmanifest') -Raw)).PackageManifest.Metadata.Identity.Version
$zip = [IO.Compression.ZipFile]::OpenRead($vsix)
try {
    $reader = New-Object IO.StreamReader(($zip.Entries | Where-Object FullName -eq 'extension.vsixmanifest').Open())
    $packed = ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity.Version
    $reader.Close()
} finally { $zip.Dispose() }
if ($packed -ne $srcVersion) { throw "VSIX manifest version $packed does not match source $srcVersion." }
"Version: $packed"
"Built: $vsix"

if ($Install) {
    Assert-SsmsClosed
    & $installer /quiet $vsix
    "Installed (exit $LASTEXITCODE). Start SSMS; look under Tools > PostgreSQL Registered Servers."
}
