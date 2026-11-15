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
                LogLine l = Formats.ParseLog(part);
                logLines.Add(l);
                if (LogShown(l)) logView.Add(l);
            }
            if (logLines.Count > 3000) { logLines.RemoveRange(0, 1000); FilterLog(); return; }
            ListBox list = E<ListBox>("LogList");
            if (list.IsVisible && logView.Count > 0) list.ScrollIntoView(logView[logView.Count - 1]);
            ShowEmpty("LogEmpty", logView.Count == 0, "", "Ничего не найдено", "Снимите фильтр или измените запрос");
        }

        void FilterLog()
        {
            logView.Clear();
            foreach (LogLine l in logLines) if (LogShown(l)) logView.Add(l);
            ShowEmpty("LogEmpty", logView.Count == 0, "", "Ничего не найдено", "Снимите фильтр или измените запрос");
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

        // Прошлый запуск упал (см. Report.SaveCrash): один раз предлагаем открыть заявку с текстом ошибки.
        // Окно скрыто (автозапуск) — спросим, когда его откроют
        string pendingCrash;

        void CheckCrash()
        {
            try
            {
                if (!File.Exists(Report.CrashFile)) return;
                pendingCrash = File.ReadAllText(Report.CrashFile);
                File.Copy(Report.CrashFile, Report.CrashOld, true);
                File.Delete(Report.CrashFile);
            }
            catch (Exception e) { Log.Write("Отчёт о падении: " + e.Message); return; }
            Log.Write("Прошлый запуск лаунчера закончился аварийно");
            if (window.IsVisible) ShowCrashOffer();
        }

        void ShowCrashOffer()
        {
            string text = pendingCrash;
            pendingCrash = null;
            if (text == null || Ask("Прошлый запуск лаунчера закончился аварийно. Сообщить разработчику?\n\nОткроется заявка на GitHub с текстом ошибки — без ключей и адресов серверов. Перед отправкой её можно проверить.",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            string url = Report.IssueUrl(Consts.LauncherRepo, Consts.Version, Report.IssueSystem(cfg), T("Лаунчер аварийно закрылся") + ":\n\n" + Report.CrashSummary(text));
            // через explorer, чтобы браузер открылся без прав администратора
            try { Process.Start("explorer.exe", "\"" + url + "\""); } catch (Exception e) { Log.Write("Не удалось открыть браузер: " + e.Message); }
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
                else if (action == "vpn-page") E<RadioButton>("NavVpn").IsChecked = true;
                else if (action == "revert") await RevertStrategy();
            }));
        }
    }
}
