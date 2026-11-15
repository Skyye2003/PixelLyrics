using System.Text;
using PixelLyrics.Core;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"断言失败：{name}");
    passed++;
    Console.WriteLine($"通过：{name}");
}
TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

var multi = LrcParser.Parse("[ti:测试]\n[00:02.25][00:01.5]你好\n[01:02.123]世界");
Check(multi.Document.Lines.Count == 3, "多时间标签展开");
Check(multi.Document.Lines[0].Timestamp == Seconds(1.5), "十分之一秒解析和排序");
Check(multi.Document.Lines[1].Timestamp == Seconds(2.25), "百分之一秒解析");
Check(multi.Document.Lines[2].Timestamp == Seconds(62.123), "分钟与毫秒解析");
Check(multi.Errors.Count == 0, "合法元数据不产生错误");
Check(LrcParser.Parse("[00:01:25]冒号小数").Document.Lines[0].Timestamp == Seconds(1.25), "兼容冒号小数");
Check(LrcParser.Parse("[00:01.1234567]精度").Document.Lines[0].Timestamp.Ticks == 11234567, "保留 ticks 精度");

var positive = LrcParser.Parse("[00:02]甲\n[offset:+500]");
var negative = LrcParser.Parse("[offset:-500]\n[00:02]甲");
Check(positive.Document.Lines[0].Timestamp == Seconds(1.5), "正 LRC offset 提前且全局生效");
Check(negative.Document.Lines[0].Timestamp == Seconds(2.5), "负 LRC offset 延后");
Check(LrcParser.Parse("[offset:2000]\n[00:01]甲").Document.Lines[0].Timestamp == Seconds(-1), "允许 offset 产生负时间");
var bad = LrcParser.Parse("[00:61]坏\n[offset:no]\n[00:xx]坏\n[00:03\n无标签\n[00:04]好\n[00:xx][00:05]保留");
Check(bad.Errors.Count == 6, "收集错误并继续解析");
Check(bad.Errors[0].LineNumber == 1 && bad.Errors[4].LineNumber == 5, "错误包含源行号");
Check(bad.Document.Lines.Count == 2 && bad.Document.Lines[1].Text == "保留", "坏标签不丢弃同一行的合法标签");
Check(LrcParser.Parse("[999999999999999999999:00]坏").Errors.Count == 1, "超大时间不抛异常");
Check(LrcParser.Parse("[offset:9223372036854775807]\n[00:01]好").Errors.Count == 1, "超大 offset 不抛异常");

var same = LrcParser.Parse("[00:02]晚\n[00:01]先\n[00:01]后").Document;
Check(same.Lines[0].Text == "先" && same.Lines[1].Text == "后", "同时间稳定排序");
Check(same.GetLineAt(Seconds(0.9)) is null, "首行之前为空");
Check(same.GetLineAt(Seconds(1))?.Text == "后", "同时间取最后一行");
Check(same.GetLineAt(Seconds(1.9))?.Text == "后", "两行之间保持前行");
Check(same.GetLineAt(Seconds(2))?.Text == "晚", "精确边界切行");
Check(same.GetLineAt(TimeSpan.MaxValue)?.Text == "晚", "末行之后保持");
Check(new LyricsDocument(Array.Empty<LyricLine>()).GetLineAt(TimeSpan.Zero) is null, "空文档查询");
Check(LrcParser.Parse("\uFEFF[00:00]中文歌词").Document.Lines[0].Text == "中文歌词", "UTF 字符串与 BOM");
foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode })
{
    using var stream = new MemoryStream(encoding.GetPreamble().Concat(encoding.GetBytes("[00:00]中文歌词")).ToArray());
    Check(LrcParser.Parse(stream).Document.Lines[0].Text == "中文歌词" && stream.CanRead, $"UTF 流 BOM 检测及保留流：{encoding.WebName}");
}

using (var invalidUtf8 = new MemoryStream(new byte[] { 0x5b, 0x30, 0x30, 0x3a, 0x30, 0x30, 0x5d, 0xff }))
{
    bool rejected = false;
    try { LrcParser.Parse(invalidUtf8); }
    catch (DecoderFallbackException) { rejected = true; }
    Check(rejected, "非法 UTF8 不静默替换为乱码");
}

long ticks = 0;
var clock = new PlaybackClock(() => ticks);
Check(!clock.TryGetPosition(out _), "没有锚点时不可用");
clock.SetAnchor(Seconds(10));
ticks = Seconds(1).Ticks;
Check(clock.TryGetPosition(out var position) && position == Seconds(11), "单调时钟推进播放位置");
Check(clock.SetPaused(true), "有效锚点可暂停");
ticks = Seconds(2).Ticks;
Check(clock.TryGetPosition(out position) && position == Seconds(11), "暂停冻结位置");
Check(clock.SetPaused(false), "有效锚点可恢复");
ticks = Seconds(3).Ticks;
Check(clock.TryGetPosition(out position) && position == Seconds(12), "恢复不计入暂停时间");
ticks = Seconds(5).Ticks;
Check(clock.TryGetPosition(out _), "恰好五秒有效");
ticks++;
Check(!clock.TryGetPosition(out _) && !clock.SetPaused(true), "超过五秒需新锚点且暂停不能续期");
clock.SetAnchor(Seconds(20), true);
ticks += Seconds(5).Ticks + 1;
Check(!clock.TryGetPosition(out _), "暂停锚点也会过期");
clock.SetAnchor(Seconds(30));
Check(clock.TryGetPosition(out position) && position == Seconds(30), "新锚点恢复有效性");
ticks--;
Check(!clock.TryGetPosition(out _), "拒绝倒退的单调时钟");
clock.Reset();
Check(!clock.TryGetPosition(out _), "重置清除锚点");

var songA = new SongIdentity("歌", "歌手", "专辑", Seconds(180));
var songB = new SongIdentity("歌二", "歌手", "专辑", Seconds(180));
Check(songA.Id.Length == 64 && songA.Id == new SongIdentity("歌", "歌手", "专辑", Seconds(180)).Id, "SHA256 标识确定性");
Check(songA.Id != songB.Id && songA.Id != new SongIdentity("歌", "歌手", "专辑", Seconds(181)).Id, "标题和时长参与标识");
Check(songA.Id != new SongIdentity("歌", "其他", "专辑", Seconds(180)).Id && songA.Id != new SongIdentity("歌", "歌手", "其他", Seconds(180)).Id, "歌手和专辑参与标识");
Check(new SongIdentity("ab", "c", "", Seconds(1)).Id != new SongIdentity("a", "bc", "", Seconds(1)).Id, "组合编码无拼接歧义");

var engine = new SyncEngine(clock);
long generationA = engine.ChangeSong(songA);
Check(engine.TryApplyLyrics(generationA, same), "接受当前代歌词");
engine.Settings = new PixelLyricsSettings
{
    GlobalOffset = Seconds(0.5),
    Bindings = new[] { new LyricsBinding(songA.Id, "local.lrc") }
};
Check(engine.Settings.Bindings[0].SongId == songA.Id, "设置支持歌曲绑定");
Check(engine.TrySetAnchor(generationA, Seconds(1.4)), "当前代位置锚点");
Check(engine.GetCurrentLine() is null, "全局正偏移延后首行");
engine.TrySetAnchor(generationA, Seconds(1.5));
Check(engine.GetCurrentLine()?.Text == "后", "全局正偏移边界");
engine.Settings = engine.Settings with { GlobalOffset = Seconds(-0.5) };
engine.TrySetAnchor(generationA, Seconds(0.5));
Check(engine.GetCurrentLine()?.Text == "后", "全局负偏移提前");
long generationB = engine.ChangeSong(songB);
Check(generationB > generationA && engine.Document is null && engine.GetCurrentLine() is null, "切歌递增代数并清空状态");
Check(!engine.TryApplyLyrics(generationA, same), "丢弃旧歌词结果");
Check(!engine.TrySetAnchor(generationA, Seconds(2)), "丢弃旧位置锚点");
Check(engine.TryApplyLyrics(generationB, same), "接受新歌曲歌词");
Check(!engine.TrySetPaused(generationA, true), "旧歌曲暂停事件不污染新歌");
Check(songA == new SongIdentity("歌", "歌手", "专辑", Seconds(180)), "歌曲身份使用值相等比较");
engine.Settings = new();
engine.TrySetAnchor(generationB, Seconds(1), true);
ticks += Seconds(1).Ticks;
Check(engine.GetCurrentLine()?.Text == "后", "引擎暂停保持歌词");
ticks += Seconds(5).Ticks;
Check(engine.GetCurrentLine() is null, "引擎锚点过期清空选择");
Check(engine.ChangeSong(songB) > generationB, "同歌曲重新加载仍增加代数");
long emptyGeneration = engine.ChangeSong(null);
Check(!engine.TryApplyLyrics(emptyGeneration, same), "无歌曲时不接收歌词");
Console.WriteLine($"全部 {passed} 个断言通过。");
