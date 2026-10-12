# TianqiStudio.DesktopShortcuts

可复用的桌面快捷键核心组件。核心库不依赖 WPF、设备、命名管道或具体业务；宿主提供动作清单、配置文件路径和快捷键注册器。

`TianqiStudio.DesktopShortcuts.Wpf` 提供基于 `RegisterHotKey` 的 WPF 全局快捷键适配器。WinForms、WinUI 或其他框架应实现自己的 `IHotkeyRegistrar`，不要把 WPF 适配器当成通用 UI 依赖。

核心行为包括：动作范围校验、全局组合冲突检测、注册新绑定后再保存、保存失败回滚、损坏配置保留、并发修改检测、局部快捷键派发和资源释放。组件只发出动作 ID；业务确认、权限和副作用由宿主处理。

配置文件路径必须由宿主按应用独立提供，例如 `ShortcutStorePaths.ForApplication("publisher", "product")`。不要让多个程序共用同一个快捷键文件。

当前版本 `0.1.0` 与 FanOrder 的原快捷键行为保持兼容，属于项目内可打包组件；发布到公共源前仍需单独完成 API 稳定性、许可证和签名评估。
