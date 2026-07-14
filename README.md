# OpenCode Web Tray

一个轻量的 Windows 系统托盘工具，用于快捷管理 [opencode](https://opencode.ai) 的 web 后台服务。

## 功能

- 启动时自动在后台运行 `opencode web --port 4096`（端口可在配置文件中修改）
- **左键单击**：无动作（避免误触）；**左键双击**：切换 opencode web 后台 开/关
- **右键菜单**：
  - `打开网页`：在默认浏览器打开 `http://127.0.0.1:<端口>/`
  - `开启`：启动 opencode web 后台
  - `关闭`：停止 opencode web 后台
  - `Exit`：停止 opencode web 后台并退出本程序
- 图标状态：运行中显示**黑色** opencode 图标，已停止显示**灰色**图标

## 配置文件

程序首次启动时会在 exe 同目录生成同名配置文件 `OpenCodeWebTray.ini`：

```ini
; OpenCode Web Tray 配置文件
; 修改后需重启程序才能生效

[opencode]
; opencode web 监听端口 (1-65535)
port=4096
```

修改 `port` 后重启程序即可让 opencode web 监听新端口。端口非法或缺失时自动回退到默认值 `4096`（不会覆盖你手写的配置）。

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
