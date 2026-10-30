**Download `ZapretLauncherSetup.exe`** — installs or updates the launcher (settings and zapret are kept). Installed launchers update themselves automatically.
**Скачайте `ZapretLauncherSetup.exe`** — установит или обновит лаунчер (настройки и zapret сохранятся). Установленные лаунчеры обновятся сами.

![Zapret Launcher](https://raw.githubusercontent.com/Pont1fe/zapret-launcher/main/docs/screenshots/vpn-en.png)

### 4.0.1 — smarter about blocks / Умнее с блокировками

- 🚧 Sites your ISP blocks by address (zapret can't help there) are now detected: the card says “blocked by address — VPN will help”, and one “Via VPN” button on the Routes tab sends the whole service through the VPN / Сайты, которые провайдер блокирует по адресу (тут zapret бессилен), теперь распознаются: на карточке — «заблокирован по адресу — поможет VPN», а кнопка «Через VPN» на вкладке «Маршруты» пускает весь сервис через VPN
- 🐕 The watchdog no longer keeps picking new strategies because of such sites / Сторож больше не гоняет подбор стратегий из-за таких сайтов
- 🛠️ A “Fix” button on the warning bar: closes other bypass programs, stops their services and unloads a foreign WinDivert driver / Кнопка «Исправить» на жёлтой плашке: закроет другие программы обхода, остановит их службы и выгрузит чужой драйвер WinDivert
- 📊 Connections grouped by app, with real app icons and traffic per app / Журнал соединений по программам — с настоящими значками и трафиком каждой
- ✨ Smooth transitions between pages and tabs; a new welcome tour with pictures of the VPN and routing modes / Плавные переходы между страницами и вкладками; новое приветствие с картинками про VPN и режимы маршрутов

### 4.0.0 — VPN right inside the launcher / VPN прямо в лаунчере

The biggest update yet: zapret and a VPN now live in one window and work together. No separate VPN app needed.
Самое большое обновление: zapret и VPN теперь в одном окне и работают вместе. Отдельное VPN-приложение больше не нужно.

**🌍 Built-in VPN / Встроенный VPN**
- Paste a vless / vmess / trojan / ss / hysteria2 / tuic key or a subscription URL — or just scan a QR code from an image or straight off the screen / Вставьте ключ vless / vmess / trojan / ss / hysteria2 / tuic или адрес подписки — или просто отсканируйте QR-код с картинки или прямо с экрана
- Copied a key? The launcher notices it and offers to add it / Скопировали ключ? Лаунчер заметит и предложит добавить
- Keys with the xhttp transport work too — the Xray core downloads itself when needed / Работают и ключи с транспортом xhttp — ядро Xray скачается само, когда понадобится

**🧭 Smart routing / Умная маршрутизация**
- Only the sites and apps you choose go through the VPN; everything else goes directly and through zapret / Через VPN идут только выбранные сайты и программы, остальное — напрямую и через zapret
- “Russia direct”: Russian sites go directly, the rest through the VPN — banks and Gosuslugi keep working / «Россия напрямую»: российские сайты — напрямую, остальное — через VPN, банки и Госуслуги работают как обычно
- Or send all traffic through the VPN with one click / Или весь трафик через VPN одной кнопкой

**⚡ Servers / Серверы**
- Honest check: a real page is opened through every server, not just a ping; signal bars show who's fast / Честная проверка: через каждый сервер открывается настоящая страница, а не просто пинг; полоски сигнала сразу показывают, кто быстрый
- “Pick the fastest” and auto-choice: if a server goes down, the VPN hops to another one / «Выбрать самый быстрый» и автовыбор: если сервер упал, VPN сам перейдёт на другой
- Servers grouped by country with flags and search; subscription traffic and expiry, auto-refresh once a day / Серверы по странам с флагами и поиском; трафик и срок подписки, автообновление раз в сутки
- Live list of connections: see what goes through the VPN and what goes direct / Живой журнал соединений: видно, что идёт через VPN, а что напрямую

**🎨 New look / Новый вид**
- A big VPN card with the country flag, connection timer and speed graph — tap it to connect / Большая карточка VPN с флагом страны, временем подключения и графиком скорости — нажмите, чтобы подключиться
- Settings split into tabs with icons, a tidier right panel, “How to start” hints for VPN newcomers / Настройки разложены по вкладкам со значками, правая панель стала аккуратнее, для новичков — подсказка «Как начать»
- New logo — a shield with a Z — and a blue VPN mark on the tray icon; VPN switch in the tray mini window / Новый логотип — щит с буквой Z — и синяя метка VPN на значке трея; переключатель VPN в мини-окне у трея

**🔙 Safety net / Подстраховка**
- Didn't like an update? Roll the launcher back to the previous version in Settings → Launcher; your settings are backed up first / Не понравилось обновление? Верните прошлую версию лаунчера в Настройках → Лаунчер, настройки перед этим сохранятся
- “What's new” window after every update / Окно «Что нового» после каждого обновления

### 3.4.0
- Settings backup: save to / restore from a file; an automatic copy outside the launcher folder is offered back after reinstalling / Резервная копия настроек: сохранение в файл и восстановление; автокопия вне папки лаунчера — после переустановки лаунчер предложит её вернуть
- Secure DNS (Settings → DNS): Cloudflare, Google, Quad9 or AdGuard with DNS over HTTPS on Windows 11; restores automatic DNS if the server doesn't respond / Защищённый DNS (Настройки → DNS): Cloudflare, Google, Quad9 или AdGuard с шифрованием DNS через HTTPS в Windows 11; если сервер не отвечает, DNS возвращается автоматический
- Check any site on the Domains page: does it open, how fast, is DNS spoofed / Проверка любого сайта на странице «Домены»: открывается ли, за сколько, не подменён ли DNS
- The IPSet list updates itself weekly / Список IPSet обновляется сам раз в неделю

### 3.3.3
- “Exit” in the tray menu now turns bypass and the Telegram proxy off before closing the launcher (restarts after updates still keep bypass running) / «Выход» в меню трея теперь выключает обход и Telegram-прокси перед закрытием лаунчера (при перезапуске после обновления обход по-прежнему не прерывается)
- Fixed the installer crashing on start / Исправлено падение установщика при запуске

### 3.3.2
- Sharp tray icon at any display scale, crisper small text, multi-size window icon / Чёткий значок в трее при любом масштабе экрана, резкий мелкий текст, значок окна во всех размерах
- Taskbar icon menu (right click): turn bypass on/off, find a strategy, fix Discord, favorite strategies — without UAC prompts / Меню на значке в панели задач (правый клик): включить и выключить обход, подбор, «Починить Discord», избранные стратегии — без запроса UAC
- Quick search among starred strategies (star button next to “Find best strategy”) / Быстрый подбор по избранным стратегиям (кнопка со звёздочкой рядом с «Подобрать стратегию»)
- If fewer sites open after a zapret auto-update, the launcher offers to restore the previous version / Если после автообновления zapret сайтов открывается меньше, лаунчер предложит вернуть прошлую версию
- On Windows start, bypass waits for the internet to come up / При запуске Windows обход ждёт появления интернета
- Compact view (button in the title bar), splash screen, loading ring on the switch, site cards lift on hover and open the site on click, sliding menu highlight / Компактный вид (кнопка в заголовке), заставка при запуске, кольцо загрузки на переключателе, карточки сайтов приподнимаются при наведении и открывают сайт по клику, плавная подсветка меню
- Fixed: site cards were empty when the launcher opened with bypass off / Исправлено: карточки сайтов были пустыми, если открыть лаунчер с выключенным обходом

### 3.3.1
- The window can be resized and maximized; size and position are remembered / Окно можно растягивать и разворачивать на весь экран, размер запоминается
- 24-hour history: when bypass worked, was paused for VPN or failed / История за сутки: когда обход работал, стоял на паузе из-за VPN или давал сбой
- Larger site cards with service icons, big ping numbers and full-width charts / Крупные карточки сайтов: значки сервисов, пинг крупным числом, график на всю ширину
- The switch card turns yellow on VPN pause and red on errors, glows while working and sends a wave when turned on / Карточка-переключатель желтеет при паузе и краснеет при ошибке, «дышит» во время работы и пускает волну при включении
- Strategy groups collapse; hovering a strategy shows the last search results per site / Группы стратегий сворачиваются, при наведении на стратегию видны результаты последнего подбора по сайтам
- Accent color choice, interface scale 90–130%, smooth theme switching, “?” hints for complex settings / Выбор цвета акцента, масштаб интерфейса 90–130%, плавная смена темы, подсказки «?» к сложным настройкам
- The taskbar icon changes color like the tray icon; the tray mini window shows the state and has a Discord fix button / Значок в панели задач меняет цвет как в трее; мини-окно показывает состояние и умеет чинить Discord
- “Refresh ping” is now just “Refresh”; removed the duplicate zapret folder button / «Обновить пинг» теперь просто «Обновить»; убрана повторяющаяся кнопка папки zapret

### 3.3.0
- Tray icon shows the state: teal — working, yellow — paused for VPN, red — error, grey — off / Значок в трее показывает состояние: бирюзовый — работает, жёлтый — пауза из-за VPN, красный — ошибка, серый — выключен
- Strategy search is about twice as fast and shows live progress: a bar on the switch card, the taskbar icon, site cards and marks in the list / Подбор стратегии примерно вдвое быстрее и показывает прогресс вживую: полоска на карточке, значок в панели задач, карточки сайтов и метки в списке
- After sleep or a network change, bypass is checked right away / После сна и смены сети обход проверяется сразу
- Less memory and fewer background checks while in the tray or on battery; faster start / Меньше памяти и реже фоновые проверки в трее и от батареи; быстрее запуск
- Downloads of zapret, tg-ws-proxy and the launcher are verified by SHA-256 / Загрузки zapret, tg-ws-proxy и лаунчера проверяются по SHA-256
- Telegram entries in hosts are restored if an antivirus removes them / Записи Telegram в hosts восстанавливаются, если их удалил антивирус
- “Fix Discord” in Settings and the tray menu: closes Discord, clears its cache and reopens it / «Починить Discord» в настройках и меню трея: закрывает Discord, чистит кэш и открывает заново
- The log is limited to 1 MB / Журнал ограничен 1 МБ

### 3.2.1
- Fixed black text in the strategy and domain lists on some systems / Исправлен чёрный текст в списках стратегий и доменов на некоторых системах
- Screenshots on the project page / Скриншоты на странице проекта

### 3.2.0
- Light theme, “As in Windows” mode and optional Mica background / Светлая тема, режим «Как в Windows» и прозрачный фон Mica
- Mini window by the tray, styled dialogs, animations, ping history, strategy groups and favorites, welcome tour / Мини-окно у трея, свои диалоги, анимации, график пинга, группы и избранное стратегий, приветствие

Windows SmartScreen may warn about an unsigned app: “More info” → “Run anyway”.
SmartScreen может предупредить о неподписанном приложении: «Подробнее» → «Выполнить в любом случае».
