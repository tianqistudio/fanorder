# TianqiStudio.DesktopShortcuts.Wpf

WPF 专用适配器，为 `TianqiStudio.DesktopShortcuts` 提供隐藏消息窗口和 `RegisterHotKey` 全局快捷键注册。注册器使用 `MOD_NOREPEAT`，释放时注销所有组合并销毁消息窗口。

适用于拥有 WPF Dispatcher 的桌面程序；其他 UI 框架应实现 `IHotkeyRegistrar`，核心库本身不要求 WPF。
