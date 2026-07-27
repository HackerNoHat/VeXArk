param(
    [string]$Alias = "vexark"
)

$ErrorActionPreference = "Stop"
$signingRoot = Join-Path $env:APPDATA "VeXArk\signing"
$keystorePath = Join-Path $signingRoot "vexark-release.jks"
$credentialsPath = Join-Path $signingRoot "credentials.json"

if ((Test-Path -LiteralPath $keystorePath) -or (Test-Path -LiteralPath $credentialsPath)) {
    throw "Signing material already exists in $signingRoot. Nothing was overwritten."
}

$keytoolCandidates = @(
    if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME "bin\keytool.exe" }
    "C:\Program Files\Android\Android Studio\jbr\bin\keytool.exe"
)
$keytool = $keytoolCandidates |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    Select-Object -First 1
if (-not $keytool) {
    throw "keytool.exe was not found. Install JDK 21 or Android Studio."
}

New-Item -ItemType Directory -Force $signingRoot | Out-Null
$passwordBytes = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
$random.GetBytes($passwordBytes)
$random.Dispose()
$password = [Convert]::ToBase64String($passwordBytes)
[Array]::Clear($passwordBytes, 0, $passwordBytes.Length)

try {
    & $keytool -genkeypair `
        -keystore $keystorePath `
        -storepass $password `
        -keypass $password `
        -alias $Alias `
        -keyalg RSA `
        -keysize 4096 `
        -validity 10000 `
        -dname "CN=VeXArk, OU=Release, O=VeXArk, C=RU"
    if ($LASTEXITCODE -ne 0) { throw "keytool failed with exit code $LASTEXITCODE." }

    $protected = ConvertTo-SecureString $password -AsPlainText -Force |
        ConvertFrom-SecureString
    @{
        version = 1
        alias = $Alias
        keystorePath = $keystorePath
        protectedPassword = $protected
    } | ConvertTo-Json | Set-Content -LiteralPath $credentialsPath -Encoding utf8
}
finally {
    $password = $null
}

Write-Host "VeXArk release key created outside the repository:"
Write-Host $keystorePath
Write-Host "Back up the entire signing folder. Losing this key prevents APK updates."
