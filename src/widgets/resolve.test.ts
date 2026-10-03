import { describe, expect, it } from "vitest";
import { defaultConfig, type Config } from "../shared/config";
import {
  inSlot,
  moveWidget,
  resolveWidgets,
  setWidgetEnabled,
  updateWidgetSettings,
} from "./resolve";
import { defineWidget, type AnyWidget } from "./types";

const Compact = () => null;
const widget = (id: string, slot: "launcher" | "dock-tail", defaults: object = {}) =>
  defineWidget({ id, name: id, description: "", icon: "music", slot, defaults, Compact });

const registry: AnyWidget[] = [
  widget("audio", "dock-tail", { step: 2 }),
  widget("media", "launcher"),
  widget("cpu", "launcher"),
];

const config = (widgets: Config["widgets"]): Config => ({ ...defaultConfig, widgets });

describe("resolveWidgets", () => {
  it("keeps config order, fills defaults and appends new widgets", () => {
    const list = resolveWidgets(
      [
        { id: "media", enabled: false, settings: {} },
        { id: "gone", enabled: true, settings: {} },
        { id: "audio", enabled: true, settings: { step: 5 } },
      ],
      registry,
    );
    expect(list.map((w) => [w.def.id, w.enabled])).toEqual([
      ["media", false],
      ["audio", true],
      ["cpu", true],
    ]);
    expect(list[1].settings).toEqual({ step: 5 });
    expect(list[2].settings).toEqual({});
  });

  it("filters a slot to enabled widgets", () => {
    const list = resolveWidgets([{ id: "media", enabled: false, settings: {} }], registry);
    expect(inSlot(list, "launcher").map((w) => w.def.id)).toEqual(["cpu"]);
    expect(inSlot(list, "dock-tail").map((w) => w.def.id)).toEqual(["audio"]);
  });
});

describe("changing widgets", () => {
  const start = config([
    { id: "audio", enabled: true, settings: {} },
    { id: "gone", enabled: true, settings: { x: 1 } },
    { id: "media", enabled: true, settings: {} },
  ]);

  it("toggles and keeps entries of unknown widgets", () => {
    const next = setWidgetEnabled(start, registry, "media", false);
    expect(next.widgets).toEqual([
      { id: "audio", enabled: true, settings: {} },
      { id: "media", enabled: false, settings: {} },
      { id: "cpu", enabled: true, settings: {} },
      { id: "gone", enabled: true, settings: { x: 1 } },
    ]);
  });

  it("moves within the slot only", () => {
    const right = moveWidget(start, registry, "media", 1);
    expect(right.widgets.map((w) => w.id)).toEqual(["audio", "cpu", "media", "gone"]);
    const back = moveWidget(right, registry, "media", -1);
    expect(back.widgets.map((w) => w.id)).toEqual(["audio", "media", "cpu", "gone"]);
    // Первый в слоте левее не уходит, и через виджет другого слота не перепрыгивает.
    expect(moveWidget(start, registry, "media", -1).widgets.map((w) => w.id)).toEqual([
      "audio",
      "media",
      "cpu",
      "gone",
    ]);
  });

  it("writes settings with defaults", () => {
    const next = updateWidgetSettings(start, registry, "audio", (s) => ({ ...s, showApps: false }));
    expect(next.widgets[0]).toEqual({
      id: "audio",
      enabled: true,
      settings: { step: 2, showApps: false },
    });
  });
});
