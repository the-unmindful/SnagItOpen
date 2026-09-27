# Building SnagItOpen

## Requirements

- Windows 10/11 x64.
- .NET SDK 10.0.401 (pinned in `global.json`, `latestPatch` roll-forward).
  On the development machine the SDK was installed per-user with Microsoft's `dotnet-install.ps1`
  into `%LOCALAPPDATA%\Microsoft\dotnet`. If `dotnet --list-sdks` shows no 10.x SDK, put that folder
  first on `PATH`:

  ```powershell
  $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH"
  ```

## Commands (from the repository root)

```powershell
dotnet build .\SnagItOpen.slnx -c Debug                                             # V0
dotnet test .\tests\SnagItOpen.Core.Tests\SnagItOpen.Core.Tests.csproj -c Debug     # V1
dotnet test .\tests\SnagItOpen.Windows.Tests\SnagItOpen.Windows.Tests.csproj -c Debug  # V2
dotnet run --project .\src\SnagItOpen.App\SnagItOpen.App.csproj                     # V3
.\scripts\publish.ps1                                                               # Release package
```

`publish.ps1` builds Release, runs both test suites, publishes a self-contained, untrimmed
`win-x64` folder to `artifacts\win-x64`, zips it with the README and notices, and writes a
SHA-256 checksum next to the zip.

## Notes

- `SNAGITOPEN_DATA` overrides the data folder (default `%LOCALAPPDATA%\SnagItOpen`). Tests and smoke
  runs use it so they never touch real user data.
- `SnagItOpen.Windows.Tests` contains one real-desktop check (100 GDI captures, handle count stable).
  It needs an interactive desktop session; it will fail on a headless build agent.
- Clipboard tests use a fake adapter and never overwrite the user's clipboard.
