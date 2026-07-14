# AGENTS.md

High-signal guidance for OpenCode agents working in this repo. Small Windows tray app that manages an `opencode web --port 4096` background process.

## Stack
C# / .NET 8 + WinForms, `net8.0-windows`, `OutputType=WinExe`. Target machine needs the .NET 8 Desktop Runtime unless published self-contained. Windows-only by design.

## CRITICAL: never kill `opencode` by name
The OpenCode agent itself runs **inside** an `opencode.exe -c` process. Any broad kill — `Get-Process opencode | Stop-Process`, `taskkill /im opencode.exe` — terminates your own session.
If a tray test leaves an orphan opencode, identify the exact PID via `Win32_Process.CommandLine` (must contain `web --port 4096`) and its parent chain, then kill **only that PID**. Never match by process name.

## Don't "simplify" the opencode launch
`StartOpencode` (in `TrayApplicationContext.cs`) launches opencode as `cmd.exe /c opencode web --port 4096` with `UseShellExecute=false` + `CreateNoWindow=true`. This is deliberate and load-bearing:
- npm-installed opencode only exposes `.cmd`/`.ps1` shims in PATH. `FileName="opencode"` + `UseShellExecute=false` **cannot** resolve shims — reverting to that breaks startup silently.
- `opencode web` is foreground-blocking (`await new Promise(()=>{})`); the tray tracks the `cmd` child via `Process.Exited` and kills the whole tree on exit.

## Icons are black + gray, NOT colored
opencode's logo is monochrome — every brand/favicon/PWA asset is 0% saturated (verified by pixel sampling). The "colored vs gray" requirement means **black = running, gray = stopped**. The gray icon is generated from the black source with ImageMagick: `magick src.png -fill "#8C8C8C" -colorize 100 ...`.

Re-sourcing icons gotcha: `packages/web/public/*` in the opencode repo are **git symlinks** (raw download returns ~50 B of path text, not an image). The real files are in `packages/ui/src/assets/favicon/` (repo `sst/opencode`). Source used here: `web-app-manifest-512x512.png`, exported to multi-size `.ico` via `magick -define icon:auto-resize=256,128,64,48,40,32,24,20,16`.

## Process-cleanup contract (operational)
Only the normal exit path cleans up opencode: right-click → `Exit` → `ExitApp` → `StopOpencode` → `Process.Kill(entireProcessTree: true)` on the `cmd` child.
**Force-killing the tray orphans opencode on port 4096**; the next launch then fails fast and shows a "启动失败 / 端口可能被占用" balloon. There is **no** orphan detection by design (it would require matching opencode — see the safety rule). Tell users to always exit via the menu.

## Click vs double-click
Left-click opens `http://127.0.0.1:4096/` only after a `SystemInformation.DoubleClickTime` delay; a second click within that window cancels the open and toggles opencode on/off instead. The `_clickTimer` is intentional — don't remove it.

## Commands
- Run (dev): `dotnet run -c Release`
- Build: `dotnet build -c Release` → `bin/Release/net8.0-windows/OpenCodeWebTray.exe`

`bin/`, `obj/`, `publish*/`, `dist/` are gitignored. Full usage docs live in `README.md`.

## Release packaging
A release ships **two** builds, each zipped. Version = `<Version>` in `OpenCodeWebTray.csproj`.

1. **self-contained** (contains .NET 8, ~155 MB, runs with nothing installed):
   `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false -o publish-sc`
2. **framework-dependent** (no .NET 8, ~hundreds of KB, needs .NET 8 Desktop Runtime):
   `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish-fdd`

Zip each output folder, named with the version:
- `OpenCodeWebTray-<version>-win-x64-self-contained.zip` ← just the `.exe` from `publish-sc/`
- `OpenCodeWebTray-<version>-win-x64-framework-dependent.zip` ← just the `.exe` from `publish-fdd/` (`PublishSingleFile` embeds `runtimeconfig.json`/`deps.json`/managed assemblies; **drop the `.pdb`**)

Attach both zips to a GitHub Release tagged `v<version>` (e.g. `v1.0.0`).

## Smoke-testing the tray
Launching the exe starts a real `opencode web --port 4096`. Before testing, check `Get-NetTCPConnection -LocalPort 4096 -State Listen`; if something is listening, clean up only by exact PID (see safety rule). If 4096 is taken, opencode exits within ~5 s and the tray shows a port-conflict balloon — that path is expected, not a crash.

## Notes
- Single-instance enforced via a global `Mutex`.
- Port/args are constants in `TrayApplicationContext` (`OpencodeUrl`, `OpencodeArgs`); changing the port means updating both.
- Embedded icon resources load by manifest name `<RootNamespace>.<dotted path>` (e.g. `OpenCodeWebTray.Assets.opencode.ico`); `<ApplicationIcon>` additionally stamps the exe icon.
