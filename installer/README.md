# FanOrder 安装器源码

`nsis/` 是 Windows x64 NSIS 安装器实现；`Package/` 与 `Bundle/` 保留 WiX 安装器实现和契约测试所需源码。`assets/` 提供产品图标。

Windows x64 安装包见 [GitHub Releases](https://github.com/tianqistudio/fanorder/releases)。源码仓库不附带 NSIS 编译器、厂商软件或驱动。构建解决方案不会运行安装器。

NSIS 打包需要独立准备 NSIS 3.12 工具链，并生成与当前源码匹配的应用候选及校验清单。`build-nsis.ps1` 和 `build-setup.ps1` 会验证候选输入；旧候选不能替代当前源码。WiX 路径另受其许可条件和显式参数约束。

正式安装器提供桌面与开始菜单快捷方式选项。服务、命名管道和安装目录保留 `V12UltraController` 标识。设备接管、关机停扇资格和驱动安装均为独立操作。

安装与卸载会改变系统服务及本机状态；不要将安装器当作普通构建验证步骤运行。自动化测试仅使用模拟设备。
