# Dynamic Island — шаг 0, пробы (WSH-E22) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** До переноса виджетов ответить на четыре вопроса, от которых зависят решения: можно ли сделать тело острова плавным и «сквозным» для кликов (И1), что видно поверх игры и как помнить fullscreen по мониторам (И2), даёт ли Windows оболочке без пакета читать уведомления (И3), как распознавать игры для FPS (И8).

**Architecture:** Чистая логика (пружина, контур с магнитом, раскладка по монитору, fullscreen по мониторам, правило игры) — в `native/Shell/Island/Core/` без ссылок на WinUI и Win32, с юнит-тестами в `native/tests/Island.Tests`. Пробы — отдельные режимы того же exe (`--island-spike`, `--overtop-test`, `--notif-test`, `--games-test`), как уже сделанные `--perf-test` и `--shots-test`: обычный запуск оболочки они не меняют, док и панель задач не трогают, пишут отчёт `*.txt` рядом с exe.

**Tech Stack:** C# 12, .NET 8, WinUI 3 (Windows App SDK 1.8, интерфейс кодом без .xaml), WinUIEx, Win32 P/Invoke, xUnit для чистой логики, PresentMon (уже в `Tools/`).

**Spec:** `docs/superpowers/specs/2026-11-07-dynamic-island-concept.md` (разделы 2, «Motion — принципы», 4 П1/П4/П5/П9, 5 И1/И2/И3/И8, 10). Карта эпиков: `docs/superpowers/plans/2026-11-07-dynamic-island-roadmap.md`.

## Global Constraints

- Цель сборки: `net8.0-windows10.0.22621.0`, `Nullable` включён, `TreatWarningsAsErrors=true` (как `Shell.csproj`).
- `Island/Core/*.cs` — только `System.*`; никакого `Microsoft.UI`, `Windows.*`, `DllImport`. Иначе тесты не соберутся на Linux.
- Подписи, лог и отчёты — по-русски, через `Log.Write`, как во всём коде.
- Обычный запуск оболочки не меняется: ни одна строка пути `App.OnLaunched → ShellHost` не меняет поведение без аргумента пробы.
- Пробы не меняют настройки Windows (масштаб, «Не беспокоить», баннеры, группы). Масштаб 100/150/175 % — только тот, что уже стоит на мониторах mephPC; другой масштаб — только если Георгий сам переключит.
- Пробы не пишут в лог и отчёт содержимое чужих уведомлений, заголовки окон и названия чужих приложений в уведомлениях — только счётчики. Имя exe игры в `--games-test` писать можно (это цель пробы).
- В покое остров не хуже нынешней оболочки: ≈0,02 % ЦП (замер mephPC 2026-10-13).
- Motion (из концепции): ориентир лента ≈200 мс, виджет ≈300 мс, смена виджета ≈250 мс, ничего дольше 400 мс; новый клик перенаправляет движение; наведение 150 мс раскрывает ленту, уход 400 мс сворачивает; магнит — под телом в радиусе ~150 px; reduced motion — без пружин.
- Размеры тела (DIP, из принятого живого эскиза): покой 190×3, лента 560×62, звук 560×262, сеть 600×300, медиа 500×260.
- mephPC: клон `нейронка\windows-shell` остаётся на `main`. Ветку собираем в отдельной папке `нейронка\windows-shell-e22` (git worktree). Скрипт обновления оболочки не запускаем: пробы идут из папки сборки ветки.
- Запуск на mephPC — только через RC и только после «да» Георгия на этот план.

## Review Focus

- Мониторы с разным масштабом и мониторы с отрицательными координатами (слева или сверху от основного): остров по центру верхнего края именно этого монитора → тест `Place_NegativeAndScaledMonitor` в задаче 2.
- Частые клики во время анимации: форма продолжает движение из видимого положения без скачка → тест `Retarget_KeepsValueAndVelocity` в задаче 1.
- Подвисание кадра (dt 0,5 с, окно было скрыто): пружина не «выстреливает» и не даёт NaN → тест `LargeDt_IsStable` в задаче 1.
- Браузер или плеер на весь экран рисует кадры, но это не игра → тест `Browser_FullscreenFrames_IsNotGame` в задаче 4.
- Игра на мониторе 1, клик на мониторе 2: монитор 1 остаётся «в игре» → тест `GameStaysFull_WhenFocusMovesToOtherScreen` в задаче 3.

---

### Task 1: Тестовый проект и пружина

**Files:**
- Create: `native/tests/Island.Tests/Island.Tests.csproj`
- Create: `native/Shell/Island/Core/Spring.cs`
- Test: `native/tests/Island.Tests/SpringTests.cs`
- Modify: `.github/workflows/ci.yml` (job `native`: шаг `dotnet test native/tests/Island.Tests` перед `Publish`)

**Interfaces:**
- Produces:
  - `public readonly record struct SpringParams(double Stiffness, double Damping)` — `Damping` строго от 0 до 1 (недодемпфированная пружина), иначе `ArgumentOutOfRangeException`.
  - `public static class Springs { public static readonly SpringParams Ribbon = new(900, 0.75), Switch = new(700, 0.75), Widget = new(420, 0.75), Highlight = new(520, 0.85); }`
  - `public struct Spring { public double Value; public double Velocity; public double Target; public void Step(double dt, SpringParams p); public bool Settled(double eps); }` — `Step` по точному решению затухающего осциллятора (не Эйлер), поэтому результат не зависит от частоты кадров.

- [ ] **Step 1: Создать `Island.Tests.csproj`**: `net8.0`, `Nullable`, `TreatWarningsAsErrors`, `IsPackable=false`; пакеты `Microsoft.NET.Test.Sdk` 17.11.1, `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2; `<Compile Include="../../Shell/Island/Core/**/*.cs" LinkBase="Core" />`.
- [ ] **Step 2: Написать падающие тесты** в `SpringTests.cs`. Хелпер `static (double settleMs, double overshoot) Run(SpringParams p, double hz)` гоняет `Spring` от 0 к 1 одну секунду; settle — время, после которого |x−1| ≤ 0,02 навсегда.

```csharp
[Theory] [InlineData(60)] [InlineData(144)] [InlineData(240)]
public void Presets_MeetMotionBudget(double hz)
{
    Assert.InRange(Run(Springs.Ribbon, hz).settleMs, 150, 210);
    Assert.InRange(Run(Springs.Switch, hz).settleMs, 200, 260);
    Assert.InRange(Run(Springs.Widget, hz).settleMs, 250, 320);
    foreach (var p in new[] { Springs.Ribbon, Springs.Switch, Springs.Widget })
        Assert.InRange(Run(p, hz).overshoot, 0.01, 0.04);   // лёгкий «долив», не желе
}

[Fact] public void FrameRateIndependent() =>
    Assert.InRange(Math.Abs(Run(Springs.Widget, 60).settleMs - Run(Springs.Widget, 144).settleMs), 0, 17);

[Fact] public void Retarget_KeepsValueAndVelocity()
{   // 10 шагов к 1, затем цель 0: значение и скорость не прыгают
    var s = new Spring { Target = 1 };
    for (var i = 0; i < 10; i++) s.Step(1 / 60.0, Springs.Widget);
    var (v, vel) = (s.Value, s.Velocity);
    s.Target = 0;
    s.Step(1e-6, Springs.Widget);
    Assert.Equal(v, s.Value, 3);
    Assert.Equal(vel, s.Velocity, 0);
}

[Fact] public void LargeDt_IsStable()
{
    var s = new Spring { Target = 1 };
    s.Step(0.5, Springs.Ribbon);
    Assert.False(double.IsNaN(s.Value));
    Assert.InRange(s.Value, 0, 1.04);
}

[Fact] public void OverdampedIsRejected() =>
    Assert.Throws<ArgumentOutOfRangeException>(() => new SpringParams(400, 1.2).Validate());
```

`SpringParams.Validate()` — тоже часть интерфейса (вызывается в `Step`).

- [ ] **Step 3: Запустить** `dotnet test native/tests/Island.Tests` → FAIL (нет `Spring`).
- [ ] **Step 4: Реализовать `Spring.cs`** — точный шаг: e = Value−Target, ω=√k, a=ζω, ωd=ω√(1−ζ²), e' = e^(−a·dt)(e·cos ωd·dt + ((v+a·e)/ωd)·sin ωd·dt), скорость — производная того же выражения. `Settled(eps)`: |e| < eps и |v| < eps·10.
- [ ] **Step 5: Запустить тесты** → PASS. `dotnet build native/check` → 0 ошибок.
- [ ] **Step 6: Commit** `git add native/tests native/Shell/Island/Core/Spring.cs .github/workflows/ci.yml && git commit -m "Остров: пружина и тесты (WSH-E22)"`

### Task 2: Контур тела, магнит и место на мониторе

**Files:**
- Create: `native/Shell/Island/Core/IslandShape.cs`, `native/Shell/Island/Core/Magnet.cs`, `native/Shell/Island/Core/IslandLayout.cs`
- Test: `native/tests/Island.Tests/ShapeTests.cs`

**Interfaces:**
- Consumes: `Spring`, `SpringParams` (задача 1).
- Produces:
  - `public readonly record struct PointD(double X, double Y)`; `public readonly record struct RectI(int Left, int Top, int Right, int Bottom)`.
  - `public enum IslandMode { Rest, Ribbon, Sound, Net, Media }`; `public static class IslandSizes { public static (double W, double H) Of(IslandMode m); }` — значения из Global Constraints.
  - `public sealed class Magnet { public void Update(double dt, double cursorX, double cursorY, bool cursorInside, double bodyW, double bodyH, double centerX, bool rest, bool reduced); public double Offset(double x); public double Amplitude { get; } }` — координаты в DIP относительно верхнего края монитора. Целевые значения как в живом эскизе (`Motion.dc.html`): радиус R = 110 в покое / 160 открыто; maxA = 22 / 46; gap = cursorY − bodyH; активен при 0 < gap < R и |cursorX − centerX| < bodyW/2 + 30; A = min(maxA·(1−gap/R)^1,5, gap·0,82); S = 70 − 40·min(1, A/46); X прижат к centerX ± (bodyW/2 − max(18, min(30, bodyH/2)) − 24). A, S, X — пружины: A (230, 0,32), S (200, 0,7), X (300, 0,75). `Offset(x)` = A·(0,58·e^(−dx²/2s²) + 0,42·e^(−dx²/2(0,42s)²)), s = max(8, S). При `reduced` A = 0.
  - `public static class IslandShape { public static IReadOnlyList<PointD> Outline(double w, double h, double centerX, Magnet magnet, int rimSamples = 64); }` — замкнутый многоугольник: верх на y = −2, бока до h−r, скругления (r = min(28, h/2, w/4), каждое — 6 отрезков), нижний край из `rimSamples` точек с y = h + magnet.Offset(x). w ≥ 40, h ≥ 2.
  - `public static class IslandLayout { public static RectI Place(RectI monitor, double scale, double wDip, double hDip); }` — по центру верхнего края монитора, px = round(dip·scale).

- [ ] **Step 1: Падающие тесты**

```csharp
[Fact] public void Outline_SymmetricWithoutMagnet()
{
    var pts = IslandShape.Outline(560, 62, 0, new Magnet());
    Assert.Equal(-280, pts.Min(p => p.X), 6);
    Assert.Equal(280, pts.Max(p => p.X), 6);
    Assert.Equal(62, pts.Max(p => p.Y), 6);
    Assert.Equal(-2, pts.Min(p => p.Y), 6);
}

[Fact] public void Magnet_PullsTipTowardCursor()
{
    var m = new Magnet();
    for (var i = 0; i < 120; i++) m.Update(1 / 60.0, cursorX: 100, cursorY: 62 + 40, cursorInside: true, 560, 62, 0, rest: false, reduced: false);
    Assert.InRange(m.Amplitude, 10, 46);
    Assert.True(m.Offset(100) > m.Offset(0));        // носик под курсором
    Assert.True(m.Offset(100) > 3 * m.Offset(220));   // узкий носик, широкое основание гаснет
}

[Fact] public void Magnet_SilentOutsideRadiusAndReduced()
{
    var far = new Magnet(); var red = new Magnet();
    for (var i = 0; i < 120; i++)
    {
        far.Update(1 / 60.0, 0, 62 + 200, true, 560, 62, 0, false, false);
        red.Update(1 / 60.0, 0, 62 + 40, true, 560, 62, 0, false, reduced: true);
    }
    Assert.Equal(0, far.Amplitude, 3);
    Assert.Equal(0, red.Amplitude, 3);
}

[Fact] public void Place_NegativeAndScaledMonitor()
{
    var r = IslandLayout.Place(new RectI(-2560, -300, 0, 1140), 1.5, 560, 62);
    Assert.Equal(new RectI(-1700, -300, -860, -207), r);
}
```

- [ ] **Step 2: Запустить** `dotnet test native/tests/Island.Tests` → FAIL.
- [ ] **Step 3: Реализовать** три файла по Interfaces.
- [ ] **Step 4: Запустить** → PASS; `dotnet build native/check` → 0 ошибок.
- [ ] **Step 5: Commit** `"Остров: контур, магнит, место на мониторе (WSH-E22)"`

### Task 3: Fullscreen по каждому монитору

**Files:**
- Create: `native/Shell/Island/Core/FullscreenMap.cs`
- Test: `native/tests/Island.Tests/FullscreenTests.cs`

**Interfaces:**
- Produces:
  - `public static class FullscreenRule { public static bool IsFull(bool exclusiveState, bool coversScreen, bool zoomed, bool resizable); }` — ровно правило из `Ui/Bars.cs` `FullscreenScreens`: `exclusive || (covers && (!zoomed || !resizable))`. `Bars.cs` в этой задаче не меняется (переход — в волне 1).
  - `public sealed class FullscreenMap { public void Observe(string screen, long hwnd, bool full); public void Prune(Func<long, string, bool> stillFull); public bool IsFull(string screen); public IReadOnlyCollection<string> FullScreens { get; } }` — `Observe` зовут для окна на переднем плане (кроме окон самой оболочки). Полное окно запоминается за своим монитором; не полное окно на переднем плане монитора снимает с него отметку; переход фокуса на другой монитор отметку не снимает. `Prune` убирает отметку, если окно закрыто, свёрнуто или больше не закрывает монитор.

- [ ] **Step 1: Падающие тесты**

```csharp
[Theory]
[InlineData(true, false, false, true, true)]    // exclusive D3D
[InlineData(false, true, false, false, true)]   // F11 в браузере: весь экран, без рамки
[InlineData(false, true, true, true, false)]    // развёрнутое окно ChatGPT (WSH-100)
[InlineData(false, false, false, true, false)]  // обычное окно
public void Rule_MatchesBars(bool excl, bool covers, bool zoomed, bool resizable, bool full) =>
    Assert.Equal(full, FullscreenRule.IsFull(excl, covers, zoomed, resizable));

[Fact] public void GameStaysFull_WhenFocusMovesToOtherScreen()
{
    var m = new FullscreenMap();
    m.Observe("A", 1, true);
    m.Observe("B", 2, false);
    Assert.True(m.IsFull("A")); Assert.False(m.IsFull("B"));
}

[Fact] public void ClosedGame_IsPruned()
{
    var m = new FullscreenMap();
    m.Observe("A", 1, true);
    m.Prune((h, s) => false);
    Assert.False(m.IsFull("A"));
}

[Fact] public void TwoFullscreenApps_BothCount()
{
    var m = new FullscreenMap();
    m.Observe("A", 1, true); m.Observe("B", 2, true);
    Assert.Equal(new[] { "A", "B" }, m.FullScreens.OrderBy(x => x));
}

[Fact] public void NormalWindowOnSameScreen_Clears()
{
    var m = new FullscreenMap();
    m.Observe("A", 1, true); m.Observe("A", 3, false);
    Assert.False(m.IsFull("A"));
}
```

- [ ] **Step 2: Запустить** → FAIL. **Step 3: Реализовать.** **Step 4: Запустить** → PASS, `dotnet build native/check` → 0 ошибок.
- [ ] **Step 5: Commit** `"Остров: fullscreen по каждому монитору (WSH-E22)"`

### Task 4: Правило «это игра»

**Files:**
- Create: `native/Shell/Island/Core/GameRule.cs`
- Test: `native/tests/Island.Tests/GameRuleTests.cs`

**Interfaces:**
- Produces: `public enum GameReason { Marked, MarkedNot, Never, Launcher, FullscreenFrames, None }`; `public static class GameRule { public static GameReason Decide(bool? marked, bool inNever, bool launcherKnown, bool fullscreenForeground, bool presentsFrames); public static bool IsGame(GameReason r); }` — порядок: ручная отметка → список «никогда» (`Games.Never`: браузеры, плееры, мессенджеры) → лаунчер/список Windows (`Games.Why` не null) → «на переднем плане в настоящем fullscreen и рисует кадры» → нет. Игра: `Marked`, `Launcher`, `FullscreenFrames`.

- [ ] **Step 1: Падающие тесты**

```csharp
[Theory]
[InlineData(true, true, false, false, false, GameReason.Marked)]
[InlineData(false, false, true, true, true, GameReason.MarkedNot)]
[InlineData(null, false, true, false, false, GameReason.Launcher)]
[InlineData(null, false, false, true, true, GameReason.FullscreenFrames)]  // D:\Games\…, Battle.net, VK Play
[InlineData(null, false, false, true, false, GameReason.None)]             // весь экран, но кадров нет (презентация)
[InlineData(null, false, false, false, true, GameReason.None)]             // рисует, но в окне — не игра автоматически
public void Decide(bool? marked, bool never, bool launcher, bool full, bool frames, GameReason want) =>
    Assert.Equal(want, GameRule.Decide(marked, never, launcher, full, frames));

[Fact] public void Browser_FullscreenFrames_IsNotGame() =>
    Assert.Equal(GameReason.Never, GameRule.Decide(null, inNever: true, false, true, true));
```

- [ ] **Step 2: Запустить** → FAIL. **Step 3: Реализовать.** **Step 4: Запустить** → PASS, `dotnet build native/check` → 0.
- [ ] **Step 5: Commit** `"Остров: правило распознавания игры (WSH-E22)"`

### Task 5: Проба тела острова `--island-spike` (И1)

**Files:**
- Create: `native/Shell/Island/Spike/SpikeArgs.cs`, `native/Shell/Island/Spike/IslandSpike.cs`, `native/Shell/Island/Spike/SpikeMetrics.cs`
- Modify: `native/Shell/App.cs` (`Main`: до мьютекса — если `SpikeArgs.Parse(args)` вернул режим, запустить WinUI-приложение без `Watchdog.Attach`, без мьютекса и без `ShellHost`; `OnLaunched` в этом режиме создаёт `IslandSpike` вместо `ShellHost`)
- Modify: `native/Shell/Native/Win32.cs` (`CreatePolygonRgn`, `WindowFromPoint`, `EnumDisplaySettingsW` для частоты монитора — если их ещё нет)

**Interfaces:**
- Consumes: `Spring`, `Springs`, `Magnet`, `IslandShape.Outline`, `IslandLayout.Place`, `IslandSizes`, `IslandMode` (задачи 1–2); `Ui.ShellWindow(Surface.Transparent, noActivate: false, rounded: false, transitions: false)`; `Native.Monitors`.
- Produces: `public sealed record SpikeOptions(char Strategy /* 'a' | 'b' */, bool Auto)`; `public static class SpikeArgs { public const string Arg = "--island-spike"; public static SpikeOptions? Parse(string[] args); }` — `--island-spike` → `('b', false)`, `--island-spike a --auto` → `('a', true)`. Отчёт `island-spike-{a|b}.txt` рядом с exe.

Поведение пробы (одинаково для обеих стратегий; остров на каждом мониторе):
- Покой: кромка 190×3 DIP. Курсор у самого верха (y < 18 DIP от верха монитора, |x − центр| < 140) 150 мс → лента. Ушёл с ленты на 400 мс → покой. Пока зажата кнопка мыши — наведение не срабатывает (П1).
- Лента: три кнопки-заглушки «Звук», «Сеть», «Медиа» и «×» (закрыть пробу). Клик по кнопке → размер этого виджета (пружина `Springs.Widget`, между виджетами — `Springs.Switch`, лента — `Springs.Ribbon`). Клик вне тела или Escape → покой.
- Форма каждый кадр (`CompositionTarget.Rendering`, только пока что-то движется или курсор в зоне магнита): `Microsoft.UI.Xaml.Shapes.Path` из `IslandShape.Outline`, заливка `#0A0A0B`. Текст и кнопки стоят на месте, деформируется только контур; содержимое проявляется на последней трети пути.
- Стратегия **a**: размер окна = рамка контура + запас под магнит, `SetWindowPos(… SWP_NOACTIVATE | SWP_NOZORDER)` каждый кадр.
- Стратегия **b**: окно неподвижно, размер по самому большому виджету + 60 DIP под магнит; область кликов `SetWindowRgn(CreatePolygonRgn(контур))` в открытом состоянии. В покое окно сквозное для мыши: сначала пробуем `WS_EX_LAYERED | WS_EX_TRANSPARENT` на WinUI-окне (отчёт: «кромка видна: да/нет, клики сквозь: да/нет»); если кромка пропадает — запасной вариант b2: в покое кромку рисует отдельное слоистое окно `UpdateLayeredWindow` (как `Perf/Overlay.cs`), а WinUI-окно скрыто. В отчёте — какой вариант сработал.
- Слежение за курсором — опрос `GetCursorPos` раз в 16 мс в фоновом потоке (как `Ui/Bars.cs`); `Rendering` подписан только во время движения.

`SpikeMetrics` пишет в отчёт:
- частоту каждого монитора и его масштаб;
- для каждого перехода (из → в): длительность до `Settled`, число кадров, кадров длиннее 1,5 периода монитора, самый длинный кадр;
- задержку «нажатие → первый кадр с изменившимся размером» (от `PointerPressed` до `Rendering`);
- сквозные клики: в покое `WindowFromPoint` в точке кромки ≠ окно острова; в открытом — внутри контура = окно острова, в углу окна вне контура ≠ окно острова (для **b**);
- ЦП в покое: 10 с ожидания, затем `Process.TotalProcessorTime` за 60 с → % от всех ядер.

`--auto`: без мыши (для RC) 10 раз проходит покой → лента → звук → сеть → медиа → лента → покой с паузой 1,2 с, затем замер ЦП, отчёт, выход. Без `--auto` проба ждёт руки Георгия и закрывается кнопкой «×» или через 10 минут.

- [ ] **Step 1: `SpikeArgs.Parse` + проводка в `App.cs`.** Проверка: `dotnet build native/check` → 0; обычный путь `Main` без аргумента не изменился (дифф `App.cs` — только ветка пробы до мьютекса и развилка в `OnLaunched`).
- [ ] **Step 2: `IslandSpike` со стратегией b** (окно, контур, наведение, лента, виджеты-заглушки, Escape/клик вне). Проверка: `dotnet build native/check` → 0.
- [ ] **Step 3: Стратегия a** в том же классе (развилка по `SpikeOptions.Strategy` только в «как двигать окно»). Проверка: сборка → 0.
- [ ] **Step 4: `SpikeMetrics` и `--auto`.** Проверка: сборка → 0.
- [ ] **Step 5: Commit** `"Остров: проба тела --island-spike (И1, WSH-E22)"`

### Task 6: Проба «поверх игры» `--overtop-test` (И2)

**Files:**
- Create: `native/Shell/Island/Spike/OvertopTest.cs`, `native/Shell/Island/Spike/Pill.cs`
- Modify: `native/Shell/App.cs` (`if (Island.Spike.OvertopTest.Cli(args)) return;` рядом с другими самопроверками)

**Interfaces:**
- Consumes: `FullscreenRule`, `FullscreenMap` (задача 3).
- Produces: `public static class OvertopTest { public const string Arg = "--overtop-test"; public static bool Cli(string[] args); }`; `internal sealed class Pill : IDisposable { public void Show(RectI monitor, double scale, string text); public void Hide(); }` — слоистое окно поверх всех (`UpdateLayeredWindow`, `WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT`, `WDA_EXCLUDEFROMCAPTURE`), красная пилюля «● запись 0:12» по центру верхнего края, фокус не берёт.

Поведение: 120 с (или `--overtop-test 300`). Раз в 250 мс: окно на переднем плане (не своё) → `FullscreenRule.IsFull` по тем же признакам, что `Bars.FullscreenScreens` → `FullscreenMap.Observe`; `Prune` с проверкой `IsWindow && !IsIconic && закрывает монитор`. На каждом мониторе из `FullScreens` показана пилюля с идущим таймером; раз в секунду пилюле заново ставится `HWND_TOPMOST`. В отчёт `overtop-test.txt` и в лог — каждое изменение: монитор, имя exe, `SHQueryUserNotificationState`, решение, «пилюля показана». Известно заранее (`Charge/ChargeBanner.cs`): в эксклюзивном режиме плашка не видна — проба это подтверждает или опровергает.

- [ ] **Step 1: Реализовать `Pill` и `OvertopTest`.** Проверка: `dotnet build native/check` → 0.
- [ ] **Step 2: Commit** `"Остров: проба поверх игры --overtop-test (И2, WSH-E22)"`

### Task 7: Проба уведомлений `--notif-test` (И3)

**Files:**
- Create: `native/Shell/Island/Spike/NotifTest.cs`
- Modify: `native/Shell/App.cs` (`if (Island.Spike.NotifTest.Cli(args)) return;`)
- Modify: `native/Shell/Tail/InputLanguage.cs` (сделать `QuietMode()` `internal static`, если он не такой; поведение не менять)

**Interfaces:**
- Consumes: `Native.Toasts.Show`, `Native.Toasts.Aumid` (`"LeDoksi.WindowsShell"`).
- Produces: `public static class NotifTest { public const string Arg = "--notif-test"; public static bool Cli(string[] args); }`; внутренний отчёт `sealed record NotifReport(string Access, bool EventsSupported, string? EventsError, int OthersCount, int OthersWithAumid, bool OwnSeen, int OwnLatencyMs, bool OwnTextReadable, bool OwnRemoved, bool WindowsQuiet)` — **в нём нет ни одного поля с текстом чужих уведомлений или именами приложений**; отчёт `notif-test.txt` пишется только из этой записи.

Шаги пробы (в фоновом потоке, `Main` ждёт до 30 с):
1. `UserNotificationListener.Current.RequestAccessAsync()` и `GetAccessStatus()` → `Access`.
2. Подписка `NotificationChanged` в try/catch → `EventsSupported` / `EventsError` (у приложений без пакета подписка может бросать исключение).
3. `GetNotificationsAsync(Toast)` → только количества: всего и сколько с непустым `AppInfo.AppUserModelId`.
4. Свой тост `Toasts.Show("Проверка уведомлений", "Это проба оболочки, можно закрыть", silent: true)`; ловим событием или опросом раз в 250 мс до 15 с тост с нашим AUMID → задержка; читаем его текст и сравниваем с отправленным; `RemoveNotification(id)` своего тоста → исчез ли он из списка.
5. `QuietMode()` — включено ли «Не беспокоить» Windows (только чтение).

- [ ] **Step 1: Реализовать `NotifTest`.** Проверка: `dotnet build native/check` → 0; поиск по файлу: `Log.Write`/запись отчёта не получают `Title`, `Text`, `DisplayName` чужих уведомлений.
- [ ] **Step 2: Commit** `"Остров: проба доступа к уведомлениям --notif-test (И3, WSH-E22)"`

### Task 8: Проба распознавания игр и FPS `--games-test` (И8)

**Files:**
- Create: `native/Shell/Island/Spike/GamesTest.cs`
- Modify: `native/Shell/App.cs` (`if (Island.Spike.GamesTest.Cli(args)) return;`)

**Interfaces:**
- Consumes: `GameRule.Decide`, `FullscreenRule.IsFull` (задачи 3–4); `Perf.Games.Refresh(wait: true)`, `Perf.Games.Why`, `Perf.Games.Roots`, `Perf.Games.KnownCount`; `Perf.Frames.Track(int pid)`, `Frames.Fps`, `Frames.AccessDenied`; ручные отметки — из `ShellConfig.Load()` настроек монитора ПК (только чтение).
- Produces: `public static class GamesTest { public const string Arg = "--games-test"; public static bool Cli(string[] args); }`; отчёт `games-test.txt`.

Поведение (90 с или `--games-test 300`):
1. Права: состоит ли пользователь в группе Performance Log Users (`WindowsIdentity.GetCurrent().Groups` содержит `S-1-5-32-559`) — «да / нет (членство действует только после полного выхода из системы)».
2. `Games.Refresh(wait: true)` → папок лаунчеров N, игр в списке Windows M (та же строка, что в логе оболочки).
3. Раз в секунду — окно на переднем плане (корневой владелец, не своё): имя exe, `FullscreenRule.IsFull`, `Games.Why`. Для каждого нового exe, который сейчас на весь экран: `Frames.Track(pid)` на 5 с → рисует ли кадры (`Fps != null`), сколько FPS, `AccessDenied`. Итог по exe: `GameRule.Decide(...)` старым правилом (`Games.Why != null`) и новым — в одну строку «старое: не игра → новое: игра (весь экран + кадры)».

- [ ] **Step 1: Реализовать `GamesTest`.** Проверка: `dotnet build native/check` → 0.
- [ ] **Step 2: Commit** `"Остров: проба распознавания игр --games-test (И8, WSH-E22)"`

### Task 9: PR и пробы на mephPC

**Files:**
- Modify: `docs/superpowers/specs/2026-11-07-dynamic-island-concept.md` (в И1, И2, И3, И8 — абзац «Итог пробы 2026-…» с цифрами из отчётов и выводом)
- Modify: `/mnt/project-files/windows-shell/handoff.md` (раздел E22), память проекта

- [ ] **Step 1: PR** из ветки эпика в `main`; CI зелёный (`dotnet test` + публикация).
- [ ] **Step 2: Сборка ветки на mephPC через RC** в отдельной папке `нейронка\windows-shell-e22` (git worktree от клона; клон остаётся на `main`), `dotnet publish native/Shell/Shell.csproj -c Release -r win-x64 -o …\publish-e22`.
- [ ] **Step 3: Прочитать в логе оболочки строку** `уведомления: доступ …` (только эту строку, `findstr`).
- [ ] **Step 4: RC запускает** `--island-spike b --auto`, затем `--island-spike a --auto`, затем `--notif-test`. Ожидается: три отчёта с заполненными разделами и строкой «ГОТОВО».
- [ ] **Step 5: Руками Георгия** (попросить в треде, с точными шагами): `--island-spike` (живой взгляд, видео по желанию); `--games-test 300` и `--overtop-test 300`, пока он запускает одну-две свои игры в оконном/«полноэкранном» режиме и одну в эксклюзивном и кликает на второй монитор.
- [ ] **Step 6: GO/NO-GO по И1** — по критериям концепции: ни одного кадра длиннее 1,5 периода при смене формы на масштабах mephPC; задержка нажатия ≤ 16 мс; сквозные клики — все «да»; ЦП в покое не выше ≈0,02 %; Георгий сказал «да». Выбрать стратегию (a или b/b2). Если обе не проходят — `request_decision` Георгию: упростить motion или остановить остров.
- [ ] **Step 7: Итоги в концепцию** (И1/И2/И3/И8), при NO-GO по И3 — выбор Георгию: пакетная идентичность (sparse package) в волне 4 или оставить баннеры Windows.
- [ ] **Step 8: Слияние PR** после своих проверок (код проб срабатывает только по аргументу, обычный запуск не меняется); RC подтягивает `main` в клон mephPC; handoff, канбан (WSH-E22 → done), память.
