# FanOrder 0.1.30 预发布版

此版本修正编译产物中的机器绝对路径。源码与调试文档采用确定性虚拟路径，快捷键组件更新为 0.1.2；普通构建、发布、NuGet 打包及最终 ZIP 增加静态隐私检查。

控制行为、设备适配范围与 0.1.29 保持一致。应用预览 ZIP 包含 Windows x64 自包含程序、第三方许可和文件校验信息。服务安装、首次设备接管及关机停扇仍遵循原有独立操作流程。

构建使用锁定依赖，回归仅使用模拟设备。未运行安装器、改变系统服务或访问真实散热器。

## English

This release removes machine-specific absolute paths from build artifacts. Source and debug documents use deterministic virtual paths; the shortcut packages are updated to 0.1.2. Static privacy checks cover ordinary builds, publishing, NuGet packages and the final ZIP.

Device behavior and supported hardware remain the same as 0.1.29. The Windows x64 preview ZIP includes self-contained applications, third-party licenses and checksums. Service installation, initial commissioning and shutdown fan-off retain their separate workflows.

Validation uses locked dependencies and simulated devices. No installer, service change or physical-device operation is performed by the build checks.
