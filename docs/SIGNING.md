# Подпись релизов (SignPath Foundation)

Релизы подписываются бесплатным сертификатом [SignPath Foundation](https://signpath.org) для open source.
Пока проект не одобрен и в репозитории нет настроек ниже, сборка выпускает релизы без подписи, как раньше.

## Как это работает

Всё делает сборка на GitHub (`.github/workflows/release.yml`) при теге `vX.Y.Z`:

1. Собирает лаунчер и установщик, прогоняет тесты.
2. Отправляет `ZapretLauncher.exe` на подпись (конфигурация `launcher`) и ждёт, пока запрос одобрят в SignPath.
3. Пересобирает установщик вокруг подписанного лаунчера (`build.ps1 -InstallerOnly`): именно этот лаунчер установится на компьютер.
4. Отправляет `ZapretLauncherSetup.exe` на подпись (конфигурация `setup`) и снова ждёт одобрения.
5. Проверяет подписи обоих файлов и публикует релиз.

Каждый запрос на подпись нужно **одобрить вручную** в SignPath — так требуют правила SignPath Foundation. Сборка ждёт до 3 часов на каждый запрос.

Не подписывается маленький помощник `ZapretLauncherCmd.exe` (меню на значке в панели задач): он встроен в лаунчер, и его подпись потребовала бы ещё одного запроса.

## Что настроить один раз

1. Подать заявку: https://signpath.org/apply (текст-заготовка — у разработчика).
2. Включить двухфакторную защиту на GitHub и в SignPath — это обязательное условие.
3. В SignPath после одобрения:
   - добавить в организацию доверенную систему сборки **GitHub.com** и привязать её к проекту; по желанию — установить приложение SignPath для GitHub;
   - создать две конфигурации артефактов со слагами `launcher` и `setup` (тексты ниже);
   - создать пользователя с правами submitter для проекта и выпустить ему API-токен.
4. В репозитории GitHub → Settings → Secrets and variables → Actions:
   - секрет `SIGNPATH_API_TOKEN` — API-токен;
   - переменные `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG` — из SignPath.
5. Добавить в README раздел «Code signing policy» (ниже) — SignPath Foundation требует его на главной странице проекта.

### Конфигурация `launcher`

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <parameters>
    <parameter name="version" required="true" />
  </parameters>
  <zip-file>
    <pe-file path="ZapretLauncher.exe" product-name="Zapret Launcher" product-version="${version}" file-version="${version}"
             company-name="Pont1fe" copyright="Copyright (c) 2026 Pont1fe" original-filename="ZapretLauncher.exe">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

### Конфигурация `setup`

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <parameters>
    <parameter name="version" required="true" />
  </parameters>
  <zip-file>
    <pe-file path="ZapretLauncherSetup.exe" product-name="Zapret Launcher" product-version="${version}" file-version="${version}"
             company-name="Pont1fe" copyright="Copyright (c) 2026 Pont1fe" original-filename="ZapretLauncherSetup.exe">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

`version` сборка передаёт сама — это версия файла, например `4.0.5.0`. В начале нового года поменяйте год в `copyright` здесь и в `app/App.cs`, `installer/Setup.cs` одновременно, иначе SignPath откажет в подписи.

## Раздел для README (после одобрения)

```markdown
## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [Pont1fe](https://github.com/Pont1fe)
- Approvers: [Pont1fe](https://github.com/Pont1fe)

Privacy: the launcher does not collect or send any data about you. It connects only to: GitHub (updates of zapret, the launcher, tg-ws-proxy, sing-box, Xray and AmneziaWG; zapret and Telegram address lists; the ready-made site sets of this repository; the Russia routing databases of sing-box), a few sites to check that they open (discord.com, youtube.com, googlevideo.com, web.telegram.org, vk.com, ya.ru, msftconnecttest.com; www.gstatic.com through each VPN server), the VPN servers and subscription you add yourself, Cloudflare DNS 1.1.1.1 through the VPN for the sites you send through it, and the secure DNS provider you pick, if you turn it on.
```
