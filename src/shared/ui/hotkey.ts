/** Нажатие клавиш → строка хоткея, которую понимает Rust (`src-tauri/src/hotkeys.rs`). */
export type KeyPress = {
  key: string;
  code: string;
  ctrlKey: boolean;
  altKey: boolean;
  shiftKey: boolean;
  metaKey: boolean;
};

const named: Record<string, string> = {
  Space: "Space",
  PageUp: "PageUp",
  PageDown: "PageDown",
  Home: "Home",
  End: "End",
  Insert: "Insert",
  Delete: "Delete",
  ArrowUp: "Up",
  ArrowDown: "Down",
  ArrowLeft: "Left",
  ArrowRight: "Right",
};

/** Клавиша без модификаторов; `null` — модификатор сам по себе или клавиша, которую не берём. */
function keyName(e: KeyPress): string | null {
  const letter = /^Key([A-Z])$/.exec(e.code);
  if (letter) return letter[1];
  const digit = /^(?:Digit|Numpad)(\d)$/.exec(e.code);
  if (digit) return digit[1];
  if (/^F([1-9]|1\d|2[0-4])$/.test(e.key)) return e.key;
  return named[e.code] ?? named[e.key] ?? null;
}

export type HotkeyResult =
  | { kind: "hotkey"; hotkey: string }
  /** Нажали только модификаторы — ждём клавишу. */
  | { kind: "waiting" }
  /** Обычная клавиша без Ctrl/Alt/Shift отняла бы её у всех программ. */
  | { kind: "needs-modifier" }
  | { kind: "unsupported" };

export function hotkeyFromEvent(e: KeyPress): HotkeyResult {
  if (["Control", "Alt", "Shift", "Meta", "OS"].includes(e.key)) return { kind: "waiting" };
  const key = keyName(e);
  if (!key) return { kind: "unsupported" };
  const mods = [e.ctrlKey && "Ctrl", e.altKey && "Alt", e.shiftKey && "Shift", e.metaKey && "Win"]
    .filter(Boolean)
    .join("+");
  if (!mods && !/^F\d+$/.test(key)) return { kind: "needs-modifier" };
  return { kind: "hotkey", hotkey: mods ? `${mods}+${key}` : key };
}
