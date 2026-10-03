import { describe, expect, it } from "vitest";
import { hotkeyFromEvent, type KeyPress } from "./hotkey";

const press = (key: string, code: string, mods: Partial<KeyPress> = {}): KeyPress => ({
  key,
  code,
  ctrlKey: false,
  altKey: false,
  shiftKey: false,
  metaKey: false,
  ...mods,
});

describe("hotkeyFromEvent", () => {
  it("builds Rust-compatible strings", () => {
    expect(hotkeyFromEvent(press("1", "Digit1", { ctrlKey: true, altKey: true }))).toEqual({
      kind: "hotkey",
      hotkey: "Ctrl+Alt+1",
    });
    // Русская раскладка: буква по физической клавише.
    expect(hotkeyFromEvent(press("л", "KeyK", { shiftKey: true, ctrlKey: true }))).toEqual({
      kind: "hotkey",
      hotkey: "Ctrl+Shift+K",
    });
    expect(hotkeyFromEvent(press("ArrowUp", "ArrowUp", { altKey: true }))).toEqual({
      kind: "hotkey",
      hotkey: "Alt+Up",
    });
    expect(hotkeyFromEvent(press("F9", "F9"))).toEqual({ kind: "hotkey", hotkey: "F9" });
  });

  it("waits for a key and refuses bare keys", () => {
    expect(hotkeyFromEvent(press("Control", "ControlLeft", { ctrlKey: true }))).toEqual({
      kind: "waiting",
    });
    expect(hotkeyFromEvent(press("k", "KeyK"))).toEqual({ kind: "needs-modifier" });
    expect(hotkeyFromEvent(press("Tab", "Tab", { ctrlKey: true }))).toEqual({
      kind: "unsupported",
    });
  });
});
