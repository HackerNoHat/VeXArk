# Contributing to VeXArk

Thanks for helping make offline Android backups safer.

## Development flow

1. Fork the repository and branch from `develop`.
2. Use a focused branch such as `feature/media-export` or `fix/path-validation`.
3. Keep protocol and repository formats backward-compatible or document a migration.
4. Add tests for security boundaries and destructive restore behavior.
5. Run the checks below before opening a pull request.

```powershell
dotnet test tests\PhoneBackup.Core.Tests\PhoneBackup.Core.Tests.csproj
cd agent
.\gradlew.bat :app:assembleDebug
```

For a complete local release:

```powershell
.\scripts\build.ps1 -Configuration Release
```

## Commit style

Use short conventional commits:

- `feat(desktop): add OLED theme`
- `fix(agent): preserve safe drawing insets`
- `docs: document recovery-key handling`
- `test(repository): reject traversal in portable bundles`

## Pull requests

Describe what changed, why it changed, user impact and how it was verified.
Screenshots are expected for UI changes. Mark root/restore code explicitly and
describe the test device or fixture.

By contributing, you agree that your work is provided under `GPL-3.0-only`.

