using System.IO;
using PixelLyrics.Core;
using PixelLyrics.OnlineProbe;

namespace PixelLyrics.Probe;

internal static class SourceMappingSelfTests
{
    internal static void Register(Action<string, Action> check)
    {
        static void Require(bool value) { if (!value) throw new Exception(); }
        SongDetails Candidate(string title = "Song (Live)", string album = "2024 Shanghai Concert", double duration = 240) => new("123", title, ["Artist"], album, duration);
        SongScore Verify(SongDetails details, string album = "2024 Shanghai Concert") => SongMatcher.EvaluateManual(details, "Song (Live)", "Artist", album, 240);
        check("手选来源：同场录音核验通过", () => Require(Verify(Candidate()).Category == MatchCategory.Accepted));
        check("候选排序：可试用优先且同版近时长排前，不放宽门禁", () =>
        {
            var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(240));
            SongScore Trial(string id, string title, string artist, double seconds) => SongMatcher.EvaluateTrial(new(id, title, [artist], "Album", seconds), song.Title, song.Artist, song.Album, 240);
            var blocked = Trial("1", "Song (Live)", "Artist", 240);
            var loose = Trial("2", "Other", "Other", 240);
            var farther = Trial("3", "Song", "Artist", 243);
            var closest = Trial("4", "Song", "Artist", 240);
            var ranked = LyricSourceMappings.RankCandidates([blocked, loose, farther, closest], song);
            Require(ranked.SequenceEqual(new[] { closest, farther, loose, blocked }));
            Require(blocked.Category == MatchCategory.VersionConflict);
            Require(LyricSourceMappings.RankCandidates([], song).Length == 0);
            Require(LyricSourceMappings.RankCandidates([closest, closest with { Details = closest.Details with { Mid = "5" } }], song)[0] == closest);
        });
        check("试用：采茶纪双声道与平台长标题可选", () =>
        {
            foreach (string candidate in new[] { "采茶纪", "徐梦圆/双笙-采茶纪（七人唱见双声道/ShakingHigh Bootleg）", "采茶纪（翻自 双笙）" })
                Require(SongMatcher.EvaluateTrial(Candidate(title: candidate, album: "采茶纪", duration: 207), "采茶纪 (双声道版)", "ShakingHigh/双笙", "采茶纪（七人唱见双声道/ShakingHigh Bootleg）", 206).Category == MatchCategory.Accepted);
        });
        check("试用：仅专辑标记现场也禁止混用", () => Require(SongMatcher.EvaluateTrial(Candidate(title: "Song", album: "Live"), "Song", "Artist", "Studio", 240).Category == MatchCategory.VersionConflict));
        check("试用：名单不完整和缺专辑允许用户试用", () => Require(SongMatcher.EvaluateTrial(Candidate(), "Song (Live)", "Artist / Other", "", 240).Category == MatchCategory.Accepted));
        check("试用：停止完成等待命令且旧曲不可试用", () =>
        {
            var song = new SongIdentity("Song", "Artist", "", TimeSpan.FromSeconds(240));
            var session = new DesktopSession(); var track = new TrackVersion(session.Latest.RunId, 1, song.Id, 1);
            Require(session.QueueSource(new(track, song, MusicPlayer.QQMusic, null, null, Trial: true)).Result == false);
            session.Update(s => s with { Track = track });
            var command = session.QueueSource(new(track, song, MusicPlayer.QQMusic, null, null, Trial: true));
            Require(!command.IsCompleted); session.Stop(); Require(command.Result == false && session.TakeSource() is null);
        });
        check("试用：另一场演出仍禁止", () => Require(SongMatcher.EvaluateTrial(Candidate(album: "2023 Beijing Concert"), "Song (Live)", "Artist", "2024 Shanghai Concert", 240).Category == MatchCategory.VersionConflict));
        check("试用：录音室和现场版本不能混用", () => Require(SongMatcher.EvaluateTrial(Candidate(title: "Song"), "Song (Live)", "Artist", "", 240).Category == MatchCategory.VersionConflict));
        check("试用：超时长差和无效时长不能放行", () =>
        {
            Require(SongMatcher.EvaluateTrial(Candidate(duration: 246), "Song (Live)", "Artist", "", 240).Category == MatchCategory.DurationConflict);
            Require(SongMatcher.EvaluateTrial(Candidate(duration: double.NaN), "Song (Live)", "Artist", "", 240).Category == MatchCategory.DurationConflict);
        });
        check("历史：采茶纪时长变化只显示一项且不迁移来源映射", () =>
        {
            var shortSong = new SongIdentity("采茶纪 (双声道版)", "ShakingHigh/双笙", "采茶纪（七人唱见双声道/ShakingHigh Bootleg）", TimeSpan.FromSeconds(200));
            var song = new SongIdentity(shortSong.Title, shortSong.Artist, shortSong.Album, TimeSpan.FromSeconds(206.943));
            var mapping = new LyricSourceMapping(MusicPlayer.NetEaseMusic, "480823627");
            var now = DateTimeOffset.Now;
            SourceHistoryEntry[] records = [new(MusicPlayer.QQMusic, shortSong, null, false, now), new(MusicPlayer.QQMusic, song, mapping, true, now.AddMinutes(-1))];
            var display = SourceHistory.ForDisplay(records);
            Require(display.Count == 1 && display[0].Confirmed && display[0].Song.Id == song.Id);
            Require(records[0].Mapping is null && shortSong.Id != song.Id);
            Require(SourceHistory.ForDisplay(records.Append(new(MusicPlayer.QQMusic, shortSong, mapping, false, now.AddMinutes(1)))).Single().Confirmed == false);
        });
        check("历史：不同播放器歌手专辑和版本不合并", () =>
        {
            var now = DateTimeOffset.Now;
            var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(200));
            SourceHistoryEntry Entry(MusicPlayer player, SongIdentity value) => new(player, value, null, false, now);
            var entries = new[] { Entry(MusicPlayer.QQMusic, song), Entry(MusicPlayer.NetEaseMusic, song),
                Entry(MusicPlayer.QQMusic, new("Song (Live)", "Artist", "Album", song.Duration)),
                Entry(MusicPlayer.QQMusic, new("Song", "Other", "Album", song.Duration)),
                Entry(MusicPlayer.QQMusic, new("Song", "Artist", "Other", song.Duration)) };
            Require(SourceHistory.ForDisplay(entries).Count == 5);
        });
        check("历史：忽略整组保留原始时长与确认记录", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var a = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(200));
                var b = new SongIdentity(a.Title, a.Artist, a.Album, TimeSpan.FromSeconds(206.943));
                SourceHistory.Record(path, MusicPlayer.QQMusic, a, null, false);
                SourceHistory.Record(path, MusicPlayer.QQMusic, b, new(MusicPlayer.NetEaseMusic, "123"), true);
                SourceHistory.Hide(path, [LyricSourceMappings.Key(MusicPlayer.QQMusic, b.Id)]);
                var entries = SourceHistory.Read(path);
                Require(entries.Count == 2 && entries.All(e => e.Hidden) && entries.Single(e => e.Confirmed).Song.Id == b.Id);
                Require(SourceHistory.ForDisplay(entries).Count == 0);
                SourceHistory.Record(path, MusicPlayer.QQMusic, a, null, false);
                Require(SourceHistory.ForDisplay(SourceHistory.Read(path)).Count == 0);
            }
            finally { File.Delete(path + ".sources.json"); }
        });
        check("历史：批量忽略持久化且自动记录不复活", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var a = new SongIdentity("Song A", "Artist", "", TimeSpan.FromSeconds(240));
                var b = new SongIdentity("Song B", "Artist", "", TimeSpan.FromSeconds(240));
                var mapping = new LyricSourceMapping(MusicPlayer.NetEaseMusic, "123");
                SourceHistory.Record(path, MusicPlayer.QQMusic, a, null, false);
                SourceHistory.Record(path, MusicPlayer.NetEaseMusic, b, mapping, true);
                SourceHistory.Hide(path, new[] { LyricSourceMappings.Key(MusicPlayer.QQMusic, a.Id), LyricSourceMappings.Key(MusicPlayer.NetEaseMusic, b.Id) });
                SourceHistory.Record(path, MusicPlayer.QQMusic, a, null, false);
                var entries = SourceHistory.Read(path);
                Require(entries.Count == 2 && entries.All(e => e.Hidden) && entries.Single(e => e.Confirmed).Mapping == mapping);
                SourceHistory.Record(path, MusicPlayer.QQMusic, a, mapping, false);
                Require(!SourceHistory.Read(path).Single(e => e.Song.Id == a.Id).Hidden);
            }
            finally { File.Delete(path + ".sources.json"); }
        });
        check("历史：待确认去重重载确认且不保存歌词", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var song = new SongIdentity("Song", "Artist", "", TimeSpan.FromSeconds(240));
                var mapping = new LyricSourceMapping(MusicPlayer.NetEaseMusic, "123");
                SourceHistory.Record(path, MusicPlayer.QQMusic, song, null, false);
                SourceHistory.Record(path, MusicPlayer.QQMusic, song, mapping, false);
                SourceHistory.Record(path, MusicPlayer.QQMusic, song, null, false);
                var pending = SourceHistory.Read(path);
                Require(pending.Count == 1 && pending[0].Mapping == mapping && !pending[0].Confirmed && pending[0].Song.Id == song.Id);
                SourceHistory.Record(path, MusicPlayer.QQMusic, song, mapping, true);
                SourceHistory.Record(path, MusicPlayer.QQMusic, song, null, false);
                Require(SourceHistory.Read(path).Single().Confirmed);
                Require(!File.ReadAllText(path + ".sources.json").Contains("Document"));
            }
            finally { File.Delete(path + ".sources.json"); }
        });
        check("手选来源：禁止录音室版本", () => Require(Verify(Candidate(title: "Song")).Category != MatchCategory.Accepted));
        check("手选来源：禁止另一场演出", () => Require(Verify(Candidate(album: "2023 Beijing Concert")).Category == MatchCategory.VersionConflict));
        check("手选来源：通用Live专辑不足以确认录音", () => Require(Verify(Candidate(album: "Live"), "Live").Category == MatchCategory.VersionConflict));
        check("手选来源：通用演唱会专辑不足以确认录音", () => Require(Verify(Candidate(album: "Live Concert"), "Live Concert").Category == MatchCategory.VersionConflict));
        check("来源窗口：时长显示使用有效格式", () => Require(TimeSpan.FromSeconds(241).ToString("m':'ss") == "4:01"));
        check("手选来源：录音时长差超过5秒禁止", () => Require(Verify(Candidate(duration: 246)).Category != MatchCategory.Accepted));
        check("手选来源：歌手冲突禁止", () => Require(Verify(Candidate() with { Singers = ["Other"] }).Category == MatchCategory.ArtistConflict));
        const string bootleg = "中华铄金娘（九人唱见双声道/ShakingHigh Bootleg）";
        const string playbackArtists = "洛天依/秋风MusiX/肥皂菌丨珉珉的猫咪丨/熊子";
        SongDetails FullCredits() => new("123", bootleg, ["洛天依", "秋风MusiX", "肥皂菌丨珉珉的猫咪丨", "熊子", "漆柚", "雨期", "喵九", "ShakingHigh"], bootleg, 307);
        SongScore Credits(SongDetails candidate, string? album = bootleg, double duration = 307, string artists = playbackArtists)
            => SongMatcher.EvaluateManual(candidate, bootleg, artists, album, duration);
        check("歌手子集：八人候选与四人媒体名单同版可选", () => Require(Credits(FullCredits()).Category == MatchCategory.Accepted));
        check("歌手子集：不同专辑禁止", () => Require(Credits(FullCredits(), album: "Other").Category == MatchCategory.InvalidMetadata));
        check("歌手子集：缺少专辑禁止", () => Require(Credits(FullCredits(), album: null).Category == MatchCategory.InvalidMetadata));
        check("歌手子集：时长差五秒允许", () => Require(Credits(FullCredits(), duration: 312).Category == MatchCategory.Accepted));
        check("歌手子集：时长差超五秒禁止", () => Require(Credits(FullCredits(), duration: 313).Category == MatchCategory.InvalidMetadata));
        check("歌手子集：不同歌手仍冲突", () => Require(Credits(FullCredits(), artists: "洛天依/Other").Category == MatchCategory.ArtistConflict));
        check("歌手子集：重复歌手不能重复使用候选", () => Require(Credits(FullCredits(), artists: "洛天依/洛天依").Category == MatchCategory.ArtistConflict));
        check("歌手子集：候选少于播放名单不放行", () => Require(Credits(FullCredits() with { Singers = ["洛天依"] }).Category == MatchCategory.ArtistConflict));
        check("歌手子集：不同Bootleg版本禁止", () => Require(Credits(FullCredits() with { Title = "中华铄金娘（另一版 Bootleg）" }).Category == MatchCategory.TitleConflict));
        check("来源链接：平台白名单且不接受任意跳转", () =>
        {
            Require(LyricSourceMappings.ParseLink("https://music.163.com/#/song?id=123", MusicPlayer.NetEaseMusic) == "123");
            Require(LyricSourceMappings.ParseLink("https://y.qq.com/n/ryqq/songDetail/abc123", MusicPlayer.QQMusic) == "abc123");
            foreach (string input in new[] { "https://music.163.com.evil.test/song?id=123", "http://music.163.com/song?id=123", "https://music.163.com:8443/song?id=123", "https://user@music.163.com/song?id=123", "https://music.163.com/song?id=0" })
                Require(LyricSourceMappings.ParseLink(input, MusicPlayer.NetEaseMusic) is null);
        });
        check("来源映射：跨平台持久化且保留旧绑定，不存歌词", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
            try
            {
                var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(240));
                var settings = new ProbeSettings(); settings.SetVerifiedBinding(song, "qq123");
                string key = LyricSourceMappings.Key(MusicPlayer.QQMusic, song.Id);
                settings.SourceMappings[key] = new(MusicPlayer.NetEaseMusic, "123"); settings.Save(path);
                var loaded = ProbeSettings.Load(path);
                Require(loaded.SourceMappings[key] == settings.SourceMappings[key] && loaded.Bindings[song.Id].Mid == "qq123");
                Require(!File.ReadAllText(path).Contains("Document") && !File.ReadAllText(path).Contains("Lines"));
                Require(key != LyricSourceMappings.Key(MusicPlayer.NetEaseMusic, song.Id));
                var session = new DesktopSession(); session.RememberBindings(loaded); session.RememberSource(key, null);
                session.MergeBindings(loaded); Require(!loaded.SourceMappings.ContainsKey(key));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        check("来源选择：停止取消未处理保存且锁止不能选择", () =>
        {
            var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(240));
            var session = new DesktopSession(); var track = new TrackVersion(session.Latest.RunId, 1, song.Id, 1);
            var task = session.QueueSource(new(track, song, MusicPlayer.QQMusic, null, null));
            session.Stop(); Require(!task.GetAwaiter().GetResult() && session.TakeSource() is null);
            session = new DesktopSession(); session.BlockNetwork(session.Latest.RunId);
            Require(!session.QueueSource(new(track, song, MusicPlayer.QQMusic, null, null)).GetAwaiter().GetResult());
        });
        check("音响提醒：连续触发仅显示一次且两秒结束", () =>
        {
            var reminder = new SourceReminder(); Require(reminder.Show(TimeSpan.Zero));
            Require(!reminder.Show(TimeSpan.FromSeconds(2)) && !reminder.Show(TimeSpan.FromSeconds(10)));
            reminder.Reset(); Require(reminder.Show(TimeSpan.FromSeconds(20)));
        });
        foreach (bool conflict in new[] { false, true })
            check("手选映射优先于网易云原曲ID，详情冲突不取词 " + conflict, () =>
            {
                var client = new SourceClient(Candidate(album: conflict ? "2023 Beijing Concert" : "2024 Shanghai Concert"));
                try
                {
                    var loaded = FollowRunner.LoadAsync(new("Song (Live)", "Artist", "2024 Shanghai Concert", TimeSpan.FromSeconds(240)), null,
                        default, testClient: client, player: MusicPlayer.NetEaseMusic, sourceSongId: "999",
                        mapping: new(MusicPlayer.QQMusic, "123")).GetAwaiter().GetResult();
                    Require(!conflict && loaded.Mid == "123" && client.DetailsId == "123" && client.Fetches == 1);
                }
                catch (FollowRunner.MatchUnavailableException) { Require(conflict && client.Fetches == 0); }
            });
        check("来源选择：切歌保留原曲身份，旧结果不能直接应用", () =>
        {
            var song = new SongIdentity("Song", "Artist", "Album", TimeSpan.FromSeconds(240));
            var session = new DesktopSession(); var original = new TrackVersion(session.Latest.RunId, 1, song.Id, 1);
            session.Update(s => s with { Track = original });
            var attempt = session.BeginAttempt(original);
            session.Update(s => s with { Track = original with { Generation = 2, SongId = "different" } });
            var task = session.QueueSource(new(original, song, MusicPlayer.QQMusic, new(MusicPlayer.NetEaseMusic, "123"), null));
            var choice = session.TakeSource();
            Require(choice?.Song == song && choice.Track != session.Latest.Track && !session.CanApply(attempt));
            session.FinishSource(true); Require(task.GetAwaiter().GetResult());
        });
    }

    private sealed class SourceClient(SongDetails details) : ILyricsSource
    {
        internal string? DetailsId;
        internal int Fetches;
        public Task<SongSelection> RecallAsync(string title, string artist, string? album, double seconds, CancellationToken token = default,
            Action? ensureNetworkAllowed = null, string? requiredMid = null, string? artistAlias = null, Func<Task>? beforeRequest = null) => throw new Exception("不应搜索");
        public Task<SongDetails> GetDetailsAsync(string id, CancellationToken cancellationToken = default) { DetailsId = id; return Task.FromResult(details); }
        public Task<VerifiedLyrics> FetchAsync(string id, CancellationToken cancellationToken = default)
        { Fetches++; return Task.FromResult(new VerifiedLyrics("[00:01]fixture", LrcParser.Parse("[00:01]fixture").Document, new Dictionary<string, string>())); }
        public void Dispose() { }
    }
}
