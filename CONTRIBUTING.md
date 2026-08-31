# Contributing

## Development setup

Requirements:

- Windows 10 or 11 x64
- the .NET SDK selected by `global.json`
- Git

Run the validation sequence before submitting a change:

```powershell
dotnet restore Torifune.slnx --locked-mode
dotnet format Torifune.slnx --verify-no-changes --no-restore
dotnet build Torifune.slnx -c Release --no-restore /nr:false
dotnet test tests/Torifune.Core.Tests/Torifune.Core.Tests.csproj -c Release --no-build
```

When changing NuGet dependencies, update the lock files explicitly:

```powershell
dotnet restore Torifune.slnx --force-evaluate
```

## Pull requests

- Keep changes focused and describe the user-visible behavior.
- Add or update tests for changed behavior.
- Pass external-process arguments through `ProcessStartInfo.ArgumentList`.
- Do not log URL query strings, authentication data, or unmasked user paths.
- Do not commit downloaded media, external tools, signing certificates, build output, or local logs.

By contributing, you agree that your contribution is licensed under the repository's MIT License.
