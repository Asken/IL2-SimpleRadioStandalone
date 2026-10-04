<#
.SYNOPSIS
    Builds the IL2-SRS server image, pushes it to Docker Hub and updates the Docker Hub page.

.DESCRIPTION
    1. Refuses to run with uncommitted changes (the image is labelled with the current commit).
    2. Builds IL2-SRS-Server-Web/Dockerfile from the repository root with version and revision labels.
    3. Pushes <version><suffix> (e.g. 1.0.4.12-preview) and the channel tag (e.g. preview).
    4. Sets the repository's short description and overview (IL2-SRS-Server-Web/DOCKERHUB.md).

    Pushing and updating the page both use your existing `docker login` (read from Docker's credential
    helper; nothing is printed). Editing the page needs a personal access token with 'Read, Write, Delete'
    access; if your docker login token has less, set DOCKERHUB_TOKEN to such a token. It takes precedence.

.EXAMPLE
    .\IL2-SRS-Server-Web\scripts\Publish-DockerImage.ps1

.EXAMPLE
    # A release: tags 1.0.4.13 and latest
    .\IL2-SRS-Server-Web\scripts\Publish-DockerImage.ps1 -Suffix '' -Channel latest
#>
[CmdletBinding()]
param(
    [string] $Repository = 'asken/il2-srs-server',
    [string] $Suffix = '-preview',
    [string] $Channel = 'preview',
    [string] $ShortDescription = 'IL2 SimpleRadio Standalone server with browser admin UI and REST API (IL-2 Great Battles & Korea)',
    [switch] $AllowDirty,
    [switch] $SkipHubPage
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$projectDir = Join-Path $repoRoot 'IL2-SRS-Server-Web'
$overviewFile = Join-Path $projectDir 'DOCKERHUB.md'

function Invoke-Native([string] $what, [scriptblock] $command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$namespace = $Repository.Split('/')[0]

# Docker Hub credentials for the website API: DOCKERHUB_TOKEN if set, otherwise the `docker login` credential.
function Get-HubCredential {
    $token = $env:DOCKERHUB_TOKEN
    if (-not $token) { $token = [Environment]::GetEnvironmentVariable('DOCKERHUB_TOKEN', 'User') }
    if ($token) { return @{ Username = $namespace; Secret = $token; Source = 'DOCKERHUB_TOKEN' } }

    $configFile = Join-Path $HOME '.docker\config.json'
    $store = if (Test-Path $configFile) { (Get-Content $configFile -Raw | ConvertFrom-Json).credsStore }
    if (-not $store) { return $null }

    # Write the server URL without a trailing newline (piping from PowerShell would add one and the lookup fails).
    $helper = New-Object System.Diagnostics.Process
    $helper.StartInfo.FileName = "docker-credential-$store"
    $helper.StartInfo.Arguments = 'get'
    $helper.StartInfo.UseShellExecute = $false
    $helper.StartInfo.RedirectStandardInput = $true
    $helper.StartInfo.RedirectStandardOutput = $true
    $helper.StartInfo.RedirectStandardError = $true
    try { [void]$helper.Start() } catch { return $null }
    $helper.StandardInput.Write('https://index.docker.io/v1/')
    $helper.StandardInput.Close()
    $stored = $helper.StandardOutput.ReadToEnd()
    $helper.WaitForExit()
    if ($helper.ExitCode -ne 0 -or -not $stored) { return $null }

    $credential = $stored | ConvertFrom-Json
    return @{ Username = $credential.Username; Secret = $credential.Secret; Source = 'docker login' }
}

# Check everything we need before building.
$hubCredential = if ($SkipHubPage) { $null } else { Get-HubCredential }
if (-not $SkipHubPage -and -not $hubCredential) {
    throw 'No Docker Hub login found. Run `docker login`, set DOCKERHUB_TOKEN, or pass -SkipHubPage.'
}

if ($hubCredential -and $hubCredential.Username -ne $namespace) {
    throw "Logged in to Docker Hub as '$($hubCredential.Username)', but the repository belongs to '$namespace'."
}

if ($ShortDescription.Length -gt 100) { throw "The short description is $($ShortDescription.Length) characters; Docker Hub allows 100." }

$dirty = git -C $repoRoot status --porcelain
if ($dirty -and -not $AllowDirty) { throw 'There are uncommitted changes. Commit them first, or pass -AllowDirty.' }

$metadata = Get-Content (Join-Path $repoRoot 'IL2-SR-Common\Network\ReleaseMetadata.cs') -Raw
$version = [regex]::Match($metadata, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw 'Could not read the version from ReleaseMetadata.cs.' }

$revision = (git -C $repoRoot rev-parse HEAD).Trim()
$versionTag = "$Repository`:$version$Suffix"
$channelTag = "$Repository`:$Channel"

Write-Host "Building $versionTag and $channelTag from $($revision.Substring(0, 7))"
Invoke-Native 'docker build' {
    docker build -f (Join-Path $projectDir 'Dockerfile') `
        --build-arg "VERSION=$version$Suffix" `
        --build-arg "REVISION=$revision" `
        --build-arg "IMAGE_URL=https://hub.docker.com/r/$Repository" `
        -t $versionTag -t $channelTag $repoRoot
}

Invoke-Native 'docker push' { docker push $versionTag }
Invoke-Native 'docker push' { docker push $channelTag }

if ($SkipHubPage) {
    Write-Host 'Skipped the Docker Hub page (-SkipHubPage).'
    return
}

try {
    $login = Invoke-RestMethod -Method Post -Uri 'https://hub.docker.com/v2/users/login' -ContentType 'application/json' `
        -Body (@{ username = $hubCredential.Username; password = $hubCredential.Secret } | ConvertTo-Json)
}
catch {
    throw "The image is pushed, but Docker Hub did not accept the $($hubCredential.Source) credential for updating the page " +
          "($($_.Exception.Message)). Set DOCKERHUB_TOKEN to a personal access token with 'Read, Write, Delete' access and run the script again."
}

$body = @{
    description      = $ShortDescription
    full_description = (Get-Content $overviewFile -Raw)
} | ConvertTo-Json
try {
    Invoke-RestMethod -Method Patch -Uri "https://hub.docker.com/v2/repositories/$Repository/" `
        -Headers @{ Authorization = "Bearer $($login.token)" } -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
}
catch {
    throw "The image is pushed, but the Docker Hub page was not updated ($($_.Exception.Message)). " +
          "Editing the description needs a personal access token with 'Read, Write, Delete' access; " +
          "set DOCKERHUB_TOKEN to one (or docker login with it) and run the script again."
}

Write-Host "Published $versionTag and $channelTag, and updated https://hub.docker.com/r/$Repository"
