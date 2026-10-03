import { useState } from "react";
import { HotkeyInput, IconButton, Toggle } from "../../shared/ui";
import { cx } from "../../shared/ui/cx";
import type { WidgetProps } from "../types";
import type { AudioProfile, AudioSettings as Settings } from "./settings";

/** Настройки звука: шаг колеса, что показывать, профили и их хоткеи. */
export function AudioSettings({ settings, updateSettings }: WidgetProps<Settings>) {
  const setProfile = (id: string, change: Partial<AudioProfile>) =>
    updateSettings((s) => ({
      ...s,
      profiles: s.profiles.map((p) => (p.id === id ? { ...p, ...change } : p)),
    }));
  return (
    <div className="ws-widget-settings__body">
      <div className="ws-flyout__row">
        <span>Шаг колеса мыши</span>
        <div className="ws-audio__steps" role="radiogroup" aria-label="Шаг колеса мыши">
          {[1, 2, 5, 10].map((n) => (
            <button
              key={n}
              type="button"
              role="radio"
              aria-checked={settings.step === n}
              className={cx("ws-chip", settings.step === n && "ws-chip--active")}
              onClick={() => updateSettings((s) => ({ ...s, step: n }))}
            >
              {n}%
            </button>
          ))}
        </div>
      </div>
      <div className="ws-flyout__row">
        <span>Громкость приложений</span>
        <Toggle
          checked={settings.showApps}
          onChange={(showApps) => updateSettings((s) => ({ ...s, showApps }))}
          label="Показывать громкость приложений"
        />
      </div>
      <div className="ws-flyout__row">
        <span>Микрофон</span>
        <Toggle
          checked={settings.showMic}
          onChange={(showMic) => updateSettings((s) => ({ ...s, showMic }))}
          label="Показывать микрофон"
        />
      </div>
      <div className="ws-flyout__row">
        <span>Следующее устройство</span>
        <HotkeyInput
          value={settings.nextOutputHotkey}
          onChange={(nextOutputHotkey) => updateSettings((s) => ({ ...s, nextOutputHotkey }))}
          label="Сочетание: следующее устройство вывода"
        />
      </div>
      <div className="ws-flyout__section">Профили</div>
      {settings.profiles.length === 0 && (
        <div className="ws-flyout__hint">
          Профиль запоминает устройство и громкость: выберите их в виджете звука и нажмите
          «Запомнить как профиль».
        </div>
      )}
      {settings.profiles.map((p) => (
        <div key={p.id} className="ws-flyout__row ws-profile-row">
          <ProfileName key={p.name} name={p.name} onChange={(name) => setProfile(p.id, { name })} />
          <HotkeyInput
            value={p.hotkey}
            onChange={(hotkey) => setProfile(p.id, { hotkey })}
            label={`Сочетание для профиля «${p.name}»`}
          />
          <IconButton
            icon="trash"
            label={`Удалить профиль «${p.name}»`}
            onClick={() =>
              updateSettings((s) => ({ ...s, profiles: s.profiles.filter((x) => x.id !== p.id) }))
            }
          />
        </div>
      ))}
    </div>
  );
}

/** Название пишем в конфиг, когда закончили печатать: иначе быстрые нажатия теряются. */
function ProfileName({ name, onChange }: { name: string; onChange: (name: string) => void }) {
  const [draft, setDraft] = useState(name);
  const commit = () => {
    const next = draft.trim();
    if (next && next !== name) onChange(next);
    else setDraft(name);
  };
  return (
    <input
      className="ws-input"
      value={draft}
      aria-label="Название профиля"
      onChange={(e) => setDraft(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => e.key === "Enter" && e.currentTarget.blur()}
    />
  );
}
