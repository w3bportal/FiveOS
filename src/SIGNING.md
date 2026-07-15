# Code-signing FiveOS.exe

An **unsigned** `FiveOS.exe` will always risk two things on a public download:

1. **Windows SmartScreen** → "Windows protected your PC / Unknown publisher" on
   first run for everyone who downloads it.
2. **Heuristic AV false positives** (e.g. Bkav flagging it on VirusTotal) — because
   a compressed, self-extracting single-file exe *looks* packer-ish to dumb ML.

**Code-signing fixes both.** It is the only permanent fix. Everything else
(per-vendor false-positive reports) is temporary whack-a-mole.

---

## Recommended: Azure Artifact Signing (~$10/month)

Formerly "Azure Trusted Signing." Cheapest real option, no USB token, builds
SmartScreen reputation over time. **Individual accounts: USA & Canada only**
(elsewhere you need an org with 3+ years of verifiable history, or a traditional
cert — see below).

One-time setup:

1. Create/point at a **paid** Azure subscription (free/trial/sponsored are not
   supported).
2. In the Azure portal, create a **Trusted/Artifact Signing account** + a
   **Certificate Profile**. Complete the identity validation (individual or org).
3. Grant your user the **Trusted Signing Certificate Profile Signer** role on the
   account.
4. Install the **Windows SDK** (for `signtool.exe`, min 10.0.22621.755+) and the
   **Trusted Signing dlib** (`Azure.CodeSigning.Dlib`), and sign in with
   `az login` / the Azure CLI so the dlib can auth.
5. Create a `metadata.json` describing your endpoint + account + profile, e.g.:

   ```json
   {
     "Endpoint": "https://eus.codesigning.azure.net/",
     "CodeSigningAccountName": "your-signing-account",
     "CertificateProfileName": "your-profile"
   }
   ```

Then publish signed:

```powershell
dotnet publish src/FiveOS.csproj -c Release `
  /p:SignExe=true `
  /p:SignToolPath="C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe" `
  /p:SigningDlib="C:\path\to\Azure.CodeSigning.Dlib.dll" `
  /p:SigningMetadata="C:\path\to\metadata.json"
```

The `SignPublishedExe` target in `FiveOS.csproj` runs `signtool` after publish and
verifies the signature. Without `/p:SignExe=true` it does nothing.

---

## Alternative: traditional OV/EV certificate

Buy from a CA (SSL.com, Sectigo, DigiCert), ~$200–600/yr. All new code-signing
certs must live on a **hardware token / HSM** now. **EV** gives *instant*
SmartScreen trust (no reputation wait); **OV** still needs downloads to accumulate.

To use a cert instead of Azure, override the `<Exec>` in the `SignPublishedExe`
target with a cert-store/PFX command, e.g.:

```
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a FiveOS.exe
```

---

## After signing

- Signed exe → SmartScreen warning goes away (instantly with EV; after some
  downloads with OV/Azure).
- Re-upload the signed `FiveOS.exe` to the GitHub Release (replace the asset).
- Re-scan on VirusTotal to confirm the heuristic flags drop.
