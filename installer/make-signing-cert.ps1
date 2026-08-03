# Creates a self-signed code-signing certificate for Multi-SSH (once per machine)
# and exports its public part to installer\Multi-SSH-CodeSigning.cer.
#
# The private key stays in your CurrentUser\My certificate store and is NEVER
# written to the repo. build-installer.ps1 uses this cert to sign automatically.
#
# NOTE: a self-signed cert is not trusted by other machines. Recipients who want
# the signature to validate must import the .cer into their Trusted Root and
# Trusted Publishers stores (see SIGNING.md).

$ErrorActionPreference = "Stop"
$subject = "CN=Multi-SSH (joc00p)"

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(5)
    Write-Host "Created code-signing cert  $($cert.Thumbprint)" -ForegroundColor Green
} else {
    Write-Host "Reusing existing cert  $($cert.Thumbprint)" -ForegroundColor Green
}

$cer = Join-Path $PSScriptRoot "Multi-SSH-CodeSigning.cer"
Export-Certificate -Cert $cert -FilePath $cer -Type CERT -Force | Out-Null
Write-Host "Exported public cert to $cer"
