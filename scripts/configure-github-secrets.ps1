param(
    [Parameter(Mandatory = $true)]
    [string]$Repository
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is required. Install it and run gh auth login."
}
& gh auth status
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not authenticated." }

. (Join-Path $PSScriptRoot "load-signing-env.ps1")
if (-not $env:VEXARK_KEYSTORE_PATH) {
    throw "Create the signing key first with scripts\create-signing-key.ps1."
}

$keystoreBase64 = [Convert]::ToBase64String(
    [IO.File]::ReadAllBytes($env:VEXARK_KEYSTORE_PATH))

& gh secret set VEXARK_KEYSTORE_BASE64 `
    --repo $Repository `
    --body $keystoreBase64
if ($LASTEXITCODE -ne 0) { throw "Failed to configure VEXARK_KEYSTORE_BASE64." }

& gh secret set VEXARK_KEYSTORE_PASSWORD `
    --repo $Repository `
    --body $env:VEXARK_KEYSTORE_PASSWORD
if ($LASTEXITCODE -ne 0) { throw "Failed to configure VEXARK_KEYSTORE_PASSWORD." }

& gh secret set VEXARK_KEY_PASSWORD `
    --repo $Repository `
    --body $env:VEXARK_KEY_PASSWORD
if ($LASTEXITCODE -ne 0) { throw "Failed to configure VEXARK_KEY_PASSWORD." }

Write-Host "Android signing secrets configured for $Repository."
