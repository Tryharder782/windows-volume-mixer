[English](README.md) | [Русский](README.ru.md)

# Volume Mixer для Windows 11

[![Build & Validate](https://github.com/Tryharder782/windows-volume-mixer/actions/workflows/build.yml/badge.svg)](https://github.com/Tryharder782/windows-volume-mixer/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/Tryharder782/windows-volume-mixer)](https://github.com/Tryharder782/windows-volume-mixer/releases/latest)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Windows 11](https://img.shields.io/badge/Windows-11-0078D6?logo=windows)](https://www.microsoft.com/windows)
[![Manifest V3](https://img.shields.io/badge/Chromium-Manifest%20V3-4285F4?logo=googlechrome&logoColor=white)](VolumeMixer.Extension/manifest.json)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

Лёгкий минималистичный микшер громкости для Windows 11 в стиле macOS Control Center. Позволяет менять уровень звука отдельных приложений (WASAPI) и каждой вкладки браузера независимо, управлять воспроизведением через встроенный мини-плеер как на экране блокировки смартфона и переключать профили аудиоустройств через мост с `CoreAudioTray` — поверх безрамочных игр и без всплывающей панели задач.

---

## Основные возможности

- **Прозрачность Windows 11 (Acrylic / Mica)**: нативное размытие фона через `SetWindowCompositionAttribute`, которое сохраняется даже в неактивном состоянии окна.
- **UI и UX в стиле macOS Control Center**: толстые скруглённые pill-слайдеры с поддержкой колёсика мыши, плавные `CubicEase`-анимации открытия, панелей и переключателей, компактный размер (~10% площади экрана над системным треем).
- **Поверх игр без панели задач**: окно открывается с флагом `WS_EX_NOACTIVATE` и закрывается по клику вне окна (через лёгкий `WH_MOUSE_LL`-хук, активный только пока микшер открыт), не забирая фокус у borderless-игр.
- **Громкость приложений (WASAPI)**: группировка аудиосессий по процессам, кэширование иконок и понятных названий из метаданных `.exe`, сохранение громкости и мута между сессиями (`%LocalAppData%\VolumeMixer\volumes.json`).
- **Отдельная громкость вкладок Chromium**: работает в паре с Manifest V3 расширением (Chrome, Edge, Brave, Opera, Vivaldi, Яндекс) через локальный WebSocket (`127.0.0.1:49152`):
  - Регулировка через Web Audio API `GainNode` (не сбрасывается нормализацией YouTube Stable Volume) с автоматическим откатом на `el.volume` для кросс-доменных видео без CORS.
  - Собственные favicon-иконки каждой вкладки, конвертация `.webp`/`.avif` обложек в JPEG на лету, heartbeat каждые 20 секунд (чтобы Service Worker не засыпал) и громкость `100%` по умолчанию для каждой новой вкладки.
- **Раскрывающийся мини-плеер (`▾` / `▴`) как на экране блокировки**: изначально скрыт, раскрывается у любого источника с поддержкой медиа (SMTC-приложения и вкладки браузера). Показывает обложку, название, автора/канал, векторные кнопки `Назад / Play-Pause / Вперёд`, бейдж `LIVE` для стримов и перемотку по отпусканию ползунка (`CommitOnRelease`).
- **Настраиваемые глобальные бинды и автозапуск**: встроенная запись горячих клавиш для вызова микшера (по умолчанию `Ctrl + Shift + V`), мута общего звука и мута активного окна, тихий автозапуск в трей (`--tray`) и пробуждение окна при повторном запуске.
- **Мост с `CoreAudioTray`**: читает `%AppData%\CoreAudioTray\config.json` для переключения профилей **🎧 Наушники / 🔊 Спикеры** в один клик и раздельного выбора **Выход / Микрофон** (`IPolicyConfig`), с сохранением выбранного профиля между сессиями.
- **Экономия ресурсов**: в свёрнутом состоянии в трее фоновые таймеры опроса полностью остановлены.

---

## Быстрый старт

### Вариант А: готовая сборка из Releases (рекомендуется)

1. Убедитесь, что установлен **.NET 9 Desktop Runtime (x64)** ([скачать с сайта Microsoft](https://dotnet.microsoft.com/download/dotnet/9.0)).
2. Скачайте `VolumeMixer-v1.0.0-win-x64.exe` и `VolumeMixer-Extension-v1.0.0.zip` со страницы [Latest Release](https://github.com/Tryharder782/windows-volume-mixer/releases/latest).
3. Переместите `VolumeMixer-v1.0.0-win-x64.exe` в постоянную папку (например, `C:\Tools\VolumeMixer\VolumeMixer.exe`) и запустите — микшер открывается по `Ctrl + Shift + V` или по клику на иконку в трее.
4. *(Опционально)* Распакуйте `VolumeMixer-Extension-v1.0.0.zip` и установите расширение в браузер (инструкция ниже).

### Вариант Б: сборка из исходников

**Требования**: Windows 10 (22621+) или Windows 11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/Tryharder782/windows-volume-mixer.git
cd windows-volume-mixer

# Сборка решения
dotnet build VolumeMixer.sln -c Release

# Публикация автономного single-file исполняемого файла
dotnet publish VolumeMixer.App/VolumeMixer.App.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained false -o publish
```

### Установка расширения для вкладок браузера

1. Откройте `chrome://extensions` (или `brave://extensions`, `edge://extensions`, `opera://extensions`).
2. Включите **Режим разработчика** (Developer mode).
3. Нажмите **Загрузить распакованное расширение** (Load unpacked) и выберите папку `VolumeMixer.Extension` (путь можно скопировать кнопкой *«Копировать путь к расширению»* прямо в настройках микшера).

---

## Конфигурация и хранение данных

| Файл | Назначение |
| :--- | :--- |
| `%LocalAppData%\VolumeMixer\settings.json` | Горячие клавиши, режим фона (`Acrylic` / `Mica`), автозапуск (`RunAtStartup`), порт WebSocket (`49152`). |
| `%LocalAppData%\VolumeMixer\volumes.json` | Сохранённые уровни громкости/мута приложений и закреплённый профиль `LockedProfile`. |
| `%AppData%\CoreAudioTray\config.json` | Внешний конфиг профилей `CoreAudioTray` для моста переключения устройств. |

---

## Известные ограничения

- **Сайты с жёстким DRM (Widevine)**: плееры вроде Netflix могут блокировать захват звука через Web Audio `GainNode` — для них громкость регулируется общим слайдером браузера.
- **Exclusive Fullscreen в играх**: в старых играх с эксклюзивным полноэкранным режимом любое стороннее окно при вызове может сворачивать игру. Рекомендуется режим *Borderless Windowed* (окно без рамки).
- **Конфликт порта `49152`**: если порт занят другим приложением, измените `WebSocketPort` в `%LocalAppData%\VolumeMixer\settings.json` и `WS_URL` в `VolumeMixer.Extension/background.js`.

---

## Лицензия

Проект распространяется под лицензией [MIT](LICENSE).
