using System.Globalization;

namespace PixelLyrics.Probe;

internal sealed record ProbeOptions(bool Watch, string? LrcPath, bool TestScreen, bool Send, bool Help,
    int? DurationSeconds, int OffsetMs, bool DesktopLyrics, bool ScreenSequence, bool Accessibility,
    bool Online, string? SongMid, string? ArtistAlias,
    bool Follow = false, string? SettingsPath = null, bool SaveSettings = false, bool HasOffset = false,
    MusicPlayer Player = MusicPlayer.QQMusic, bool HasPlayer = false, PlayerMode Mode = PlayerMode.QQMusic)
{
    public static ProbeOptions Parse(string[] args)
    {
        bool watch = false, test = false, send = false, help = false;
        bool desktopLyrics = false, screenSequence = false, accessibility = false;
        bool online = false, follow = false, saveSettings = false;
        string? songMid = null, artistAlias = null, settingsPath = null;
        string? path = null;
        int? duration = null;
        int offset = 0;
        bool hasOffset = false;
        bool hasPlayer = false;
        MusicPlayer player = MusicPlayer.QQMusic;
        PlayerMode mode = PlayerMode.QQMusic;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help": case "-h": help = true; break;
                case "--player":
                    if (hasPlayer || ++i >= args.Length) throw new ArgumentException("--player 必须指定一次 auto、qqmusic 或 netease。");
                    mode = args[i] switch { "auto" => PlayerMode.Auto, "qqmusic" => PlayerMode.QQMusic, "netease" => PlayerMode.NetEaseMusic, _ => throw new ArgumentException("播放器无效。") };
                    player = mode == PlayerMode.NetEaseMusic ? MusicPlayer.NetEaseMusic : MusicPlayer.QQMusic;
                    hasPlayer = true;
                    break;
                case "--watch": watch = true; break;
                case "--online":
                    if (online) throw new ArgumentException("--online 不可重复。");
                    online = true;
                    break;
                case "--follow":
                    if (follow) throw new ArgumentException("--follow 不可重复。");
                    follow = true;
                    break;
                case "--save-settings":
                    if (saveSettings) throw new ArgumentException("--save-settings 不可重复。");
                    saveSettings = true;
                    break;
                case "--settings":
                    if (settingsPath is not null || ++i >= args.Length
                        || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                        throw new ArgumentException("--settings 必须指定一次非空本地文件路径。");
                    settingsPath = args[i];
                    break;
                case "--songmid": case "--artist-alias":
                    string key = args[i];
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                        throw new ArgumentException(key + " 必须指定非空值。");
                    if (key == "--songmid" && songMid is null) songMid = args[i];
                    else if (key == "--artist-alias" && artistAlias is null) artistAlias = args[i];
                    else throw new ArgumentException(key + " 不可重复。");
                    break;
                case "--test-screen": test = true; break;
                case "--send": send = true; break;
                case "--desktop-lyrics": desktopLyrics = true; break;
                case "--accessibility": accessibility = true; break;
                case "--screen-sequence": screenSequence = true; break;
                case "--duration":
                    if (duration is not null || ++i >= args.Length
                        || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
                        throw new ArgumentException("--duration 必须指定一次正整数秒数。");
                    duration = seconds;
                    watch = true;
                    break;
                case "--offset-ms":
                    if (hasOffset || ++i >= args.Length
                        || !int.TryParse(args[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offset)
                        || offset < -5000 || offset > 5000)
                        throw new ArgumentException("--offset-ms 必须指定一次 -5000..5000 的整数毫秒数。");
                    hasOffset = true;
                    break;
                case "--lrc":
                    if (path is not null || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("--lrc 必须指定且只能指定一个本地文件路径。");
                    path = args[i];
                    break;
                default: throw new ArgumentException($"未知参数：{args[i]}；使用 --help 查看说明。");
            }
        }
        if (!help && mode != PlayerMode.QQMusic && (songMid is not null || artistAlias is not null || accessibility || desktopLyrics))
            throw new ArgumentException("网易云不支持 QQ MID、别名或 QQ 窗口诊断参数。");
        if (!help && follow && (!online || songMid is not null || artistAlias is not null))
            throw new ArgumentException("--follow 必须与 --online 组合，且不可使用 --songmid 或 --artist-alias 启动别名。");
        if (!help && saveSettings && settingsPath is null)
            throw new ArgumentException("--save-settings 必须显式指定 --settings <本地路径>。");
        if (!help && accessibility && (desktopLyrics || test || send || screenSequence || path is not null || hasOffset))
            throw new ArgumentException("--accessibility 为独立只读 UI Automation 探测，仅可与 --watch、--duration 组合。");
        if (!help && screenSequence && (test || send))
            throw new ArgumentException("--screen-sequence 与 --test-screen、--send 互斥。");
        if (!help && desktopLyrics && (test || send || screenSequence || path is not null))
            throw new ArgumentException("--desktop-lyrics 为只读窗口诊断，不可与写屏模式或 --lrc 组合。");
        if (!help && online && (path is not null || accessibility || desktopLyrics || test || screenSequence))
            throw new ArgumentException("--online 不可与 --lrc、诊断或屏幕测试组合。");
        if (!help && !online && (songMid is not null || artistAlias is not null))
            throw new ArgumentException("--songmid、--artist-alias 仅可用于 --online。");
        if (!help && send && path is null && !online) throw new ArgumentException("--send 必须与 --lrc 或 --online 组合使用。");
        if (!help && test && send) throw new ArgumentException("--test-screen 与 --send 请分开运行。");
        return new(watch, path, test, send, help, duration, offset, desktopLyrics, screenSequence, accessibility,
            online, songMid, artistAlias, follow, settingsPath, saveSettings, hasOffset, player, hasPlayer, mode);
    }

    public const string HelpText = """
        PixelLyrics.Probe — P0 控制台原型（不控制播放，不读取缓存或 QRC）
        无参数 / --gui：打开设置界面；--tray：独立参数，仅启动至托盘，不联网、不写屏、不开始歌词。
        界面登录自启默认关闭，保存全部设置时应用；开关只存于当前用户 Run 的 PixelLyrics 项，不存 JSON。
        自启仅在当前用户登录时启动托盘，无需管理员权限，不代表联网或同步授权。
        其他探测参数：枚举 SDK 屏幕端点、GSMTC 媒体会话，读取所选播放器唯一会话的曲目与时间线。
        --player <值>       auto、qqmusic 或 netease；未指定时读取配置，旧配置默认 QQ 音乐。auto 按播放状态选择，来源稳定一秒后切换。
        --watch             每 250ms 输出位置与状态；单独使用不向 USB 写屏。
        --lrc <本地路径>    导入 LRC，只在内存绑定启动时所选播放器唯一会话的当前曲目，持续预览。
        --duration <N>     正整数秒数，自动启用 watch，到时退出，无需 Ctrl+C。
        --offset-ms <毫秒>  -5000..5000，默认 0；正值延后歌词（查询位置减去偏移）。
        --online            授权所选来源：QQ 使用 c.y.qq.com / u.y.qq.com；网易云使用 music.163.com 查询详情和普通 LRC。
                            网易云可使用进度组件提供的 NCM 歌曲 ID；缺失时搜索并核验，不回退到 QQ。
                            无 Cookie/凭据/代理/重定向，10秒期限、不重试；HTTP/业务码异常立即停止。
                            唯一 GSMTC 会话，歌名/歌手/专辑 NFKC 精确核对、时长容差2秒；多个匹配拒绝。
                            默认仅终端预览，不保存歌词文件；与 lrc/诊断/屏幕测试互斥。
        --follow            必须配合 --online 跟随切歌；不可与 --songmid、--artist-alias 组合。
        --settings <路径>   显式读取本地 JSON；文件不存在使用空配置，不创建个人目录。
        --save-settings     必须配合 --settings；启动时仅保存显式 offset，验证成功后才保存 MID/别名。
                            不保存歌词或联网授权，父目录必须已存在；未指定 offset 不写默认偏移。
        --songmid <MID>     仅 online：指定候选 MID，仍必须核对完整详情。
        --artist-alias <值> 仅 online：本次启动单曲的显式歌手别名；不剥括号、不用于后续曲目。
        --send              配合 --lrc 或 --online 才显式启用 USB 歌词输出，每200ms检查并去重；暂停不写歌词。
        --test-screen       显式发送一次固定测试文字；必须肉眼确认，不代表 P0 验证通过。
        --desktop-lyrics    只读枚举 QQMusic 进程的所有顶层窗口，仅输出类名/可见性/标题（最多256字符）。
                            配合 --duration 每250ms轮询，只输出新窗口或标题变化；不读取其他程序、不写屏。
                            窗口标题仅作诊断，普通主窗口标题不是已验证歌词。
        --accessibility     独立只读 UI Automation 探测，默认关闭；仅限 QQMusic 标题精确为“桌面歌词”的顶层窗口。
                            全次最多150元素、根深度0至8；仅读 Name、ControlType、TextPattern 可见文本，文本最多256字符。
                            即使配合 --watch/--duration 也仅采样一次；只输出终端，不落日志、不OCR、不写屏、不发送输入。
                            UIA 同步调用不能由 duration 强制中断；输出仅作诊断，不代表已验证歌词。
        --screen-sequence   与 test/send 互斥，唯一严格设备依次显示“第一行 测试”3秒、
                            “第二行 测试”3秒并清除；报告调用结果，需用户肉眼确认，不改灯光音量。
        --help / -h         显示本说明。
        手动模式仅匹配所选播放器；自动模式按播放状态选择，多个同来源会话拒绝同步。
        只允许唯一 VID=2D99/PID=A106/col02 且 MI_04、UsagePage=FF24/FF14、Usage=1、64 字节报告端点写入。
        按完整 SongIdentity.Id 绑定；默认切歌或会话变化后失效，--follow 才重新验证后续歌曲。
        属性 revision 仅检查快照一致性；不稳定时暂停显示，同曲目恢复稳定后可继续。
        时间线 age 超过 5 秒标记 stale 且不显示歌词；本轮保留限制观察真实时间线。
        暂停位置变化会更新终端预览，但暂停时不发送歌词。
        未绑定时用空白文字清除本程序已发送的旧歌词；暂停期间不发送歌词。
        USB 异常后本次进程停止输出，不重试、不切换设备；test/sequence/send 写屏故障退出码为 5。
        无可用严格匹配端点退出码为 3；请关闭其他设备控制程序。
        duration 限制采样循环，不保证打断 SDK 同步 I/O；同步阻塞风险本轮未修复。
        文本按完整字素限制为 SDK 允许的 UTF-8 字节数，固件字库/显示效果需肉眼确认。
        Ctrl+C 退出；仅显式 --save-settings 保存偏移和验证后的身份/MID/别名，不保存歌词或联网授权。
        不主动创建日志。终端重定向/历史记录由调用方管理。
        示例：PixelLyrics.Probe --watch
              PixelLyrics.Probe --lrc "C:/Lyrics/song.lrc"
              PixelLyrics.Probe --send --lrc "C:/Lyrics/song.lrc"
        """;
}
