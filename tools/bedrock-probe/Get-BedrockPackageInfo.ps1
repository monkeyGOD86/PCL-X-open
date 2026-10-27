[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$PackageName = 'Microsoft.MinecraftUWP'
)

$ErrorActionPreference = 'Stop'
$result = [ordered]@{
    schemaVersion = 1
    scope = 'CurrentWindowsUser'
    queryName = $PackageName
    queryStatus = 'Unknown'
    packages = @()
}

try {
    $packages = @(Get-AppxPackage -Name $PackageName -ErrorAction Stop |
        Where-Object { $_.Name -eq $PackageName })
} catch {
    $result.queryStatus = 'QueryFailed'
    $result | ConvertTo-Json -Depth 7
    exit 1
}

foreach ($package in $packages) {
    $item = [ordered]@{
        name = $package.Name
        familyName = $package.PackageFamilyName
        version = $package.Version.ToString()
        architecture = $package.Architecture.ToString()
        packageStatus = $package.Status.ToString()
        manifestStatus = 'Unknown'
        applications = @()
        microsoftGameConfig = 'Unknown'
    }

    try {
        $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
        $item.applications = @($manifest.Package.Applications.Application | ForEach-Object {
            $appId = $_.GetAttribute('Id')
            if ($appId) {
                [ordered]@{
                    id = $appId
                    aumid = "$($package.PackageFamilyName)!$appId"
                    entryPoint = $_.GetAttribute('EntryPoint')
                }
            }
        })
        $item.manifestStatus = 'Read'
    } catch {
        $item.manifestStatus = 'Unavailable'
    }

    if ($package.InstallLocation) {
        try {
            $configFiles = @(Get-ChildItem -LiteralPath $package.InstallLocation -Filter 'MicrosoftGame.config' -File -ErrorAction Stop)
            $item.microsoftGameConfig = if ($configFiles.Count -gt 0) { 'Present' } else { 'Absent' }
        } catch {
            $item.microsoftGameConfig = 'Unknown'
        }
    }
    $result.packages += $item
}

$result.queryStatus = if ($packages.Count -gt 0) { 'Found' } else { 'NotFound' }
$result | ConvertTo-Json -Depth 7
