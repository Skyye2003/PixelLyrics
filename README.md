# PixelLyrics

Windows WPF 歌词屏应用：读取当前媒体曲目，在用户授权后获取歌词，并通过 PixelBar SDK 输出到 USB 歌词屏。v0.2.0 增加自动识别播放器、网易云音乐接入和运行中保存设置实时生效。网易云进度仍依赖下述组件组合。

界面包含运行、显示和高级设置，支持浅深主题与自适应分栏。默认需要本次联网与输出授权。在高级设置开启「打开应用后自动运行」并保存，即授权后续每次启动自动联网获取歌词及 USB 输出；与「登录后启动到托盘」同时开启可在登录后直接同步。旧配置默认关闭自动运行。

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
dotnet publish PixelLyrics.Probe/PixelLyrics.Probe.csproj -p:PublishProfile=Windows-x64 -r win-x64 --self-contained true -p:Version=0.2.0 --source https://api.nuget.org/v3/index.json -o release/win-x64
```

分发 EXE 时请一并提供 `vendor-pixelbar/LICENSE` 和 `THIRD_PARTY.md`；程序也将 PixelBar 许可证包含在单文件包内。

## 离线检查

```powershell
dotnet run --project PixelLyrics.Probe/PixelLyrics.Probe.csproj -c Release -- --self-test
dotnet run --project PixelLyrics.Tests/PixelLyrics.Tests.csproj -c Release
```

## 网易云音乐（预发布）

自动核对无结果、候选不明确或没有可同步歌词时，可以从运行页「选择歌词来源」搜索 QQ／网易云，或粘贴对应平台完整歌曲链接。Live 候选必须通过具体演出专辑、版本、歌手和时长核验，并由用户确认同一录音；不允许以录音室版或另一场演出替代。成功取词后只记住来源映射，不保存歌词正文。下次播放会重新核验；可通过「更换来源」「忘记此来源」调整。验证范围与操作步骤见 [来源映射验证](docs/source-mapping-validation.md)。

运行页可选择「自动识别」「QQ 音乐」「网易云音乐」，运行中修改后点击「保存设置」即可应用。自动模式在唯一播放来源连续稳定一秒后跟随；两个播放器同时播放时保留已有来源，尚未绑定时提示手动选择并保存。QQ 与网易云绑定分别保存，不跨平台回退。网易云接口客户端内置于程序，无需 Node.js、代理服务、Cookie 或登录凭证。

设置控件只编辑草稿，保存后才更新运行参数；「重新加载」只恢复磁盘值。开始运行采用已保存设置；未开启自动运行时，要求本次联网和 USB 授权。保存时首次开启自动运行会立即开始；关闭该开关不停止当前任务。手动停止后，保存其他参数、设备重连或休眠恢复均不会自行重启任务。保存或切换来源不会解除已有来源访问限制或限流等待。验证情况及测试入口见 [自动识别与实时设置](docs/auto-player-live-settings.md)。

实测组合为网易云 **3.1.41.205564 x64 + BetterNCM 1.3.4 x64 + InfLink-rs 3.4.0**。进度桥接已通过连续播放、暂停恢复、双向拖动、切歌及双播放器检查，详见 [组件实测](docs/netease-plugin-test.md)。匿名接口详情、搜索核验和取词已验证；已按官方网页播放器行为兼容行首 `[00:00.00-1]` 为零秒，其他未经核实的异常标签在重取一次后仍无效时跳过当前曲目，不猜测时间。详见 [接入验证](docs/netease-api-integration.md)。

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

## 请求失败与恢复

网络连接、超时及 HTTP 408/500/502/503/504 按 2 秒、5 秒间隔重试，每个请求最多三次。响应格式或歌词解析失败只重取一次，仍失败则跳过当前曲目，下一首正常处理。不会跨播放器取词或绕过歌曲身份核验。

HTTP 429 按 Retry-After 等待；缺失时依次等待 60、120、240、480、900 秒，到期恢复同一请求。等待按歌词来源隔离，切歌或保存不能绕过。HTTP 401/403 或网易云要求登录时暂停该来源请求，使用「重新尝试」手动恢复；程序不会代为登录。停止或切歌会取消旧请求与等待。

安全事件保存在 %LOCALAPPDATA%/PixelLyrics/Logs/safety.log，最多三个文件，每个约 1 MiB，记录请求阶段、来源、歌曲 ID、重试和失败原因，不记录歌词正文或接口响应。

### 设备与系统状态

运行中未连接歌词屏时等待设备，暂停取词与输出。仅绑定唯一严格匹配的 HID 端点，连接稳定一秒后重新核验当前曲目和进度；设备通知加每五秒检查用于发现断开和重连。写入失败只允许一次重新校验，连续失败后需拔插设备。

停止、正常退出、休眠、注销和关机时尝试切换到已保存的时钟样式。USB 报告写入限时 500 ms，系统结束通知最多等待两秒；发送成功仍需肉眼确认实际显示。突然断电、USB 已断开或操作系统强制结束进程时无法保证回钟。

休眠恢复保留同一次运行的接口限流和访问限制，撤销旧歌词、请求结果与位置绑定，不重播开场。离线测试及本轮尚未完成的实测见 [生命周期验证](docs/lifecycle-validation.md)。
