Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function New-Directory([string[]] $Path) {
  if (!(Test-Path -Path $Path)) {
    New-Item -Path $Path -Force -ItemType "Directory" | Out-Null
  }
}

function Copy-File([string[]] $Path, [string] $Destination, [switch] $Force, [switch] $Recurse) {
  if (!(Test-Path -Path $Destination)) {
    New-Item -Path $Destination -Force:$Force -ItemType "Directory" | Out-Null
  }
  Copy-Item -Path $Path -Destination $Destination -Force:$Force -Recurse:$Recurse
}

try {
  $ScriptName = [System.IO.Path]::GetFileNameWithoutExtension($MyInvocation.MyCommand.Name)

  $RepoRoot = Join-Path -Path $PSScriptRoot -ChildPath ".."

  $SourceRoot = Join-Path -Path $RepoRoot -ChildPath "sources"

  $ArtifactsRoot = Join-Path -Path $RepoRoot -ChildPath "artifacts"
  New-Directory -Path $ArtifactsRoot

  $BuildRoot = Join-Path -Path $ArtifactsRoot -ChildPath "build"
  New-Directory -Path $BuildRoot

  $PackageRoot = Join-Path $ArtifactsRoot -ChildPath "packages"
  New-Directory -Path $PackageRoot

  $DotNetInstallScriptUri = "https://dot.net/v1/dotnet-install.ps1"
  Write-Host "${ScriptName}: Downloading dotnet-install.ps1 script from $DotNetInstallScriptUri..." -ForegroundColor Yellow
  $DotNetInstallScript = Join-Path -Path $ArtifactsRoot -ChildPath "dotnet-install.ps1"
  Invoke-WebRequest -Uri $DotNetInstallScriptUri -OutFile $DotNetInstallScript -UseBasicParsing

  Write-Host "${ScriptName}: Installing dotnet 6.0..." -ForegroundColor Yellow
  $DotNetInstallDirectory = Join-Path -Path $ArtifactsRoot -ChildPath "dotnet"
  New-Directory -Path $DotNetInstallDirectory

  $env:DOTNET_CLI_TELEMETRY_OPTOUT = 1
  $env:DOTNET_MULTILEVEL_LOOKUP = 0
  $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = 1

  # & $DotNetInstallScript -Channel 6.0 -Version latest -InstallDir $DotNetInstallDirectory

  $env:PATH="$DotNetInstallDirectory;$env:PATH"

  Write-Host "${ScriptName}: Restoring dotnet tools..." -ForegroundColor Yellow
  & dotnet tool restore
  if ($LastExitCode -ne 0) {
    throw "${ScriptName}: Failed restore dotnet tools."
  }

  Write-Host "${ScriptName}: Calculating NuGet version for FFmpeg..." -ForegroundColor Yellow
  $NuGetVersion = dotnet gitversion /showvariable NuGetVersion /output json
  if ($LastExitCode -ne 0) {
    throw "${ScriptName}: Failed calculate NuGet version for FFmpeg."
  }

  $SourceDir = Join-Path -Path $SourceRoot -ChildPath "FFmpeg"
  $BuildDir = Join-Path -Path $BuildRoot -ChildPath "FFmpeg.nupkg"

  Write-Host "${ScriptName}: Producing FFmpeg multi-platform package folder structure in $BuildDir..." -ForegroundColor Yellow
  Copy-File -Path "$RepoRoot\packages\FFmpeg\*" -Destination $BuildDir -Force -Recurse
  Copy-File -Path "$SourceDir\LICENSE.md" $BuildDir
  Copy-File -Path "$SourceDir\README.md" $BuildDir

  Write-Host "${ScriptName}: Replacing variable `$version`$ in runtime.json with value '$NuGetVersion'..." -ForegroundColor Yellow
  $RuntimeContent = Get-Content $BuildDir\runtime.json -Raw
  $RuntimeContent = $RuntimeContent.replace('$version$', $NuGetVersion)
  Set-Content $BuildDir\runtime.json $RuntimeContent

  Write-Host "${ScriptName}: Building FFmpeg multi-platform package..." -ForegroundColor Yellow
  & nuget pack $BuildDir\FFmpeg.nuspec -Properties version=$NuGetVersion -OutputDirectory $PackageRoot
  if ($LastExitCode -ne 0) {
    throw "${ScriptName}: Failed to build FFmpeg multi-platform package."
  }
}
catch {
  Write-Host -Object $_ -ForegroundColor Red
  Write-Host -Object $_.Exception -ForegroundColor Red
  Write-Host -Object $_.ScriptStackTrace -ForegroundColor Red
  exit 1
}