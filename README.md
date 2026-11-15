# PixelLyrics

Windows WPF 歌词屏应用：读取当前媒体曲目，在用户授权后获取 QQ 音乐歌词，并通过 PixelBar SDK 输出到 USB 歌词屏。

界面包含运行、显示和高级设置，支持浅深主题与自适应分栏。联网与输出需要用户明确授权，启动应用不会自动开始歌词同步。

## 构建与运行

需要 Windows 和 .NET 10 SDK（包含 Windows Desktop 支持）。在项目根目录执行：

```powershell
dotnet build PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release
.\Start-PixelLyrics.cmd
```

启动脚本打开 Release 构建产物。仓库不包含 .NET SDK、编译产物或个人运行配置。可将本地 .NET 安装放在 `.tools/dotnet`，应用也支持系统安装的运行时。

## 离线检查

```powershell
dotnet run --project PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release -- --self-test
dotnet run --project PixelLyrics.Tests/PixelLyrics.Tests.csproj -c Release
```

## 目录

- `PixelLyrics.Core`：歌词解析与同步基础逻辑。
- `PixelLyrics.OnlineProbe`：联网歌词获取与曲目匹配。
- `PixelLyrics.Probe`：WPF 桌面界面、会话控制与设备输出。
- `PixelLyrics.Tests`：离线测试。
- `vendor-pixelbar`：构建所需的第三方 PixelBar SDK 源码及许可证，详见 THIRD_PARTY.md。
