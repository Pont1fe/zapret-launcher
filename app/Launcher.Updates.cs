// Обновления zapret и лаунчера, откат, «Что нового», резервная копия настроек
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
    partial class Launcher
    {
        // ---------- Что нового ----------
        // Коротко о главном в каждой крупной версии; показывается один раз после обновления
        static readonly Dictionary<string, string[]> WhatsNew = new Dictionary<string, string[]>
        {
            { "4.0.0", new[]
                {
                    "Встроенный VPN: вставьте ключ или подписку на странице VPN",
                    "Через VPN — только выбранные сайты и программы, остальное идёт напрямую",
                    "Пинг серверов, автовыбор самого быстрого и переход на другой, если сервер упал",
                    "Скорость и трафик через VPN — на странице VPN и на главном экране",
                    "Откат лаунчера на прошлую версию — в настройках",
                    "Новый логотип",
                } },
            { "4.0.1", new[]
                {
                    "Сайты, которые провайдер блокирует по адресу, отмечаются отдельно — одна кнопка пускает их через VPN",
                    "Сторож больше не меняет стратегию из-за таких сайтов",
                    "Кнопка «Исправить», если мешает другая программа обхода или чужой драйвер WinDivert",
                    "Журнал соединений по программам — со значками",
                    "Плавные переходы между страницами и вкладками",
                    "Новое приветствие с картинками про VPN и режимы маршрутов",
                } },
            { "4.0.2", new[]
                {
                    "Готовые наборы сайтов в доменах и маршрутах VPN: YouTube, Instagram, Spotify, ChatGPT и другие — одной кнопкой",
                    "Домены можно добавлять сразу списком: вставить несколько строк или загрузить из файла .txt",
                    "Наборы в списках — одной карточкой со значком сервиса, поиск по длинному списку",
                    "Журнал: ошибки и предупреждения выделены цветом, есть поиск, фильтр «Только важное» и кнопка «Копировать»",
                    "Если сторож сменил стратегию — её можно вернуть одной кнопкой; сайты, идущие через VPN, он больше не считает",
                    "Сторож спокойнее: из-за одного сайта ждёт около 8 минут, сначала перезапускает стратегию и не перебирает их чаще раза в полчаса",
                    "Уведомления Windows теперь показываются — в том числе с кнопками",
                } },
            { "4.0.3", new[]
                {
                    "Серверы AmneziaWG, в том числе Amnezia Premium: файл из их приложения добавляется кнопкой «Файл AmneziaWG…» на странице VPN",
                    "С AmneziaWG работает режим «Весь трафик»; «Только выбранное» и «Россия напрямую» с ним — пока экспериментально",
                    "Сторож тише: прежде чем засчитать неудачу, сразу перепроверяет сайт, а разовые сбои не пишет в журнал",
                    "Если выбрать сервер, пока VPN переподключается, выбор больше не теряется; в журнале видно, через какой сервер подключён VPN",
                } },
            { "4.0.4", new[]
                {
                    "«Не пускать мимо VPN» на вкладке «Маршруты»: если VPN упал, сайты и программы из списка не откроются напрямую, пока он не заработает",
                    "Список серверов: работающие — наверху, самые быстрые первыми; неработающие можно скрыть одной кнопкой",
                    "Кнопка «Собрать отчёт» в журнале: журналы и сведения о системе одним архивом на рабочий стол — без ключей VPN и подписки",
                } },
        };

        bool whatsNewPending;

        void ShowWhatsNew()
        {
            whatsNewPending = false;
            string[] items;
            string key = WhatsNew.Keys.FirstOrDefault(v => v == Consts.Version) ?? WhatsNew.Keys.Where(v => !Util.TestNewer(v, Consts.Version)).OrderByDescending(v => new Version(v)).FirstOrDefault();
            if (key != null && WhatsNew.TryGetValue(key, out items) && Util.TestNewer(key, cfg.LastSeenVersion))
                Dialog.Show(window, T("Лаунчер обновлён до версии") + " " + Consts.Version + "\n\n" + string.Join("\n", items.Select(i => "•  " + T(i))),
                    T("Что нового"), MessageBoxButton.OK, MessageBoxImage.Information);
            cfg.LastSeenVersion = Consts.Version;
            cfg.Save();
        }

        // ---------- Откат лаунчера ----------
        // Прошлая версия — из локальной копии (сохраняется при самообновлении) или предыдущий релиз с GitHub
        async Task RollbackLauncherUi()
        {
            Release remote = null;
            string target = SelfUpdate.PreviousLocal();
            if (target == null)
            {
                await RunBusy(async () => { remote = await SelfUpdate.PreviousRemote(cfg.LauncherRepo); }, "Проверка…");
                if (remote == null) { Ask("Не нашёл версию лаунчера старше текущей ни локально, ни на GitHub."); return; }
                target = remote.Version;
            }
            if (Ask(T("Вернуть лаунчер версии") + " " + target + " " + T("вместо") + " " + Consts.Version + "?\n\n" +
                    T("Перед этим настройки сохранятся в резервную копию: старая версия может не знать новых настроек.") + "\n" +
                    T("Автообновление выключится, иначе лаунчер сам обновится обратно. Включите его, когда захотите.") + "\n" +
                    T("Обход продолжит работать, VPN выключится."),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            bool done = false;
            await RunBusy(async () =>
            {
                string backup = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SettingsBackup.AutoFile), "before-rollback-" + Consts.Version + ".zlbackup");
                cfg.AutoUpdate = false;
                cfg.VpnOn = false;
                cfg.Save();
                SettingsBackup.Save(backup);
                await Vpn.Stop();   // старые версии не умеют управлять встроенным VPN
                await SelfUpdate.Rollback(remote);
                Log.Write("Перед откатом настройки сохранены в " + backup);
                done = true;
            }, "Откат…");
            if (done) Restart();   // откат прошёл — запускаем прошлую версию
        }

        // ---------- Резервная копия настроек ----------
        bool backupReady;
        void AutoBackup() { if (backupReady) SettingsBackup.Auto(); }

        bool OfferAutoRestore()
        {
            string created = SettingsBackup.Describe(SettingsBackup.AutoFile);
            if (created == null) return false;
            if (Ask(T("Найдены настройки от прошлой установки лаунчера") + " (" + created + "): " +
                    T("стратегия, домены, избранное, профили сетей.") + "\n\n" + T("Восстановить их?"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
            try { SettingsBackup.Restore(SettingsBackup.AutoFile); }
            catch (Exception e) { Ask(e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            Restart();
            return true;
        }

        void BackupSave()
        {
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = T("Сохранить настройки в файл"), FileName = "ZapretLauncher-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zlbackup",
                Filter = T("Резервная копия Zapret Launcher") + " (*.zlbackup)|*.zlbackup", DefaultExt = ".zlbackup",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(window) != true) return;
            try
            {
                cfg.Save();
                SettingsBackup.Save(dlg.FileName);
                Log.Write("Настройки сохранены в " + dlg.FileName);
                Ask("Настройки сохранены. После переустановки откройте этот файл через «Восстановить настройки из файла».");
            }
            catch (Exception e) { Ask(T("Не удалось сохранить: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void BackupRestore()
        {
            Microsoft.Win32.OpenFileDialog dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = T("Восстановить настройки из файла"),
                Filter = T("Резервная копия Zapret Launcher") + " (*.zlbackup;*.json)|*.zlbackup;*.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(window) != true) return;
            string created = SettingsBackup.Describe(dlg.FileName);
            if (created == null) { Ask("Это не файл резервной копии Zapret Launcher", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (Ask(T("Заменить текущие настройки копией от") + " " + created + "?\n" + T("Лаунчер перезапустится, обход продолжит работать"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { SettingsBackup.Restore(dlg.FileName); }
            catch (Exception e) { Ask(T("Не удалось восстановить: ") + e.Message, MessageBoxButton.OK, MessageBoxImage.Error); return; }
            Restart();
        }

        // ---------- Обновления ----------
        async Task InstallZapretUi()
        {
            await RunBusy(async () =>
            {
                if (latest == null) latest = await Util.LatestRelease(Consts.ZapretRepo, n => n.EndsWith(".zip"));
                await Zapret.Install(latest, cfg, s => StartBypass(false));
                UpdateStrategyList();
                UpdateTitle();
            }, "Обновление…");
        }

        async Task CheckUpdates(bool quiet, bool allowAuto)
        {
            await UpdateSitePresets(quiet);   // первым: файл с raw.githubusercontent.com, лимиты GitHub API на него не действуют
            latest = await Util.LatestRelease(Consts.ZapretRepo, n => n.EndsWith(".zip"));
            lastCheck = DateTime.Now;
            string local = Zapret.LocalVersion;
            if (Util.TestNewer(latest.Version, local))
            {
                bool skipped = allowAuto && cfg.SkipVersion == latest.Version;
                if (allowAuto && cfg.AutoUpdate && !skipped)
                {
                    Log.Write("Доступна новая версия zapret: " + latest.Version + " (установлена: " + local + ")");
                    // запоминаем, сколько сайтов открывалось до обновления, — после него сравним
                    okBeforeUpdate = Zapret.Running && siteResults != null ? siteResults.Count(r => r.Ok) : (int?)null;
                    await Zapret.Install(latest, cfg, s => StartBypass(false));
                    UpdateStrategyList();
                    UpdateTitle();
                    Notify("zapret обновлён до версии " + latest.Version);
                    if (okBeforeUpdate.HasValue && Zapret.Running) { updatedFrom = local; siteCheckDue = DateTime.Now.AddSeconds(15); }
                }
                else if (!skipped || !quiet)
                    Notify("Доступна новая версия zapret: " + latest.Version + (skipped ? " (пропущена после отката)" : ""), new[] { "Обновить", "update" });
            }
            else if (!quiet) Log.Write("zapret: установлена последняя версия (" + local + ")");

            if (allowAuto && cfg.AutoUpdate) await AutoUpdateIpset();

            // ядро VPN — только если оно уже установлено (то есть VPN хоть раз включали)
            string sbLocal = Vpn.Version;
            if (sbLocal != null)
            {
                try
                {
                    Release sb = await Vpn.Latest();
                    if (Util.TestNewer(sb.Version, sbLocal) && allowAuto && cfg.AutoUpdate)
                    {
                        bool wasOn = vpnRunning && !AwgDirect;   // туннелю AmneziaWG «весь трафик» обновление sing-box не мешает
                        if (wasOn) await Vpn.Stop();
                        await Vpn.Install(sb);
                        if (wasOn) await StartVpn(true);
                        Log.Write("Ядро VPN обновлено до версии " + sb.Version);
                    }
                    else if (!quiet) Log.Write("Ядро VPN: установлена версия " + sbLocal + (Util.TestNewer(sb.Version, sbLocal) ? ", доступна " + sb.Version : " (последняя)"));
                }
                catch (Exception e) { if (!quiet) Log.Write("Ядро VPN: не удалось проверить обновления: " + e.Message); }
            }
            // Xray — только если уже скачан (был сервер с транспортом xhttp)
            string xrLocal = Vpn.XrayVersion;
            if (xrLocal != null && allowAuto && cfg.AutoUpdate)
            {
                try
                {
                    Release xr = await Vpn.XrayLatest();
                    if (Util.TestNewer(xr.Version.TrimStart('v'), xrLocal))
                    {
                        bool wasOn = vpnRunning && CurrentServer != null && Vpn.NeedsXray(CurrentServer.Link);
                        if (wasOn) await Vpn.Stop();
                        await Vpn.InstallXray(xr);
                        if (wasOn) await StartVpn(true);
                    }
                }
                catch (Exception e) { Log.Write("Ядро Xray: не удалось обновить: " + e.Message); }
            }
            // AmneziaWG — только если уже скачан (был сервер AmneziaWG). Пока туннель работает, его файлы заняты:
            // VPN на время обновления выключается и сразу включается снова
            string awgLocal = Awg.Version;
            if (awgLocal != null)
            {
                try
                {
                    Release awg = await Awg.Latest();
                    if (Util.TestNewer(awg.Version, awgLocal) && allowAuto && cfg.AutoUpdate)
                    {
                        bool wasOn = vpnRunning && CurrentServer != null && Awg.IsAwg(CurrentServer.Link);
                        if (wasOn) await Vpn.Stop();
                        await Awg.Install(awg);
                        if (wasOn) await StartVpn(true);
                        Log.Write("Ядро AmneziaWG обновлено до версии " + awg.Version);
                    }
                    else if (!quiet) Log.Write("Ядро AmneziaWG: установлена версия " + awgLocal + (Util.TestNewer(awg.Version, awgLocal) ? ", доступна " + awg.Version : " (последняя)"));
                }
                catch (Exception e) { Log.Write("Ядро AmneziaWG: не удалось обновить: " + e.Message); }
            }

            if (cfg.Telegram)
            {
                try { await Telegram.UpdateHostsCache(); Telegram.SetHosts(true); }
                catch (Exception e) { Log.Write("Не удалось обновить адреса Telegram: " + e.Message); }
                string tgLocal = Telegram.ProxyVersion;
                if (tgLocal != null)
                {
                    try
                    {
                        Release tg = await Telegram.LatestProxy();
                        if (Util.TestNewer(tg.Version, tgLocal))
                        {
                            if (allowAuto && cfg.AutoUpdate) { await Telegram.InstallProxy(tg); Notify("tg-ws-proxy обновлён до версии " + tg.Version); }
                            else Notify("Доступна новая версия tg-ws-proxy: " + tg.Version);
                        }
                        else if (!quiet) Log.Write("tg-ws-proxy: установлена последняя версия (" + tgLocal + ")");
                    }
                    catch (Exception e) { Log.Write("tg-ws-proxy: не удалось проверить обновления: " + e.Message); }
                }
            }

            await CheckLauncherUpdate(quiet, allowAuto && cfg.AutoUpdate);
        }

        // Готовые наборы сайтов из репозитория (SitePresets.Parse — что в файле и как он проверяется). Если в набор, который у пользователя
        // включён, добавили домены или программы, они дописываются в его списки: в «Обходить» (тогда обход перезапускается) и в маршруты VPN
        async Task UpdateSitePresets(bool quiet)
        {
            string text;
            try { text = await Util.GetString(SitePresets.Url + "?t=" + DateTime.Now.Ticks, 10); }
            catch (Exception e) { if (!quiet) Log.Write("Готовые наборы: не удалось скачать: " + e.Message); return; }
            string cached = null;
            try { if (File.Exists(SitePresets.Cache)) cached = File.ReadAllText(SitePresets.Cache, System.Text.Encoding.UTF8); } catch { }
            if (text == cached && SitePresets.Updated != null) { if (!quiet) Log.Write("Готовые наборы: без изменений"); return; }
            string error;
            SitePresets.PresetFile f = SitePresets.Parse(text, out error);
            if (f == null) { Log.Write("Готовые наборы: файл из репозитория не подошёл — " + error); return; }
            if (f.Skipped.Count > 0) Log.Write("Готовые наборы: пропущены наборы с ошибками — " + string.Join(", ", f.Skipped));

            SitePreset[] old = SitePresets.All;
            SitePresets.Use(f);
            try { File.WriteAllText(SitePresets.Cache, text, new System.Text.UTF8Encoding(false)); } catch (Exception e) { Log.Write("Готовые наборы: не удалось сохранить: " + e.Message); }

            bool restart = false;
            if (Zapret.LocalVersion != null)
            {
                List<string> general = Zapret.UserDomains("general");
                List<string> add = SitePresets.Additions(old.Where(p => !p.VpnOnly), f.Sets, general, general, false);
                if (add.Count > 0)
                {
                    Zapret.SaveUserDomains("general", general.Concat(add));
                    Log.Write("Готовые наборы обновлены — добавлено в «Обходить»: " + string.Join(", ", add));
                    restart = true;
                }
            }
            List<string> vpnDomains = SitePresets.Additions(old, f.Sets, cfg.VpnDomains, cfg.VpnDomains, false);
            List<string> vpnApps = SitePresets.Additions(old, f.Sets, cfg.VpnDomains, cfg.VpnApps, true);
            if (vpnDomains.Count + vpnApps.Count > 0)
            {
                cfg.VpnDomains.AddRange(vpnDomains);
                cfg.VpnApps.AddRange(vpnApps);
                Log.Write("Готовые наборы обновлены — добавлено в маршруты VPN: " + string.Join(", ", vpnDomains.Concat(vpnApps)));
            }
            if (!restart && vpnDomains.Count + vpnApps.Count == 0) Log.Write("Готовые наборы сайтов обновлены" + (f.Updated != null ? " (" + f.Updated + ")" : ""));
            UpdateDomainUi();
            RefreshVpnUi();
            AutoBackup();
            if (restart) await RestartIfRunning();
            if (vpnDomains.Count + vpnApps.Count > 0) await ApplyVpnChanges();
        }

        // Список IP-адресов для IPSet — раз в неделю вместе с проверкой обновлений. Если он изменился и используется,
        // обход перезапускается, чтобы winws перечитал список
        async Task AutoUpdateIpset()
        {
            string mode = Zapret.IpsetMode(Paths.Zapret);
            DateTime last;
            if (mode == null || (DateTime.TryParse(cfg.IpsetUpdated, out last) && (DateTime.Now - last).TotalDays < 7)) return;
            string file = Paths.InZapret(@"lists\ipset-all.txt");
            string target = mode == "loaded" ? file : file + ".backup";
            string before = File.Exists(target) ? File.ReadAllText(target) : "";
            try { await Zapret.UpdateIpsetList(); }
            catch (Exception e) { Log.Write("IPSet: не удалось обновить список: " + e.Message); return; }
            cfg.IpsetUpdated = DateTime.Now.ToString("yyyy-MM-dd");
            cfg.Save();
            bool changed = File.Exists(target) && File.ReadAllText(target) != before;
            if (changed && mode == "loaded" && Zapret.Running && !pausedByVpn) await StartBypass(false);
        }

        async Task CheckLauncherUpdate(bool quiet, bool install)
        {
            try
            {
                launcherLatest = await SelfUpdate.Latest(cfg.LauncherRepo);
                if (!Util.TestNewer(launcherLatest.Version, Consts.Version))
                {
                    if (!quiet) Log.Write("Лаунчер: установлена последняя версия (" + Consts.Version + ")");
                    return;
                }
                if (!install) { Notify("Доступна новая версия лаунчера: " + launcherLatest.Version, new[] { "Обновить лаунчер", "launcher-update" }); return; }
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }
            catch (Exception e) { if (!quiet) Log.Write("Лаунчер: не удалось проверить обновления (" + cfg.LauncherRepo + "): " + e.Message); }
        }

        Task UpdateLauncherUi(bool ask)
        {
            return RunBusy(async () =>
            {
                launcherLatest = await SelfUpdate.Latest(cfg.LauncherRepo);
                if (!Util.TestNewer(launcherLatest.Version, Consts.Version))
                {
                    Dialog.Show(window, T("Установлена последняя версия лаунчера") + " (" + Consts.Version + ").");
                    return;
                }
                if (ask && Dialog.Show(window, T("Доступна версия") + " " + launcherLatest.Version + ". " + T("Обновить лаунчер сейчас? Обход продолжит работать."),
                    "Zapret Launcher", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                await SelfUpdate.Apply(launcherLatest);
                Restart();
            }, "Проверка…");
        }

        // Фоновые обновления — под флагом busy, чтобы сторож не перезапускал обход посреди установки
        async Task<bool> BackgroundUpdate()
        {
            if (busy) return false;
            busy = true;
            try { await CheckUpdates(true, true); return true; }
            catch (Exception e) { Log.Write("Не удалось проверить обновления: " + e.Message); return false; }
            finally { busy = false; UpdateUi(); }
        }

        void Restart()
        {
            App.ReleaseMutex();
            Process.Start(Paths.Exe, (window.IsVisible ? "" : "--autostart ") + "--restarted");
            Exit();
        }
    }
}
