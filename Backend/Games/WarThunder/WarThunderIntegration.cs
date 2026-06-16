using Serilog;
using System.Text.Json;
using Segra.Backend.Core.Models;
using System.Text.Json.Serialization;

namespace Segra.Backend.Games.WarThunder
{
    internal class WarThunderIntegration : Integration, IDisposable
    {
        private CancellationTokenSource? _cts;
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(2) };
        private int _lastDmgId;
        private string _nickname = string.Empty;

        private const string HudMsgUrl = "http://127.0.0.1:8111/hudmsg?lastEvt=0&lastDmg=";
        private const int PollIntervalMs = 500;

        private static readonly string[] KillVerbs =
        {
            "shot down", "destroyed",
            "abattu", "ha abbattuto", "abgeschossen", "ha derribado a",
            "сбил", "zestrzelił", "sestřelil", "düşürdü", "击落", "撃墜",
            "abateu", "збив", "oborio", "lelőtte", "격추", "збіў",
            "doborât", "擊落", "bắn hạ",
            "détruit", "ha distrutto", "zerstört", "ha destruido",
            "уничтожил", "zniszczył", "zničil", "imha etti", "击毁", "撃破",
            "destruiu", "знищив", "uništio", "megsemmisítette", "격파",
            "знішчыў", "distrus", "擊毀", "đã phá huỷ"
        };

        private static readonly string[] CrashVerbs =
        {
            "has crashed", "s'est écrasé", "si è schiantato", "ist abgestürzt",
            "se ha estrellado", "разбился", "rozbił się", "havaroval",
            "yere çakıldı", "坠毁", "墜落", "sofreu um acidente", "розбився",
            "se srušio", "összetörte", "추락했습니다", "разбіўся", "s-a prăbușit",
            "墜毀", "bị rơi"
        };

        private class HudMsg
        {
            [JsonPropertyName("damage")]
            public List<Damage>? Damage { get; set; }
        }

        private class Damage
        {
            [JsonPropertyName("id")]
            public int Id { get; set; }

            [JsonPropertyName("msg")]
            public string? Msg { get; set; }
        }

        public override Task Start()
        {
            _cts = new CancellationTokenSource();
            Log.Information("[WT] Starting War Thunder integration");
            _ = Task.Run(() => ResolveNickname(_cts.Token));
            _ = Task.Run(() => PollLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public override Task Shutdown()
        {
            Log.Information("[WT] Shutting down War Thunder integration");
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _client.Dispose();
            }
            catch { }
            _cts = null;
            return Task.CompletedTask;
        }

        public void Dispose() => Shutdown().Wait();

        private async Task ResolveNickname(CancellationToken token)
        {
            for (int attempt = 0; attempt < 60 && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    string? folder = ClogDecoder.ResolveClogDirectory(ExePath);
                    if (folder == null)
                    {
                        Log.Debug("[WT] clog directory not found yet, retrying");
                    }
                    else
                    {
                        string? clog = ClogDecoder.GetMostRecentClog(folder);
                        if (clog != null)
                        {
                            string decoded = ClogDecoder.Decode(clog);
                            string nick = ClogDecoder.ExtractNickname(decoded);
                            if (!string.IsNullOrEmpty(nick))
                            {
                                _nickname = nick;
                                Log.Information($"[WT] Resolved nickname: {_nickname}");
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug($"[WT] Nickname resolution attempt {attempt} failed: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(1), token); } catch { return; }
            }

            if (string.IsNullOrEmpty(_nickname))
            {
                Log.Warning("[WT] Could not resolve nickname — no bookmarks will be produced this session.");
            }
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var url = HudMsgUrl + _lastDmgId;
                    string json;
                    try
                    {
                        json = await _client.GetStringAsync(url, token);
                    }
                    catch (HttpRequestException)
                    {
                        try { await Task.Delay(PollIntervalMs, token); } catch { return; }
                        continue;
                    }
                    catch (TaskCanceledException)
                    {
                        if (token.IsCancellationRequested) return;
                        try { await Task.Delay(PollIntervalMs, token); } catch { return; }
                        continue;
                    }

                    HudMsg? payload;
                    try { payload = JsonSerializer.Deserialize<HudMsg>(json); }
                    catch (Exception ex)
                    {
                        Log.Debug($"[WT] Failed to parse /hudmsg payload: {ex.Message}");
                        try { await Task.Delay(PollIntervalMs, token); } catch { return; }
                        continue;
                    }

                    if (payload?.Damage != null)
                    {
                        foreach (var dmg in payload.Damage)
                        {
                            if (dmg.Id > _lastDmgId) _lastDmgId = dmg.Id;
                            ProcessDamage(dmg.Msg);
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Warning($"[WT] Poll error: {ex.Message}");
                }

                try { await Task.Delay(PollIntervalMs, token); } catch { return; }
            }
        }

        private void ProcessDamage(string? msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            if (string.IsNullOrEmpty(_nickname) || !msg.Contains(_nickname))
            {
                return;
            }

            foreach (var verb in CrashVerbs)
            {
                if (msg.Contains(verb))
                {
                    Log.Information($"[WT] Crash detected: {msg.Trim()}");
                    AddBookmark(BookmarkType.Death);
                    return;
                }
            }

            int idx = msg.IndexOf(_nickname, StringComparison.Ordinal);
            bool isKill = idx >= 0 && idx < 3;

            foreach (var verb in KillVerbs)
            {
                if (msg.Contains(verb))
                {
                    var type = isKill ? BookmarkType.Kill : BookmarkType.Death;
                    Log.Information($"[WT] {type} detected: {msg.Trim()}");
                    AddBookmark(type);
                    return;
                }
            }
        }

        private static void AddBookmark(BookmarkType type)
        {
            var recording = AppState.Instance.Recording;
            if (recording == null)
            {
                Log.Debug($"[WT] No recording active, skipping {type} bookmark");
                return;
            }

            var compensation = TimeSpan.FromMilliseconds(PollIntervalMs / 2.0);
            var bookmark = new Bookmark
            {
                Type = type,
                Time = (DateTime.Now - recording.StartTime) - compensation
            };
            recording.AddBookmark(bookmark);
            Log.Information($"[WT] BOOKMARK ADDED: {type} at {bookmark.Time}");
        }
    }
}
