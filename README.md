# OpenCode Web Tray

一个轻量的 Windows 系统托盘工具，用于快捷管理 [opencode](https://opencode.ai) 的 web 后台服务。

## 功能

- 启动时自动在后台运行 `opencode web --port 4096`
- **左键单击**托盘图标 → 在默认浏览器打开 `http://127.0.0.1:4096/`
- **左键双击**托盘图标 → 切换 opencode web 后台进程 开/关
- **右键菜单**：
  - `开启`：启动 opencode web 后台
  - `关闭`：停止 opencode web 后台
  - `Exit`：停止 opencode web 后台并退出本程序
- 图标状态：运行中显示**黑色** opencode 图标，已停止显示**灰色**图标

> 单击与双击互不干扰：单击会延迟到系统双击判定时间之后才触发，若在此期间检测到双击，则仅执行切换（不打开浏览器）。

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
