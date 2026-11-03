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

            // Antigravity reports one limit per model group; the first group of each length is the main limit.
            var antigravity = new ProviderState { Id = "antigravity", Name = "Antigravity", Plan = "pro", Usage = new Usage { Source = "live", At = now } };
            var agSession = now.AddHours(1).AddMinutes(25);
            var agWeek = now.Date.AddDays(4).AddHours(7);
            antigravity.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Suffix = "Gemini", Short = UsageWindow.ShortFiveHours, Used = 47, ResetsAt = agSession, StartedAt = agSession.AddHours(-5) });
            antigravity.Usage.Windows.Add(new UsageWindow { Label = "Session 5 h", Suffix = "Claude + GPT", Short = "m", Used = 12, ResetsAt = agSession, StartedAt = agSession.AddHours(-5) });
            antigravity.Usage.Windows.Add(new UsageWindow { Label = "Week", Suffix = "Gemini", Short = UsageWindow.ShortWeek, Used = 29, ResetsAt = agWeek, StartedAt = agWeek.AddDays(-7) });
            antigravity.Usage.Windows.Add(new UsageWindow { Label = "Week", Suffix = "Claude + GPT", Short = "m", Used = 8, ResetsAt = agWeek, StartedAt = agWeek.AddDays(-7) });
            s.Providers.Add(antigravity);

            // A credit service: key limit, a monthly budget from provider-reported totals, period spending and today's models.
            var openrouter = new ProviderState { Id = "openrouter", Name = "OpenRouter", Running = true, Usage = new Usage { Source = "live", At = now } };
            var usd = new Money { Currency = "USD" };
            var monthStart = new DateTime(now.Year, now.Month, 1);
            openrouter.Usage.Windows.Add(new UsageWindow { Label = "Key limit", Short = "$", Used = 36.8, Spent = new Money { Used = 18.4, Limit = 50, Currency = "USD" } });
            openrouter.Usage.Windows.Add(new UsageWindow { Label = "Monthly budget", Short = "$", Budget = true, Used = 61, Spent = new Money { Used = 18.3, Limit = 30, Currency = "USD" },
                Amount = 18.3, AmountUnit = "USD", StartedAt = monthStart, ResetsAt = monthStart.AddMonths(1) });
            openrouter.Usage.SpendNote = () => L.F("Spent: today {0} · this week {1} · this month {2}", usd.Format(1.42), usd.Format(6.85), usd.Format(18.3));
            openrouter.Usage.TodayModels = new List<ModelCost>
            {
                new ModelCost { Model = "deepseek/deepseek-v4", Cost = 0.94, Tokens = 1840000, Replies = 37 },
                new ModelCost { Model = "moonshotai/kimi-k3", Cost = 0.48, Tokens = 612000, Replies = 14 },
            };
            openrouter.Sessions.Add(new Session { Kind = "Pi", Title = "Translate the settings strings", Status = "busy", Model = "deepseek/deepseek-v4", Updated = now.AddSeconds(-30) });
            s.Providers.Add(openrouter);

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
