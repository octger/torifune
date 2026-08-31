# Release procedure

## Prerequisites

- all CI checks pass on `main`
- `CHANGELOG.md` contains the release version and date
- `Directory.Build.props` and the Git tag use the same semantic version
- `dotnet restore Torifune.slnx --locked-mode` succeeds
- no vulnerable dependency is reported by NuGet audit
- Windows code-signing secrets are configured when a signed release is required

## Local validation

```powershell
dotnet restore Torifune.slnx --locked-mode
dotnet format Torifune.slnx --verify-no-changes --no-restore
dotnet build Torifune.slnx -c Release --no-restore /nr:false
dotnet test tests/Torifune.Core.Tests/Torifune.Core.Tests.csproj -c Release --no-build
./scripts/publish.ps1 -Version 1.0.0
```

The publish script creates these files under `artifacts/release`:

- `Torifune-<version>-win-x64.zip`
- `Torifune-<version>-win-x64.zip.sha256`

The ZIP is self-contained, includes `LICENSE` and `THIRD-PARTY-NOTICES.md`, and excludes non-x64 LibVLC assets.

## Code signing

For local signing, install the Windows SDK, set the certificate password only in the process environment, and pass the PFX path:

```powershell
$env:TORIFUNE_CERTIFICATE_PASSWORD = '<enter locally>'
./scripts/publish.ps1 -Version 1.0.0 -CertificatePath C:\secure\torifune.pfx
Remove-Item Env:TORIFUNE_CERTIFICATE_PASSWORD
```

Never commit a PFX file or its password. For GitHub Actions, configure these repository secrets:

- `SIGNING_CERTIFICATE_BASE64`: Base64-encoded PFX
- `SIGNING_CERTIFICATE_PASSWORD`: PFX password

Without these secrets, the workflow creates an unsigned ZIP. Windows SmartScreen may warn users about unsigned or low-reputation binaries.

## GitHub release

Push an annotated semantic-version tag:

```powershell
git tag -a v1.0.0 -m "Torifune 1.0.0"
git push origin v1.0.0
```

The release workflow reruns tests, creates the archive and checksum, optionally signs `Torifune.exe`, and uploads the artifacts to GitHub Releases.
