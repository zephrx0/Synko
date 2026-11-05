# Локальные зависимости интерфейса Synko

- Microsoft.Web.WebView2 1.0.4314.35 — SDK и loader нативного окна. Runtime используется системный; на машине разработки обнаружен 156.0.4314.58.
- @material/material-color-utilities 0.4.0 — опубликованные ES-модули, Apache-2.0. Источник: https://registry.npmjs.org/@material/material-color-utilities/-/material-color-utilities-0.4.0.tgz. Файлы `.js` и `LICENSE` сохранены без изменения. Два extensionless imports разрешаются локальным import map с CSP-хешем.
- Roboto Flex — variable TTF из google/fonts, SIL OFL 1.1. Шрифт и OFL.txt находятся в `Ui/fonts`.
- Material Symbols Rounded — variable WOFF2 из google/material-design-icons, Apache-2.0. Шрифт и LICENSE находятся в `Ui/fonts`.

Шрифты, иконки и код Material Color Utilities загружаются локально. Node.js, npm, UI-фреймворки и внешние CDN для интерфейса не нужны.
# Дополнения Synko 2.1

Локальные SVG Apple Music, Spotify, Discord и YouTube: [Simple Icons](https://github.com/simple-icons/simple-icons), CC0; исходные файлы и лицензия включены в `Ui/assets`. Использование товарных знаков не означает принадлежность приложения к владельцам этих марок.

Резервная PNG-иконка Spotify взята из [официального веб-плеера](https://open.spotifycdn.com/cdn/images/favicon32.b64ecc03.png), хранится в `docs/images/spotify.png` и используется как публичная резервная картинка Discord, когда обложка трека не найдена.

## Synko 2.2

Material Symbols Rounded заменён на официальный локальный subset только используемых символов (4 140 байт вместо 5 402 952). Источник: Google Fonts CSS API с `icon_names`, локальный WOFF2, Apache-2.0. Цвет и форма иконок управляются теми же UI-компонентами.

Браузерные логотипы: Simple Icons (CC0), [Browser Logos](https://github.com/alrra/browser-logos) (MIT, права на товарные знаки у владельцев) и официальный логотип Waterfox (уведомление MPL 2.0). Файлы и уведомления находятся в `Ui/assets`, лицензии включены в архив.

Чтение адресной строки использует системный COM API UI Automation; WPF-зависимость удалена. Самодостаточная сборка сжимается средствами .NET (`EnableCompressionInSingleFile`), без неподдерживаемого trimming WinForms.
