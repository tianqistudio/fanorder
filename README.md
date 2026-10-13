# 风序 · FanOrder

<img src="installer/assets/FanOrder.svg" width="88" alt="FanOrder 图标">

适用于 Windows 的 V12 Ultra 风扇控制工具，提供温控曲线、手动调速、灯光预设与托盘快捷操作。

<a id="功能"></a>

## 主要功能

- 自动温控、手动与纯手动模式，支持曲线和方案管理。
- 风扇开关、转速调整、灯光预设及状态回读。
- 托盘操作、全局快捷键、登录后启动到托盘。
- 本地运行历史、趋势查看与曲线建议。
- 简体中文与 English 界面，Windows Fluent 深色与经典深色外观。
- 独立后台服务负责设备访问，设置界面和 CLI 通过本地命名管道通信。

## 平台与设备

当前目标为 Windows x64、.NET 10，仅适配 V12 Ultra。内部程序、服务与命名管道继续使用 `V12UltraController` 标识，以保持安装兼容性。

硬件接管需要显式配对和验证。温度监控依赖 LibreHardwareMonitor；目标机器可能需要单独安装 PawnIO。仓库不附带厂商软件、固件或驱动安装包。

<a id="下载与安装"></a>

## 安装与使用

Windows x64 安装程序见 [0.1.25 预发布版](https://github.com/tianqistudio/fanorder/releases/tag/v0.1.25-preview.20261013)。安装包包含 .NET 10 运行时，首次安装需要另行安装官方 PawnIO 2.2.0。

此安装包基于 0.1.25，不包含后续 0.1.26 界面补丁及当前源码的可靠性改进；下列构建命令针对当前源码。

<a id="从源码构建"></a>

## 开发与测试

安装与 `global.json` 匹配的 .NET 10 SDK，在 Windows x64 的项目根目录运行：

```powershell
dotnet restore V12UltraController.slnx --locked-mode
dotnet build V12UltraController.slnx -c Release --no-restore
```

离线回归测试：

```powershell
dotnet test `
  tests/V12UltraController.Tests/V12UltraController.Tests.csproj `
  -c Release --no-build
dotnet test `
  tests/TianqiStudio.DesktopShortcuts.Tests/TianqiStudio.DesktopShortcuts.Tests.csproj `
  -c Release --no-build
```

测试采用模拟设备，不向真实设备发送 HID 写入。构建与测试不会安装服务或接管风扇。

## 使用边界

首次运行需完成服务安装与有人看护的设备配对。不要让多个控制程序同时写入同一设备。风扇关闭需要明确确认；关机停扇另有独立资格验证与启用流程，默认不启用。

安装器源码见 [installer](installer/README.md)，当前源码的验证范围见 [源码发布说明](docs/source-release.md)。安装包的版本、校验和及验证边界以对应 Release 为准。

## 项目结构

| 目录 | 内容 |
| --- | --- |
| `src/V12UltraController.Core` | 协议、设备访问与核心模型 |
| `src/V12UltraController.Service` | 控制协调、保护与本地 API |
| `src/V12UltraController.Settings` | WPF 设置界面与托盘 |
| `src/V12UltraController.Cli` | 命令行客户端 |
| `src/TianqiStudio.DesktopShortcuts*` | 快捷键核心与 WPF 适配层 |
| `tests` | 离线回归测试 |
| `installer` | 图标与安装器源码 |

第三方依赖和许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

由 [tianqistudio](https://tianqistudio.com/home) 制作。
