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

## 下载与打包

在 GitHub Releases 下载 Windows x64 EXE，双击即可运行，无需另装 .NET。首次运行会将内置运行时解压到用户临时目录。尚未进行代码签名，Windows 可能提示未知发布者。私有仓库的下载需要拥有仓库访问权限。

重新打包（Windows，.NET 10 SDK）：

```powershell
dotnet publish PixelLyrics.Probe/PixelLyrics.Probe.csproj -p:PublishProfile=Windows-x64 -r win-x64 --self-contained true -p:Version=0.1.0 --source https://api.nuget.org/v3/index.json -o release/win-x64
```

分发 EXE 时请一并提供 `vendor-pixelbar/LICENSE` 和 `THIRD_PARTY.md`；程序也将 PixelBar 许可证包含在单文件包内。

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
