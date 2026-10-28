param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,
    [Parameter(Mandatory = $true)]
    [string] $FormXamlPath
)

$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$assemblyDirectory = Split-Path -Parent $assemblyPath
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve([ResolveEventHandler]{
    param($sender, $request)
    try { return [System.Reflection.Assembly]::ReflectionOnlyLoad($request.Name) }
    catch {
        $simpleName = (New-Object System.Reflection.AssemblyName($request.Name)).Name
        $candidate = Join-Path $assemblyDirectory ($simpleName + '.dll')
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)
        }
        throw
    }
})
$assembly = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($assemblyPath)
$pageType = $assembly.GetType('PCL.FormMain+PageType', $true)

# These public enum values predate Bedrock and are consumed by custom events.
$expected = [ordered]@{
    Launch = 0
    Download = 1
    Link = 2
    Setup = 3
    Other = 4
    InstanceSelect = 5
    TaskManager = 6
    InstanceSetup = 7
    ResourceDetail = 8
    HelpDetail = 9
    Bedrock = 10
}
foreach ($name in $expected.Keys) {
    $field = $pageType.GetField($name)
    if ($null -eq $field -or $field.GetRawConstantValue() -ne $expected[$name]) {
        throw "Public PageType value changed or is missing: $name"
    }
}
$xaml = [xml](Get-Content -LiteralPath $FormXamlPath -Raw -Encoding UTF8)
$xNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$buttons = [ordered]@{
    BtnTitleSelect0 = '0'
    BtnTitleSelect1 = '1'
    BtnTitleSelect2 = '2'
    BtnTitleSelect3 = '3'
    BtnTitleSelect4 = '4'
    BtnTitleSelectBedrock = '10'
}
$radioButtons = @($xaml.SelectNodes("//*[local-name()='MyRadioButton']"))
foreach ($name in $buttons.Keys) {
    $match = @($radioButtons | Where-Object { $_.GetAttribute('Name', $xNamespace) -eq $name })
    if ($match.Count -ne 1 -or $match[0].GetAttribute('Tag') -ne $buttons[$name]) {
        throw "Navigation button tag changed or is missing: $name"
    }
}
Write-Output 'Public PageType values and navigation tags retain their expected identities.'
