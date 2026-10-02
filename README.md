# Windows Shell

Персональная оболочка поверх Windows 11: верхняя панель с категориями, плавающий Dock и виджеты (первый — звук). Tauri 2 + React + TypeScript, всё локально.

## Документы

- [Исходный MVP](docs/mvp-v2-original.md) и [концепт](docs/concept.png)
- [Архитектура и оценка осуществимости](docs/ARCHITECTURE.md)
- [Система виджетов и аудио-виджет](docs/WIDGETS.md)
- [Без Taskbar: системный хвост и свой трей](docs/SYSTEM-TAIL.md)
- [План по этапам](docs/ROADMAP.md)
- [Дизайн-система «Shell Glass»](docs/DESIGN-SYSTEM.md) и [живое превью](docs/design/preview.html)

## Разработка

Нужны Node 22+, pnpm 10, Rust stable и [зависимости Tauri для Windows](https://v2.tauri.app/start/prerequisites/) (WebView2 уже есть в Windows 11, плюс Microsoft C++ Build Tools).

```bash
pnpm install
pnpm tauri dev     # приложение с горячей перезагрузкой
pnpm dev           # только фронт в браузере, http://localhost:1420
pnpm check         # всё сразу: типы, ESLint, Prettier, тесты, cargo fmt, clippy, cargo test
```

## Сборки

Каждый PR и коммит в `main` собирается на GitHub Actions под Windows. Установщики (`.exe` и `.msi`) лежат в артефактах запуска: вкладка Actions → нужный запуск → Artifacts. Подписи нет, поэтому при первом запуске SmartScreen предупредит: «Подробнее» → «Выполнить в любом случае».
