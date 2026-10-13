# FanOrder 0.1.28 预发布版

## 中文

补齐中英文界面、安装器和 README。语言选择位于「设置 → 常规」，即时生效并保存；方案名称、输入内容与设备数值保持原样。

- 翻译方案、灯光、历史分析、托盘、快捷键、设备维护及恢复提示，覆盖动态数值和延迟生成的下拉选项。
- 安装与卸载页面、快捷方式选项及维护提示支持中英文。安装器语言与程序界面语言分别保存。
- 新增完整 [English README](../README.en.md)，安装包包含两种语言的说明。
- 保留 0.1.27 的具体设备适配说明：仅适配绿巨能（llano）V12Ultra（原 V16）AI 智控款，京东商品编号 100211395916；V11、V11 Pro、普通 V12 及其他型号不在范围内。

Windows 10/11 x64（build 17763 或更新版本）；内置 .NET 10 运行时，官方 PawnIO 2.2.0 仍需另行安装。升级前退出托盘与 CLI。首次设备接管和关机停扇仍需各自的明确确认与验证。

本版验证包括锁定依赖构建、模拟设备回归、双语合成界面检查、原生提示窗口生命周期检查，以及安装器 CRC 和嵌入载荷 SHA-256 校验。未运行真实安装、升级、服务接管或实机风扇操作。

## English

Completes Chinese and English support across the interface, installer and README. Choose the interface language under **Settings → General**; it applies immediately and is saved. Profile names, input and device values remain unchanged.

- Covers profiles, lighting, history analysis, tray controls, shortcuts, maintenance and recovery messages, including dynamic values and lazily generated dropdown labels.
- Installation, uninstallation, shortcut options and maintenance messages support both languages. Installer and application language preferences are independent.
- Adds a complete English README and includes both READMEs in the installer.
- Retains the precise device scope from 0.1.27: the llano V12Ultra (formerly V16) AI-controlled model, JD item 100211395916. V11, V11 Pro, ordinary V12 and other models are unsupported.

Requires Windows 10/11 x64, build 17763 or later. The .NET 10 runtime is included; install official PawnIO 2.2.0 separately. Exit the tray app and CLI before upgrading. Commissioning and shutdown fan-off still require their separate explicit confirmations and verification.

Validation covers locked-dependency builds, simulated-device regression tests, bilingual synthetic UI checks, native tooltip lifecycle checks, installer CRC and embedded-payload SHA-256 checks. Real installation, upgrade, service commissioning and physical fan operations were not performed.
