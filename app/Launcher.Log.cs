// Журнал, диалоги и уведомления
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ZapretLauncher
{
    // Строка журнала на странице «Журнал»: время, текст и вид — err / trace / warn / ok / обычная
    public class LogLine
    {
        public string Raw { get; set; }
        public string Time { get; set; }
        public string Text { get; set; }
        public string Kind { get; set; }
    }

    partial class Launcher
    {
        // ---------- Журнал ----------
        // В памяти — последние строки; на странице — с фильтром «только важное» и поиском, новые строки дописываются на лету
        readonly List<LogLine> logLines = new List<LogLine>();
        readonly System.Collections.ObjectModel.ObservableCollection<LogLine> logView = new System.Collections.ObjectModel.ObservableCollection<LogLine>();
        static readonly string[] LogErrors = { "ошибка", "Ошибка", "Не удалось", "не удалось", "Exception", "не работает", "упал", "остановилось",
                                               "error", "Error", "failed", "Could not", "Couldn't", "isn't working", "stopped right" };
        static readonly string[] LogWarnings = { "Сторож", "Внимание", "не отвечает", "не открывается", "перестала", "блокирует",
                                                 "Watchdog", "Warning", "not responding", "doesn't open", "stopped working", "blocks" };
        static readonly string[] LogGood = { "Запущена стратегия", "Лучшая стратегия", "Новая стратегия", "VPN подключён", "обновлён до",
                                             "Started strategy", "Best strategy", "New strategy", "VPN connected", "updated to" };

        static LogLine ParseLog(string line)
        {
            LogLine l = new LogLine { Raw = line, Time = "", Text = line, Kind = "" };
            if (line.StartsWith("[") && line.IndexOf("] ") > 0)
            {
                int end = line.IndexOf("] ");
                l.Time = line.Substring(1, end - 1);
                l.Text = line.Substring(end + 2);
            }
            else if (line.StartsWith("   ") || line.StartsWith("\t")) { l.Kind = "trace"; l.Text = line.Trim(); return l; }   // строки стека после ошибки
            l.Kind = LogErrors.Any(k => l.Text.Contains(k)) ? "err" : LogWarnings.Any(k => l.Text.Contains(k)) ? "warn" : LogGood.Any(k => l.Text.Contains(k)) ? "ok" : "";
            return l;
        }

        bool LogShown(LogLine l)
        {
            string query = E<TextBox>("LogSearch").Text.Trim();
            if (E<ToggleButton>("LogImportant").IsChecked == true && l.Kind != "err" && l.Kind != "warn" && l.Kind != "trace") return false;
            return query.Length == 0 || l.Raw.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void AddLogLine(string text)
        {
            foreach (string part in text.Replace("\r", "").Split('\n'))
            {
                if (part.Trim().Length == 0) continue;
                LogLine l = ParseLog(part);
                logLines.Add(l);
                if (LogShown(l)) logView.Add(l);
            }
            if (logLines.Count > 3000) { logLines.RemoveRange(0, 1000); FilterLog(); return; }
            ListBox list = E<ListBox>("LogList");
            if (list.IsVisible && logView.Count > 0) list.ScrollIntoView(logView[logView.Count - 1]);
            E<FrameworkElement>("LogEmpty").Visibility = logView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void FilterLog()
        {
            logView.Clear();
            foreach (LogLine l in logLines) if (LogShown(l)) logView.Add(l);
            E<FrameworkElement>("LogEmpty").Visibility = logView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (logView.Count > 0) E<ListBox>("LogList").ScrollIntoView(logView[logView.Count - 1]);
        }

        async Task CopyLog()
        {
            Button b = E<Button>("BtnLogCopy");
            try { Clipboard.SetText(string.Join("\r\n", logView.Select(l => l.Raw))); }
            catch (Exception e) { Log.Write("Не удалось скопировать журнал: " + e.Message); return; }
            b.Content = T("Скопировано") + " ✓";
            await Task.Delay(1500);
            b.Content = T("Копировать");
        }

        // Отчёт для разработчика: архив на рабочем столе (Report.cs — что в нём и что из него вычищено), сразу показываем его в Проводнике
        Task SaveReport() { return SaveReport("BtnLogReport", "Собрать отчёт", false); }

        // «Сообщить о проблеме»: тот же отчёт + страница новой заявки на GitHub с заполненными версиями; архив пользователь перетаскивает сам
        async Task ReportProblem()
        {
            if (Ask(T("Лаунчер сохранит отчёт на рабочий стол и откроет в браузере страницу новой заявки на GitHub (нужен аккаунт GitHub).") + "\n\n" +
                    T("Опишите, что не работает, и перетащите архив отчёта в поле «Отчёт».") + "\n\n" +
                    T("Заявки на GitHub видны всем. Ключи VPN, адрес подписки, адреса серверов и имя пользователя Windows из отчёта убраны, но в нём остаются списки сайтов и программ и сведения о сети — архив можно открыть и проверить перед отправкой."),
                    MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            await SaveReport("BtnLogIssue", "Сообщить о проблеме", true);
        }

        async Task SaveReport(string button, string label, bool issue)
        {
            Button b = E<Button>(button);
            b.IsEnabled = false;
            b.Content = T("Собираю…");
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ZapretLauncher-report-" + DateTime.Now.ToString("yyyy-MM-dd-HHmm") + ".zip");
            try
            {
                cfg.Save();
                string system = null;
                await Task.Run(() => { Report.Create(cfg, path); if (issue) system = Report.IssueSystem(cfg); });
                Log.Write("Отчёт сохранён на рабочий стол: " + System.IO.Path.GetFileName(path));
                try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                // через explorer, чтобы браузер открылся без прав администратора
                if (issue)
                    try { Process.Start("explorer.exe", "\"" + Report.IssueUrl(Consts.LauncherRepo, Consts.Version, system) + "\""); }
                    catch (Exception e) { Log.Write("Не удалось открыть браузер: " + e.Message); }
                b.Content = T("Сохранено на рабочий стол") + " ✓";
                await Task.Delay(2500);
            }
            catch (Exception e) { Log.Write("Не удалось собрать отчёт: " + e.Message); Ask(T("Не удалось собрать отчёт: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { b.Content = T(label); b.IsEnabled = true; }
        }

        // ---------- Диалоги и уведомления ----------
        MessageBoxResult Ask(string text, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
        {
            return Dialog.Show(window, T(text), "Zapret Launcher", buttons, icon);
        }

        // Уведомление Windows с кнопками (если окно не на виду), иначе — подсказка в трее; всё пишется в журнал
        void Notify(string text, params string[][] actions)
        {
            Log.Write(text);
            if (window.IsVisible && window.IsActive) return;
            string[][] translated = actions.Select(a => new[] { T(a[0]), a[1] }).ToArray();
            if (cfg.Toasts && Toasts.Show("Zapret Launcher", T(text), translated, OnToastAction)) return;
            if (!window.IsVisible) tray.ShowBalloonTip(5000, "Zapret Launcher", T(text), Forms.ToolTipIcon.Info);
        }

        void OnToastAction(string action)
        {
            window.Dispatcher.BeginInvoke(new Action(async () =>
            {
                ShowWindow();
                if (action == "pick") await StartAutoPickUi(false);
                else if (action == "update") await InstallZapretUi();
                else if (action == "launcher-update") await UpdateLauncherUi(false);
                else if (action == "start") await StartBypassUi();
                else if (action == "rollback") RollbackUi(true);
                else if (action == "vpn") OpenVpnRoutes();
                else if (action == "revert") await RevertStrategy();
            }));
        }
    }
}
