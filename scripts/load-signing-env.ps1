$credentialsPath = Join-Path $env:APPDATA "VeXArk\signing\credentials.json"
if (Test-Path -LiteralPath $credentialsPath) {
    $credentials = Get-Content -LiteralPath $credentialsPath -Raw | ConvertFrom-Json
    $securePassword = ConvertTo-SecureString $credentials.protectedPassword
    $password = [Net.NetworkCredential]::new("", $securePassword).Password

    $env:VEXARK_KEYSTORE_PATH = [string]$credentials.keystorePath
    $env:VEXARK_KEYSTORE_PASSWORD = $password
    $env:VEXARK_KEY_ALIAS = [string]$credentials.alias
    $env:VEXARK_KEY_PASSWORD = $password
}
