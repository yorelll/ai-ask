param(
    [Parameter(Mandatory = $true)]
    [string]$StageDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$stage = (Resolve-Path $StageDirectory).Path
$manifestPath = Join-Path $stage 'plugin.json'

if (-not (Test-Path $manifestPath)) {
    throw "AI Ask release staging is missing plugin.json."
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Language -ne 'csharp') {
    throw "Expected managed-plugin manifest language, got '$($manifest.Language)'."
}
if ($manifest.Version -ne $ExpectedVersion) {
    throw "Manifest version '$($manifest.Version)' does not match expected '$ExpectedVersion'."
}
if ([string]::IsNullOrWhiteSpace($manifest.ExecuteFileName)) {
    throw 'Manifest ExecuteFileName is empty.'
}

$dllPath = Join-Path $stage $manifest.ExecuteFileName
if (-not (Test-Path $dllPath)) {
    throw "Manifest executable '$($manifest.ExecuteFileName)' is missing from staging."
}
if (-not (Test-Path (Join-Path $stage 'Images\plugin.png'))) {
    throw 'AI Ask release staging is missing Images\plugin.png.'
}

$forbiddenNames = @('main.py', 'requirements.txt', 'SettingsTemplate.yaml', 'test_v2_protocol.py')
foreach ($name in $forbiddenNames) {
    if (Test-Path (Join-Path $stage $name)) {
        throw "Python runtime file '$name' must not be in the native C# artifact."
    }
}

$forbiddenDirectories = @('lib', 'skill_files', '.venv', 'task')
foreach ($name in $forbiddenDirectories) {
    if (Test-Path (Join-Path $stage $name)) {
        throw "Python/local directory '$name' must not be in the native C# artifact."
    }
}

$assemblies = @(Get-ChildItem -Path $stage -Filter '*.dll' -File)
if ($assemblies.Count -lt 2) {
    throw 'Release staging must contain the plugin DLL and required managed dependencies.'
}
if (-not ($assemblies.Name -contains 'Flow.Launcher.Plugin.dll')) {
    throw 'C# staging is missing Flow.Launcher.Plugin.dll dependency.'
}

Write-Host "AI Ask release staging verified: $($assemblies.Count) DLLs, manifest version $ExpectedVersion."