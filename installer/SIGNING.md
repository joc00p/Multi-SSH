# Code signing

The installer and the app executable are Authenticode-signed with a **self-signed**
code-signing certificate (`CN=Multi-SSH (joc00p)`) and timestamped via DigiCert.

## What this gives you
- **Tamper-evidence** — Windows detects if the signed binary is modified.
- **A consistent publisher identity** on the file's Digital Signatures tab.

## What it does NOT do
A self-signed certificate is **not trusted by other machines**, so Windows
SmartScreen still shows an "unknown publisher" prompt for other users (More info →
Run anyway). Removing that prompt for everyone requires a certificate from a public
CA (DigiCert, Sectigo, …) or an EV cert; swap that cert in and re-run the build.

## Trusting the signature on a machine (optional)
To make the signature validate as **Valid** on a given PC, import the public cert
(`Multi-SSH-CodeSigning.cer`) into Trusted Root and Trusted Publishers for the
current user (PowerShell):

```powershell
Import-Certificate -FilePath .\Multi-SSH-CodeSigning.cer -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath .\Multi-SSH-CodeSigning.cer -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

(Importing to Trusted Root may show a confirmation dialog — that's expected.)

## Rebuilding / re-signing
1. `pwsh installer\make-signing-cert.ps1` — creates the cert once (private key stays
   in your certificate store; never committed).
2. `pwsh installer\build-installer.ps1` — publishes, signs the exe, builds the
   installer, and signs the installer.
