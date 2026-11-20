// Цикл восстановления без окна и сети: после каждого действия обязательно проверяет результат.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ZapretLauncher
{
    static class ConnectionRepair
    {
        public static bool Succeeded(Verdict verdict) { return verdict != null && verdict.Title == "Всё работает"; }

        public static async Task<Verdict> Run(Func<Task<Verdict>> diagnose, Func<string, Task<bool>> fix, Func<bool> cancelled)
        {
            HashSet<string> tried = new HashSet<string>();
            while (true)
            {
                if (cancelled()) throw new OperationCanceledException();
                Verdict verdict = await diagnose();
                if (cancelled()) throw new OperationCanceledException();
                if (Succeeded(verdict) || verdict.Fix == "" || verdict.Fix == "vpn" || !tried.Add(verdict.Fix)) return verdict;
                if (!await fix(verdict.Fix)) return verdict;
            }
        }
    }
}
