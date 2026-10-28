# Благодарности и используемые компоненты (Credits & Acknowledgments)

**STORM DS DECRYPTOR 1.5.0** разработан командой **STORM TEAM** на базе архитектуры **WinUI 3 (Windows App SDK Canary/Dev)** как сверхбыстрый многопоточный инструмент для расшифровки, криптографической инспекции и подготовки образов Nintendo DS, Nintendo DSi, DSiWare, 3DS CIA, TAD и NUS/CDN.

Мы выражаем искреннюю благодарность авторам и сообществу проектов с открытым исходным кодом, чьи исследования, библиотеки и алгоритмы легли в основу этой программы.

---

## 1. Библиотеки и открытые модули

### 📦 SharpCompress
- **Автор**: Adam Hathcock и сообщество контрибьюторов SharpCompress
- **Лицензия**: MIT License
- **Назначение**: Универсальный движок для быстрого потокового чтения и извлечения файлов из архивов форматов `.zip`, `.7z`, `.rar`, `.tar`, `.gz`, `.bz2`, `.xz` без необходимости установки сторонних архиваторов в системе.
- **Репозиторий**: [https://github.com/adamhathcock/sharpcompress](https://github.com/adamhathcock/sharpcompress)

### 🔐 Microsoft Windows App SDK (WinUI 3) & .NET Cryptography
- **Разработчик**: Microsoft Corporation
- **Лицензия**: MIT License
- **Назначение**: Современный аппаратно-ускоренный графический стек DirectComposition (`WinUI 3`) и высокопроизводительные криптографические примитивы `Aes` (`AES-128-ECB / AES-CTR` с аппаратным ускорением `AES-NI`) для пакетной дешифровки разделов Modcrypt 1 и Modcrypt 2.

---

## 2. Исследования и алгоритмы дешифрования Nintendo DSi

### 🎮 melonDS & melonDS-android
- **Авторы**: Arisotura, Rafael Rivera, Nadia Holmquist, Jesse Talavera-Greenberg и контрибьюторы проекта melonDS
- **Лицензия**: GPLv3
- **Назначение**: Архитектурные алгоритмы генерации ключей DSi Modcrypt (нормализация KeyX на основе названия картриджа и GameCode, таблица констант `key_const`, 42-битный циклический сдвиг `ROL16`, сопоставление хэшей `0x350`, `0x328`, `0x364`).
- **Репозиторий**: [https://github.com/melonDS-emu/melonDS](https://github.com/melonDS-emu/melonDS)

### 🛠️ dsi-decrypt & twl-modcrypt
- **Авторы**: Prof. 9, WinterMute, Martin Korth (GBATEK)
- **Назначение**: Фундаментальные исследования структуры заголовков Nintendo DS/DSi (`NTR`/`TWL`), расчёт контрольных сумм заголовка и Secure Area `CRC16` (`0x15E`, `0x0E`), карта разделов Modcrypt (`0x220..0x22C`), структура 16-цветных пиксельных баннеров `32×32` (`0x68`) и анимированных иконок DSi (`0x1240`).

### 🛡️ tiny-AES-c
- **Автор**: Kokke
- **Лицензия**: Public Domain / Unlicense
- **Назначение**: Опорная эталонная реализация AES для верификации вычислений CTR-счётчика и поблочного XOR.
- **Репозиторий**: [https://github.com/kokke/tiny-AES-c](https://github.com/kokke/tiny-AES-c)

---

## 3. Архитектура и дизайн STORM

- **STORM TEAM**:
  - Полная реализация пользовательского интерфейса **STORM AURORA FLUID** на **WinUI 3**: 9 динамических тем, 13 цветов акцентов, 10 шрифтов, 100% локализация интерфейса, столбцов и подсказок на 11 языков, единые высококонтрастные векторные `Path`-иконки и адаптивная вертикальная перестройка (`Responsive Vertical Stacking`).
  - Синхронный декодер пиксельных баннеров картриджей `32×32 → 128×128 Nearest-Neighbor` (`WriteableBitmap`) и встроенный **ROM & Banner Inspector**.
  - Чистый Win32-сервис системного трея `StormTrayService` (`Close-to-Tray`) и сквозной `Drag-and-Drop` на всей площади окна (включая режим Администратора).
  - Единый тёмный установщик **STORM INSTALLER**, подписанный цифровым сертификатом **`STORM SERTIFICATE`** (`Проверенный издатель: STORM TEAM`).
