using System;
using System.Collections.Generic;

namespace AIMeter
{
    /// <summary>
    /// Demo data, invented but realistic: for previewing the app (--demo) and for the README images
    /// (tools\render-media.ps1), without showing real sessions or projects.
    /// </summary>
    internal static class DemoData
    {
        public static Snapshot Build(DateTime now)
        {
            var s = new Snapshot { Taken = now };

            var claude = new ProviderState { Id = "claude", Name = "Claude", Plan = "max", Running = true, CanSignIn = true, Usage = new Usage { Source = "live", At = now } };
            var session = now.AddHours(2).AddMinutes(50);
            claude.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 82, ResetsAt = session, StartedAt = session.AddHours(-5), RunsOutAt = now.AddMinutes(38) });
            claude.Usage.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 41, ResetsAt = now.Date.AddDays(3).AddHours(9), StartedAt = now.Date.AddDays(-4).AddHours(9) });
            claude.Usage.Windows.Add(new UsageWindow { Label = "Week", Suffix = "Opus", Short = "m", Used = 18, ResetsAt = now.Date.AddDays(3).AddHours(9) });
            claude.Usage.Breakdown.Add(new KeyValuePair<string, double>("Claude Code", 91));
            claude.Usage.Breakdown.Add(new KeyValuePair<string, double>("Chat", 6));
            claude.Usage.Breakdown.Add(new KeyValuePair<string, double>("Cowork", 3));
            claude.Sessions.Add(new Session { Kind = "Code · Desktop", Title = "Refactor auth middleware", Status = "busy", Model = "claude-opus-5-5", Updated = now.AddSeconds(-20) });
            claude.Sessions.Add(new Session { Kind = "Code · CLI (WSL)", Title = "Fix flaky CI tests", Status = "idle", Model = "claude-sonnet-5-5", Updated = now.AddMinutes(-4) });
            claude.Sessions.Add(new Session { Kind = "Cowork", Title = "Quarterly report draft", Status = "idle", Model = "claude-sonnet-5-5", Updated = now.AddMinutes(-12) });
            s.Providers.Add(claude);

            var openai = new ProviderState { Id = "openai", Name = "ChatGPT", Plan = "pro", Running = true, Usage = new Usage { Source = "live", At = now } };
            var codex = now.AddHours(3).AddMinutes(40);
            openai.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Short = UsageWindow.ShortFiveHours, Used = 23, ResetsAt = codex, StartedAt = codex.AddHours(-5) });
            openai.Usage.Windows.Add(new UsageWindow { Label = "Week", Short = UsageWindow.ShortWeek, Used = 37, ResetsAt = now.Date.AddDays(5).AddHours(18), StartedAt = now.Date.AddDays(-2).AddHours(18) });
            openai.Sessions.Add(new Session { Kind = "Codex · CLI", Title = "Migrate billing API to v2", Status = "busy", Model = "gpt-5.5-codex", Updated = now.AddSeconds(-45) });
            s.Providers.Add(openai);

            var copilot = new ProviderState { Id = "copilot", Name = "Copilot", Plan = "pro", Usage = new Usage { Source = "live", At = now } };
            var month = new DateTime(now.Year, now.Month, 1).AddMonths(1);
            copilot.Usage.Windows.Add(new UsageWindow { Label = "Premium requests", Short = UsageWindow.ShortMonth, Used = 64, ResetsAt = month, StartedAt = month.AddMonths(-1) });
            s.Providers.Add(copilot);

            var local = new ProviderState { Id = "local", Name = L.T("Local models"), Running = true, GpuTotal = 16L << 30, GpuUsed = (long)(11.4 * (1 << 30)), Models = new List<LocalModel>() };
            var qwen = new LocalModel
            {
                Name = "qwen3-coder:30b", Server = "Ollama", Where = "Windows", Detail = "30.5B · Q4_K_M",
                Vram = (long)(10.9 * (1 << 30)), Ram = (long)(7.7 * (1 << 30)), Context = 32768, ContextUsed = 21400, Busy = 1, Slots = 1,
            };
            qwen.Sessions.Add(new Session { Kind = "OpenCode", Title = "Add WSL port probe", Status = "busy", Model = "qwen3-coder:30b", Updated = now.AddSeconds(-10), Note = L.F("{0} tokens in context", "21K") });
            local.Models.Add(qwen);
            local.Sessions.Add(new Session { Kind = "Pi", Title = "Write release notes", Status = "idle", Model = "claude-sonnet-5-5", Updated = now.AddMinutes(-6), Note = L.F("{0} tokens in context", "12K") });
            s.Providers.Add(local);
            return s;
        }
    }
}
