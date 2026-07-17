# OpenCode Web Tray

一个轻量的 Windows 系统托盘工具，用于快捷管理 [opencode](https://opencode.ai) 的 web 后台服务。

## 功能

- 支持多套独立配置（profile），每套可在右键菜单单独 开启/关闭，端口互不冲突；启动时仅自动启动 `[tray].default` 指定的 profile（留空则不自动启动）
- **左键单击**：无动作（避免误触）；**左键双击**：切换默认 profile 开/关（若未设置 `[tray].default` 则提示）
- **右键菜单**：每个 profile 一块，包含灰显标题（名称+状态）、打开网页、开启/关闭（单按钮，按状态切换文案与动作）；块间以分隔符隔开；末尾 Exit
- 图标状态：任一 profile 运行中显示**黑色** opencode 图标，全停止则显示**灰色**图标

## 配置文件

程序首次启动时会在 exe 同目录生成同名配置文件 `OpenCodeWebTray.ini`：

```ini
; OpenCode Web Tray 配置文件
; 修改后需重启程序才能生效

[tray]
; 启动时自动开启哪个 profile (填 profile 标识, 如 profile.windows)
; 留空 = 启动时不自动开启任何实例, 仅显示托盘
default=profile.windows

; ===== 每个 [profile.名称] 定义一套独立配置 =====
; 名称建议用英文, 不可重复; 各 profile 的 port 不可冲突
; 新增 profile: 复制下面整块、改 [profile.xxx] 名和 port 即可, 无需抄注释

[profile.windows]
; opencode web 监听端口 (1-65535)
port=4096
; 使用哪个环境中的 opencode (留空=Windows原生 / 填发行版名=WSL2)
distro=
; 仅 WSL 模式生效: 运行身份 (默认 root)
user=root
```

- **多 profile 管理**：每个 `[profile.名称]` 节定义一套独立配置，名称建议用英文且不可重复。在右键菜单每个 profile 可单独开启/关闭，端口互不冲突。
- **新增 profile**：复制 `[profile.windows]` 整块，修改节名和 `port` 即可。
- **`[tray].default`**：填入 `profile.名称`（如 `profile.windows`）以指定启动时自动开启哪个 profile；留空则启动时不自动开启任何实例，仅显示托盘。
- **端口冲突**：配置文件中多个 profile 使用相同端口时，对应 profile 无法启动并提示"端口冲突"；启动前还会用 `Get-NetTCPConnection` 探测端口是否已被外部程序占用，被占用时弹出警告并放弃启动。
- **从旧版本升级**：检测到旧版 `[opencode]`/`[WSL]` 格式时自动迁移为 `[profile.windows]`，原值保留，原文件备份为 `OpenCodeWebTray.ini.bak`。
- **WSL 模式**：`distro` 填发行版名（如 `Ubuntu-22.04`，需 WSL2），`user` 指定运行身份（默认 `root`）。非 WSL 模式留空即可。
- **注意**：修改配置后需重启程序才能生效。端口值非法或缺失时自动回退到默认值 `4096`（不会覆盖文件）。

## 依赖

- [opencode](https://opencode.ai) 已安装并在 `PATH` 中（终端执行 `opencode --version` 验证）
- .NET 8.0 Desktop Runtime（开发构建需要 .NET 8 SDK）

## 构建与运行

```bash
dotnet run -c Release
```

或构建可执行文件：

```bash
dotnet build -c Release
# 产物：bin/Release/net8.0-windows/OpenCodeWebTray.exe
```

## 发布为单文件（免运行时依赖）

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 技术栈

- C# / .NET 8 + WinForms
- 托盘图标：运行时从嵌入的 opencode 品牌 `.ico` 切换（黑色 / 灰色，来源 opencode 官方 `web-app-manifest` 图标）

## 许可证

Apache License 2.0，详见 [LICENSE](LICENSE)。
