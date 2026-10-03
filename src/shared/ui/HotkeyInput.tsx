import { useState } from "react";
import { hotkeyFromEvent } from "./hotkey";
import { IconButton } from "./IconButton";

type Props = {
  value: string | null;
  onChange: (hotkey: string | null) => void;
  label: string;
};

/** Поле хоткея: клик — «Нажмите сочетание», Esc — отмена, крестик — убрать. */
export function HotkeyInput({ value, onChange, label }: Props) {
  const [recording, setRecording] = useState(false);
  const [hint, setHint] = useState<string | null>(null);
  const stop = () => {
    setRecording(false);
    setHint(null);
  };
  return (
    <span className="ws-hotkey">
      <button
        type="button"
        className="ws-hotkey__button"
        aria-label={label}
        data-recording={recording || undefined}
        onClick={() => setRecording(true)}
        onBlur={stop}
        onKeyDown={(e) => {
          if (!recording) return;
          e.preventDefault();
          e.stopPropagation();
          if (e.key === "Escape") return stop();
          const result = hotkeyFromEvent(e);
          if (result.kind === "hotkey") {
            onChange(result.hotkey);
            stop();
          } else if (result.kind === "needs-modifier") {
            setHint("Добавьте Ctrl, Alt или Shift");
          } else if (result.kind === "unsupported") {
            setHint("Эту клавишу не взять");
          }
        }}
      >
        {recording ? (hint ?? "Нажмите сочетание…") : (value ?? "Не задано")}
      </button>
      {value && !recording && (
        <IconButton icon="close" label="Убрать сочетание" onClick={() => onChange(null)} />
      )}
    </span>
  );
}
