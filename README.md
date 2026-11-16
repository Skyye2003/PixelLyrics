# PixelLyrics

Windows WPF 歌词屏应用：读取当前媒体曲目，在用户授权后获取歌词，并通过 PixelBar SDK 输出到 USB 歌词屏。v0.2.0-rc.1 预发布版增加自动识别播放器、网易云音乐接入和运行中保存设置实时生效。网易云进度仍依赖下述组件组合。

界面包含运行、显示和高级设置，支持浅深主题与自适应分栏。联网与输出需要用户明确授权，启动应用不会自动开始歌词同步。

## 构建与运行

需要 Windows 和 .NET 10 SDK（包含 Windows Desktop 支持）。在项目根目录执行：

```powershell
dotnet build PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release
.\Start-PixelLyrics.cmd
```

仓库只保留 `Start-PixelLyrics.cmd` 一个启动入口，优先打开 `release/win-x64/PixelLyrics.exe`，否则打开 Release 构建产物。仓库不包含 .NET SDK、编译产物或个人运行配置。可将本地 .NET 安装放在 `.tools/dotnet`，应用也支持系统安装的运行时。下载用户直接运行 EXE。

## 下载与打包

在 GitHub Releases 下载 Windows x64 EXE，双击即可运行，无需另装 .NET。首次运行会将内置运行时解压到用户临时目录。尚未进行代码签名，Windows 可能提示未知发布者。私有仓库的下载需要拥有仓库访问权限。

重新打包（Windows，.NET 10 SDK）：

```powershell
dotnet publish PixelLyrics.Probe/PixelLyrics.Probe.csproj -p:PublishProfile=Windows-x64 -r win-x64 --self-contained true -p:Version=0.2.0-rc.1 --source https://api.nuget.org/v3/index.json -o release/win-x64
```

分发 EXE 时请一并提供 `vendor-pixelbar/LICENSE` 和 `THIRD_PARTY.md`；程序也将 PixelBar 许可证包含在单文件包内。

## 离线检查

```powershell
dotnet run --project PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release -- --self-test
dotnet run --project PixelLyrics.Tests/PixelLyrics.Tests.csproj -c Release
```

## 网易云音乐（预发布）

运行页可选择「自动识别」「QQ 音乐」「网易云音乐」，运行中修改后点击「保存设置」即可应用。自动模式在唯一播放来源连续稳定一秒后跟随；两个播放器同时播放时保留已有来源，尚未绑定时提示手动选择并保存。QQ 与网易云绑定分别保存，不跨平台回退。网易云接口客户端内置于程序，无需 Node.js、代理服务、Cookie 或登录凭证。

设置控件只编辑草稿，保存后才更新运行参数；「重新加载」只恢复磁盘值。开始运行采用已保存设置，并单独要求本次联网和 USB 授权。保存或切换来源不会解除已有联网锁止，也不会启动已停止的任务。验证情况及测试入口见 [自动识别与实时设置](docs/auto-player-live-settings.md)。

实测组合为网易云 **3.1.41.205564 x64 + BetterNCM 1.3.4 x64 + InfLink-rs 3.4.0**。进度桥接已通过连续播放、暂停恢复、双向拖动、切歌及双播放器检查，详见 [组件实测](docs/netease-plugin-test.md)。匿名接口详情、搜索核验和取词已验证；已按官方网页播放器行为兼容行首 `[00:00.00-1]` 为零秒，其他未经核实的异常标签仍显示错误并锁止本次后续联网。详见 [接入验证](docs/netease-api-integration.md)。

仅验证跟随与取词、不写 USB 的命令：

```powershell
dotnet run --project PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release -- --online --follow --player netease --duration 30
```

v0.2.0-rc.2 提供 [集成安装包](docs/bundled-installer.md)：Setup 内置主程序、BetterNCM、InfLink-rs 及 VC++ 离线运行库，通过可选组件一次安装，并修复桌面快捷方式及安装程序图标。当前为预发布版；干净环境安装和第三方对应源码材料完整性审核未完成，不标记为正式版。

### 原生兼容性诊断

本机无插件网易云 3.1.33.205244 连续播放实测未提供有效进度和总时长，未通过原生同步门槛。之后经用户授权转向上述组件方案。详见 [兼容性实测](docs/netease-native-compatibility.md)。原生诊断结论仅适用于记录中的版本与环境。

在 Windows 交互桌面打开网易云并播放歌曲，然后运行：

```powershell
dotnet run --project PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release -- --diagnose-netease
```

每轮约 65 秒，仅通过 Windows 媒体会话读取客户端版本、会话标识、歌名、歌手、专辑及时间线，向标准输出写入 JSONL。不会联网取词、访问 USB、保存配置或修改播放器。日志包含曲目信息，分享前请检查。Ctrl+C 可取消。

先连续播放至少 60 秒，再分轮记录暂停与恢复、向前与向后拖动、切歌，以及 QQ 音乐同时打开的场景，并记下操作时间及播放器实际显示的进度。诊断不能判断客户端是否安装插件，验证时需使用无插件客户端。

退出码 0 仅表示采到可用时间线，不代表通过全部兼容性验收；2 表示无可用样本或采样未完成；3 表示会话访问失败，需在交互桌面重试，不能据此认定客户端不兼容。缺少会话也应先确认播放器确实正在播放。安装组件后的结果不能视为免插件原生兼容性通过。

## 目录

- `PixelLyrics.Core`：歌词解析与同步基础逻辑。
- `PixelLyrics.OnlineProbe`：联网歌词获取与曲目匹配。
- `PixelLyrics.Probe`：WPF 桌面界面、会话控制与设备输出。
- `PixelLyrics.Tests`：离线测试。
- `vendor-pixelbar`：构建所需的第三方 PixelBar SDK 源码及许可证，详见 THIRD_PARTY.md。
