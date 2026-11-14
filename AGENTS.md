# ARVSerialTool — Agent Workspace Guidelines

These instructions apply to all AI agent sessions (including cloud Remote Agents on Linux and local Windows agents) working in this repository.

## Project Architecture
- **`src/EverLogger.Core`** (`net8.0`): Cross-platform serial port manager (`RJCP.SerialPortStream`), streaming `FileSender`, lock-free data queues, and file log writers.
- **`src/EverLogger.App`** (`net8.0-windows`, WPF): Desktop UI, view models, terminal buffer, and single-file portable publish target (`ARVSerialTool.exe`).
- **`tests/EverLogger.Tests`** (xUnit): Automated tests.
  - On Windows: targets `net8.0-windows` and runs both Core tests and WPF ViewModel tests.
  - On Linux / non-Windows (including cloud Remote Agents): automatically targets `net8.0` and runs all cross-platform Core tests (`EverLogger.Core` + `FileSender` + `LogFileWriter`), while `Directory.Build.props` sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so `EverLogger.App` C# and WPF XAML still compile cleanly on Linux.

## Repository Workflow & Verification Commands
- This is a **single-branch (`main`), source-only** repository. Commit and push source changes directly to `origin/main`.
- Run both commands after every code change before pushing:
  - `dotnet build EverLogger.sln -c Release`
  - `dotnet test EverLogger.sln -c Release`

## Linux Remote Agent Constraints
- **Never commit or distribute `.exe` binaries in Git or CI.** `publish/` and `dist/` are gitignored; the user builds `dist\ARVSerialTool.exe` locally on Windows via `update_and_build.bat` after pulling from `main`.
- GitHub Actions (`windows-latest`) in `.github/workflows/build.yml` runs `dotnet test EverLogger.sln -c Release` on every push/PR to `main` to verify Windows builds and WPF ViewModel tests.
- `tools/make_icon.ps1` depends on Windows `System.Drawing` / GDI+ and must only be run on a Windows machine.
- Never attempt to open or transmit bytes to real hardware/Bluetooth COM ports during tests.

## Coding, Formatting & Git Conventions
- Preserve existing indentation (4 spaces in C#/XAML, 2 spaces in `.csproj`/YAML).
- Do not modify unrelated code or comments.
- Write all code comments and documentation in **English only**.
- Preserve exact original file encoding (**UTF-8 without BOM**) and line endings (`CRLF` for `.cs` / `.xaml` / `.bat` / `RELEASE_NOTES.md`; `LF` for `.csproj` / `.props` / `.sln` / `.yml` / `.ps1` / `.md`).
- Follow **Conventional Commits v1.0.0** (`feat(...)`, `fix(...)`, `ci(...)`, `docs(...)`, `chore(release): release vX.Y.Z`).

## Version Bump Checklist (`vX.Y.Z`)
When asked to cut a new version:
1. Update `<Version>` in `src/EverLogger.App/EverLogger.App.csproj`.
2. Update `README.md`, `docs/LIMITS.md`, and `src/EverLogger.App/HelpWindow.xaml` if user-facing behavior or limits changed.
3. Add a `## vX.Y.Z — YYYY-MM-DD` section at the top of `RELEASE_NOTES.md`.
4. Verify `dotnet test EverLogger.sln -c Release` passes.
5. Commit with `chore(release): release vX.Y.Z`, optionally create lightweight tag `vX.Y.Z` (`git tag vX.Y.Z`), and push to `origin` (`git push origin main`, and `git push origin vX.Y.Z` if tagged).
