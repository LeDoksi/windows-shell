import { describe, expect, it } from "vitest";
import type { WindowInfo } from "../shared/backend";
import { defaultConfig } from "../shared/config";
import { appMenu } from "./menu";
import {
  buildDock,
  clickAction,
  launchTarget,
  movePin,
  pinApp,
  programsFrom,
  repairPins,
  unpinApp,
  type DockEntry,
} from "./model";

const win = (hwnd: number, app: Partial<WindowInfo> = {}): WindowInfo => ({
  hwnd,
  title: `Окно ${hwnd}`,
  aumid: null,
  exe: null,
  appName: "",
  minimized: false,
  active: false,
  attention: false,
  seq: hwnd,
  focused: 0,
  ...app,
});

const pins = [
  { id: "chrome", title: "Chrome", target: "Chrome" },
  { id: "tg", title: "Telegram", target: "TelegramDesktop" },
  { id: "notes", title: "Заметки", target: "C:\\Apps\\notes.exe" },
];
const chromeExe = "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe";

describe("buildDock", () => {
  it("finds windows of pinned apps by AUMID or by exe", () => {
    const targets = new Map([["Chrome", { exists: true, exe: chromeExe }]]);
    const dock = buildDock(
      pins,
      [
        win(1, { exe: chromeExe.toUpperCase() }),
        win(2, { aumid: "telegramdesktop", active: true }),
        win(3, { exe: "c:\\apps\\NOTES.exe" }),
      ],
      targets,
    );
    expect(dock.pinned.map((e) => [e.id, e.state, e.windows.map((w) => w.hwnd)])).toEqual([
      ["chrome", "running", [1]],
      ["tg", "active", [2]],
      ["notes", "running", [3]],
    ]);
    expect(dock.transient).toEqual([]);
  });

  it("matches by AUMID before exe", () => {
    const dock = buildDock(
      [{ id: "chrome", title: "Chrome", target: chromeExe }],
      [win(1, { exe: chromeExe }), win(2, { exe: chromeExe, aumid: "Chrome._crx_mail" })],
    );
    // PWA тоже chrome.exe: AUMID ей не помог, exe совпал — уходит к Chrome.
    expect(dock.pinned[0].windows.map((w) => w.hwnd)).toEqual([1, 2]);

    const byAumid = buildDock(
      [{ id: "mail", title: "Почта", target: "Chrome._crx_mail" }, ...pins],
      [
        win(1, { exe: chromeExe, aumid: "Chrome" }),
        win(2, { exe: chromeExe, aumid: "Chrome._crx_mail" }),
      ],
      new Map([["Chrome", { exists: true, exe: chromeExe }]]),
    );
    expect(byAumid.pinned[0].windows.map((w) => w.hwnd)).toEqual([2]);
    expect(byAumid.pinned[1].windows.map((w) => w.hwnd)).toEqual([1]);
  });

  it("groups unpinned apps and keeps them in order of appearance", () => {
    const dock = buildDock(pins, [
      win(9, { exe: "C:\\b.exe", appName: "Б", seq: 2, focused: 1 }),
      win(8, { exe: "C:\\a.exe", appName: "А", seq: 1 }),
      win(7, { exe: "C:\\B.EXE", seq: 3, focused: 5 }),
    ]);
    expect(dock.transient.map((e) => [e.title, e.windows.map((w) => w.hwnd)])).toEqual([
      ["А", [8]],
      ["Б", [7, 9]],
    ]);
  });

  it("attention wins over active", () => {
    const dock = buildDock(pins, [
      win(1, { aumid: "Chrome", active: true }),
      win(2, { aumid: "Chrome", attention: true }),
    ]);
    expect(dock.pinned[0].state).toBe("attention");
  });

  it("marks pinned apps that are not installed", () => {
    const targets = new Map([
      ["Chrome", { exists: false, exe: null }],
      ["TelegramDesktop", { exists: true, exe: null }],
    ]);
    const dock = buildDock(pins, [], targets);
    expect(dock.pinned.map((e) => e.missing)).toEqual([true, false, false]);
  });
});

describe("launchTarget", () => {
  it("prefers exe, but packaged apps start by AUMID", () => {
    expect(launchTarget(win(1, { exe: "C:\\a.exe", aumid: "A" }))).toBe("C:\\a.exe");
    expect(
      launchTarget(
        win(1, { exe: "C:\\Program Files\\WindowsApps\\calc\\calc.exe", aumid: "Calc!App" }),
      ),
    ).toBe("Calc!App");
    expect(
      launchTarget(
        win(1, { exe: "C:\\Windows\\System32\\ApplicationFrameHost.exe", aumid: "Xbox!App" }),
      ),
    ).toBe("Xbox!App");
  });
});

const entry = (windows: WindowInfo[]): DockEntry => ({
  id: "a",
  title: "A",
  pinned: true,
  state: "idle",
  target: "A",
  windows,
});

describe("clickAction", () => {
  it("launches, activates the last window, minimizes or lists windows", () => {
    expect(clickAction(entry([]))).toEqual({ kind: "launch" });
    expect(clickAction(entry([win(2), win(1)]))).toEqual({ kind: "activate", hwnd: 2 });
    expect(clickAction(entry([win(1, { active: true })]))).toEqual({ kind: "minimize", hwnd: 1 });
    expect(clickAction(entry([win(1, { active: true }), win(2)]))).toEqual({ kind: "menu" });
  });
});

describe("pin and unpin", () => {
  it("adds a pin with a unique id and removes it", () => {
    const app = { ...entry([win(1)]), title: "Figma", pinned: false };
    let config = pinApp(defaultConfig, app, "C:\\Figma.exe");
    config = pinApp(config, app, "D:\\Figma.exe");
    const added = config.dock.pinned.slice(-2);
    expect(added.map((p) => p.id)).toEqual(["app-figma", "app-figma-2"]);
    expect(added[0]).toMatchObject({ title: "Figma", target: "C:\\Figma.exe" });
    expect(unpinApp(config, "app-figma").dock.pinned.map((p) => p.id)).not.toContain("app-figma");
  });

  it("does not pin the same target twice", () => {
    const config = pinApp(defaultConfig, { title: "Figma" }, "C:\\Figma.exe");
    expect(pinApp(config, { title: "Figma" }, "c:\\figma.EXE")).toBe(config);
  });

  it("pins at a place and reorders pins", () => {
    const config = pinApp(defaultConfig, { title: "Figma" }, "C:\\Figma.exe", 1);
    const ids = config.dock.pinned.map((p) => p.id);
    expect(ids[1]).toBe("app-figma");
    const moved = movePin(config, "app-figma", 0).dock.pinned.map((p) => p.id);
    expect(moved).toEqual(["app-figma", ids[0], ...ids.slice(2)]);
    expect(movePin(config, "nope", 0)).toBe(config);
    expect(movePin(config, "app-figma", 1)).toBe(config);
  });
});

describe("programsFrom", () => {
  it("keeps programs and shortcuts, named without extension", () => {
    expect(
      programsFrom([
        "C:\\Apps\\Figma.exe",
        "D:\\Фото",
        "C:\\Desktop\\Steam.lnk",
        "a.txt",
        "C:\\Desktop\\Stardew Valley.url",
      ]),
    ).toEqual([
      { title: "Figma", target: "C:\\Apps\\Figma.exe" },
      { title: "Steam", target: "C:\\Desktop\\Steam.lnk" },
      { title: "Stardew Valley", target: "C:\\Desktop\\Stardew Valley.url" },
    ]);
  });
});

describe("appMenu", () => {
  const noop = () => {};
  const actions = { activate: noop, launch: noop, pin: noop, unpin: noop, close: noop };
  const labels = (e: DockEntry) =>
    appMenu(e, actions).map((m) => (m.kind === "separator" ? "—" : m.label));

  it("lists windows, then new window, pin and close", () => {
    const e = entry([win(1, { title: "Почта" }), win(2, { title: "Почта" })]);
    expect(labels(e)).toEqual([
      "Почта",
      "Почта",
      "—",
      "Новое окно",
      "Убрать из дока",
      "—",
      "Закрыть все окна (2)",
    ]);
    expect(labels({ ...entry([]), pinned: false })).toEqual(["Открыть", "Закрепить в доке"]);
  });
});

describe("repairPins", () => {
  const apps = [
    { name: "Steam", id: "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\\Steam\\Steam.exe" },
    { name: "Notes", id: "a" },
    { name: "Notes", id: "b" },
  ];
  it("moves a broken pin to the only Start app with the same name", () => {
    const fixes = repairPins(
      [
        { id: "steam", title: "steam", target: "Valve.Steam.Client" },
        { id: "notes", title: "Notes", target: "x" },
        { id: "gone", title: "Gone", target: "y" },
      ],
      apps,
    );
    expect([...fixes]).toEqual([["steam", apps[0].id]]);
  });
});

describe("Explorer pin", () => {
  it("owns explorer.exe windows that have no AUMID", () => {
    const pins = [{ id: "explorer", title: "Проводник", target: "Microsoft.Windows.Explorer" }];
    const { pinned, transient } = buildDock(pins, [win(1, { exe: "C:\\Windows\\explorer.exe" })]);
    expect(pinned[0].windows.map((w) => w.hwnd)).toEqual([1]);
    expect(transient).toEqual([]);
  });
});
