# Settings 离屏验证

使用合成状态、注入的管道客户端、项目内测试偏好和模拟热键注册器，加载真实 WPF XAML 与渲染代码。不会连接真实命名管道、设备或传感器，也不会修改用户实际设置。

```powershell
dotnet restore tools/SettingsPreview/SettingsPreview.csproj --locked-mode
dotnet run --project tools/SettingsPreview -c Release -- --output artifacts/ui-preview/new-run
```

输出目录必须不存在。结果 JSON 包含检查数、失败数、截图数和硬件、管道、原生窗口访问标记。

覆盖断连、保护、纯手动、灯光编辑、历史、方案、快捷键和曲线草稿等界面状态。模拟测试不能替代真实设备、系统快捷键、高 DPI、多显示器或辅助功能验收。
