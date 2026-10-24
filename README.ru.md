# Zapret Launcher

**Русский** · [English](README.md)

Удобный лаунчер для [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) (Flowseal) — обход блокировок Discord, YouTube и Telegram в Windows одной кнопкой.

## Установка

1. Скачайте **ZapretLauncherSetup.exe** со страницы [Releases](https://github.com/Pont1fe/zapret-launcher/releases/latest).
2. Запустите и нажмите «Установить». По умолчанию лаунчер ставится в `C:\ZapretLauncher`. При первом запуске он сам скачает последнюю версию zapret.
3. Нажмите большую карточку-переключатель или «Подобрать стратегию».

> Windows SmartScreen может предупредить, что приложение не подписано: «Подробнее» → «Выполнить в любом случае». Антивирус может ругаться на драйвер WinDivert, без которого zapret не работает, — добавьте папку лаунчера в исключения.

## Возможности

- **Запуск одной кнопкой**, смена стратегии кликом, работа из трея. winws запускается без консольного окна.
- **Автоподбор стратегии**: лаунчер проверяет все стратегии на Discord, YouTube, видео YouTube и Telegram и ставит лучшую.
- **Карточки сайтов** на главном экране показывают HTTPS-время через обход и TCP-пинг до каждого сервера.
- **Сторож**: если winws упал, перезапускает его; если сайты перестали открываться, а интернет есть, сам подбирает новую стратегию.
- **Пауза при VPN**: пока включён VPN, обход стоит на паузе и возвращается, когда VPN отключат.
- **Своя стратегия для каждой сети**: дома, на мобильном интернете и в другом Wi-Fi лаунчер переключается на ту стратегию, что работала в этой сети.
- **Telegram**: Desktop работает через локальный [tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), Web — через записи в hosts. Всё включается одним переключателем.
- **Автообновление** zapret, tg-ws-proxy и самого лаунчера. Ваши списки и фильтры сохраняются, а прошлую версию zapret можно вернуть одной кнопкой.
- **Домены, Game Filter и IPSet** настраиваются прямо в окне, без `service.bat`.
- **Уведомления Windows** с кнопками действий и **автозапуск с Windows** через Планировщик задач, без запроса UAC.
- Русский и английский интерфейс; удаление через «Установленные приложения» Windows.

## Сборка из исходников

Нужен только Windows 10/11: используется встроенный компилятор C# из .NET Framework 4.8.

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Готовые `ZapretLauncher.exe` и `ZapretLauncherSetup.exe` появятся в папке `dist`. Релизы собирает GitHub Actions: поменяйте `AppVersion` в `app/App.cs`, обновите `RELEASE_NOTES.md` и отправьте тег `vX.Y.Z`.

- `app/` — лаунчер (WPF): `Launcher.cs` — окно и логика, `Zapret.cs` — работа с zapret, `Telegram.cs`, `Network.cs` — проверки сайтов, VPN и сети, `Toast.cs` — уведомления, `I18n.cs` — перевод, `Shell.cs` — значки, ярлыки, самообновление, `ui.xaml` — интерфейс.
- `installer/` — установщик (WinForms).

## Благодарности

- [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube) и [bol-van/zapret](https://github.com/bol-van/zapret) — сам обход.
- [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) — прокси для Telegram Desktop.

Лаунчер — неофициальная оболочка; zapret и tg-ws-proxy скачиваются напрямую из релизов их авторов.
