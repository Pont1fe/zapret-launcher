using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ZapretLauncher
{
    partial class Launcher
    {
        async Task<Verdict> RepairConnection(List<Action<string, string>> marks, Action<string> progress, Func<bool> cancelled, Window owner, System.Threading.CancellationToken cancellation)
        {
            RepairSnapshot snapshot = new RepairSnapshot(cfg);
            string strategy = snapshot.Strategy, dns = snapshot.Dns;
            bool running = Zapret.Running, wanted = wantRunning, paused = pausedByVpn, wasCrashed = crashed;
            int? previousBaseline = baseline;
            bool success = false, attempted = false;
            Verdict result = null;
            Exception error = null;
            Verdict checkedResult = null;
            try
            {
                result = await ConnectionRepair.Run(
                    async () =>
                    {
                        if (checkedResult != null) { Verdict cached = checkedResult; checkedResult = null; return cached; }
                        progress("Проверяю соединение…");
                        return await RunDiagnosis(marks, cancellation);
                    },
                    async action =>
                    {
                        if (DemoMode) throw new Exception("В демо-режиме исправление недоступно");
                        Log.Write("Исправление соединения: " + action);
                        switch (action)
                        {
                            case "install":
                                progress("Устанавливаю zapret…");
                                attempted = true;
                                await InstallZapret(cancellation);
                                return true;
                            case "conflicts":
                                if (Dialog.Show(owner, T("Закрыть мешающие программы обхода, остановить их службы и выгрузить чужой драйвер WinDivert? Обход лаунчера перезапустится."),
                                    T("Исправить соединение"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
                                if (cancelled()) throw new OperationCanceledException();
                                progress("Убираю помехи…");
                                attempted = true;
                                await ClearConflicts();
                                return true;
                            case "dns":
                                progress("Включаю защищённый DNS…");
                                attempted = true;
                                await ChangeSecureDns(true, DnsChoice());
                                return true;
                            case "start":
                                progress("Включаю обход…");
                                attempted = true;
                                await StartBypass(false);
                                return Zapret.Running;
                            case "pick":
                                // Результат через VPN ничего не говорит о стратегии zapret.
                                if (vpnRunning || vpnLocked || cfg.VpnOn || pausedByVpn || Conflicts.ActiveVpn() != null) return false;
                                attempted = true;
                                progress("Перезапускаю обход и проверяю сайты…");
                                await StartBypass(false);
                                await Task.Delay(400, cancellation);
                                Verdict afterRestart = await RunDiagnosis(marks, cancellation);
                                if (cancelled()) throw new OperationCanceledException();
                                if (ConnectionRepair.Succeeded(afterRestart) || afterRestart.Fix != "pick") { checkedResult = afterRestart; return true; }
                                progress("Подбираю рабочую стратегию…");
                                await AutoPick(true, false, progress, cancellation);
                                return true;
                            default: return false;
                        }
                    }, cancelled);
                success = ConnectionRepair.Succeeded(result);
            }
            catch (Exception e) { error = e; }
            try
            {
                if (!success && attempted)
                {
                    progress("Возвращаю прежнюю стратегию и DNS…");
                    Log.Write("Исправление не завершено — возвращаю прежнюю стратегию и DNS");
                    Exception restoreError = null;
                    // Откат DNS и обхода независимы: ошибка одного не должна пропустить второй.
                    try
                    {
                        if (cfg.SecureDns != dns) await ChangeSecureDns(dns.Length > 0, dns);
                    }
                    catch (Exception e) { restoreError = e; }
                    try
                    {
                        bool changed = cfg.Strategy != strategy;
                        snapshot.Restore(cfg);
                        baseline = previousBaseline;
                        cfg.Save();
                        try
                        {
                            if (running && (changed || !Zapret.Running) && File.Exists(Paths.InZapret(strategy))) await StartBypass(false);
                            else if (!running && Zapret.Running) await StopBypass();
                        }
                        finally
                        {
                            wantRunning = wanted;
                            pausedByVpn = paused;
                            crashed = wasCrashed;
                            snapshot.Restore(cfg);
                            cfg.Save();
                            UpdateNetUi();
                            siteResults = null;
                            BuildSitePanel();
                            UpdateStrategyList();
                        }
                    }
                    catch (Exception e) { restoreError = restoreError == null ? e : new AggregateException(restoreError, e); }
                    if (restoreError != null) throw new Exception(T("Не удалось полностью вернуть настройки:") + " " + restoreError.Message, restoreError);
                    if (result != null) result.Text = T(result.Text) + "\n\n" + T("Прежняя стратегия и DNS восстановлены.");
                }
            }
            finally
            {
                watchdog.Reset();
                healthDue = DateTime.Now.Add(HealthInterval());
                siteCheckDue = Zapret.Running ? DateTime.Now.AddSeconds(4) : DateTime.MaxValue;
            }
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }
}
