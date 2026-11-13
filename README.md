# Windows Shell

Персональная оболочка поверх Windows 11: док на каждом мониторе, свой Пуск, остров у верхнего края
(звук, медиа, сеть, снимки, буфер, уведомления, монитор ПК, канбан) и замена панели задач с треем.
C# + WinUI 3, всё локально.

Код и описание устройства — в [native/README.md](native/README.md). Поведение — в `behaviour-spec.md`,
`design-v2.md` и [docs/superpowers](docs/superpowers) (остров).

## Сборка

Нужен .NET 8 SDK.

```
dotnet publish native/Shell/Shell.csproj -c Release -r win-x64 -o native/publish
dotnet test native/tests/Island.Tests
```

Обновить установленную оболочку на ПК: `scripts\update-shell.cmd` (останавливает оболочку и сторожа,
собирает, запускает снова). Аварийный возврат панели задач: `scripts\restore-taskbar.cmd`.

Каждый PR и коммит в `main` собирается на GitHub Actions под Windows; сборка лежит в артефакте
`windows-shell-native-<коммит>`.

## История

Первая версия была на Tauri 2 + React (этапы E1–E12). В WSH-E13/E14 оболочка переехала на WinUI 3;
код старой версии удалён в WSH-E27 (он остался в истории git до коммита 8b4d812). Документы того
времени лежат в [docs](docs) как история решений.
