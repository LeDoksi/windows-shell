import { backend } from "../../shared/backend";
import { Icon, IconButton, Slider } from "../../shared/ui";
import { cx } from "../../shared/ui/cx";
import { NowPlaying } from "../media/NowPlaying";
import { useMedia } from "../media/useMedia";
import type { WidgetProps } from "../types";
import { PeakBar, SessionIcon } from "./parts";
import { activeProfile, profileFromState, volumeIcon, type AudioSettings } from "./settings";
import { useAudio } from "./useAudio";
import { useMixer } from "./useMixer";

/** Flyout звука: устройства, громкость, профили, приложения, микрофон, «сейчас играет». */
export function AudioFlyout({ settings, updateSettings }: WidgetProps<AudioSettings>) {
  const { state, setVolume, setMute, setDefault, applyProfile } = useAudio();
  const { mixer, setSession } = useMixer();
  const { media, control } = useMedia();
  if (!state) return null;
  const { output, input } = state;
  const active = activeProfile(settings.profiles, state);
  const sessions = mixer?.sessions ?? [];

  return (
    <>
      {media && <NowPlaying media={media} onControl={control} size="small" />}

      <div className="ws-flyout__section">Вывод</div>
      {output.devices.length === 0 ? (
        <div className="ws-flyout__hint">Нет устройств вывода</div>
      ) : (
        <div role="radiogroup" aria-label="Устройство вывода" className="ws-flyout__list">
          {output.devices.map((d) => (
            <button
              key={d.id}
              type="button"
              role="radio"
              aria-checked={d.id === output.defaultId}
              className="ws-flyout__option"
              onClick={() => setDefault("output", d.id)}
            >
              <span className="ws-radio" aria-hidden="true" />
              <span className="ws-flyout__option-text">{d.name}</span>
            </button>
          ))}
        </div>
      )}
      {output.defaultId && (
        <div className="ws-audio__level">
          <div className="ws-flyout__row ws-flyout__row--volume">
            <IconButton
              icon={volumeIcon(output.volume, output.muted)}
              label={output.muted ? "Включить звук" : "Выключить звук"}
              aria-pressed={output.muted}
              onClick={() => setMute("output", !output.muted)}
            />
            <Slider
              value={output.volume}
              onChange={(v) => setVolume("output", v)}
              label="Общая громкость"
              wheelStep={settings.step}
            />
            <span className="ws-flyout__value">{output.volume}%</span>
          </div>
          <PeakBar peak={output.muted ? 0 : (mixer?.outputPeak ?? 0)} />
        </div>
      )}

      <div className="ws-audio__profiles" aria-label="Профили">
        {settings.profiles.map((p) => (
          <button
            key={p.id}
            type="button"
            className={cx("ws-chip", p.id === active && "ws-chip--active")}
            aria-pressed={p.id === active}
            title={p.hotkey ? `${p.name} · ${p.hotkey}` : p.name}
            disabled={!output.devices.some((d) => d.id === p.deviceId)}
            onClick={() => applyProfile(p.deviceId, p.volume)}
          >
            {p.name}
          </button>
        ))}
        {output.defaultId && !active && (
          <button
            type="button"
            className="ws-chip ws-chip--add"
            onClick={() => {
              const profile = profileFromState(state, settings.profiles);
              if (profile) updateSettings((s) => ({ ...s, profiles: [...s.profiles, profile] }));
            }}
          >
            <Icon name="plus" size={14} />
            Запомнить как профиль
          </button>
        )}
      </div>

      {settings.showApps && sessions.length > 0 && (
        <>
          <div className="ws-flyout__section">Приложения</div>
          <ul className="ws-audio__sessions" aria-label="Громкость приложений">
            {sessions.map((s) => (
              <li key={s.id} className="ws-session">
                <SessionIcon exe={s.exe} />
                <div className="ws-session__body">
                  <div className="ws-session__name" title={s.name}>
                    {s.name}
                  </div>
                  <Slider
                    value={s.volume}
                    onChange={(volume) => setSession(s.id, { volume, muted: false })}
                    label={`Громкость: ${s.name}`}
                    wheelStep={settings.step}
                  />
                  <PeakBar peak={s.muted ? 0 : s.peak} />
                </div>
                <span className="ws-flyout__value">{s.volume}%</span>
                <IconButton
                  icon={s.muted ? "speakerMuted" : "speaker"}
                  label={s.muted ? `Включить звук: ${s.name}` : `Выключить звук: ${s.name}`}
                  aria-pressed={s.muted}
                  onClick={() => setSession(s.id, { muted: !s.muted })}
                />
              </li>
            ))}
          </ul>
        </>
      )}

      {settings.showMic && input.defaultId && (
        <>
          <div className="ws-flyout__section">Микрофон</div>
          {/* Списком, а не выпадающим меню: окно flyout по размеру карточки и обрезало бы его. */}
          {input.devices.length > 1 ? (
            <div role="radiogroup" aria-label="Микрофон" className="ws-flyout__list">
              {input.devices.map((d) => (
                <button
                  key={d.id}
                  type="button"
                  role="radio"
                  aria-checked={d.id === input.defaultId}
                  className="ws-flyout__option"
                  onClick={() => setDefault("input", d.id)}
                >
                  <span className="ws-radio" aria-hidden="true" />
                  <span className="ws-flyout__option-text">{d.name}</span>
                </button>
              ))}
            </div>
          ) : (
            <div className="ws-flyout__hint">{input.devices[0]?.name}</div>
          )}
          <div className="ws-audio__level">
            <div className="ws-flyout__row ws-flyout__row--volume">
              <IconButton
                icon={input.muted ? "micMuted" : "mic"}
                label={input.muted ? "Включить микрофон" : "Выключить микрофон"}
                aria-pressed={input.muted}
                onClick={() => setMute("input", !input.muted)}
              />
              <Slider
                value={input.volume}
                onChange={(v) => setVolume("input", v)}
                label="Громкость микрофона"
                wheelStep={settings.step}
              />
              <span className="ws-flyout__value">{input.volume}%</span>
            </div>
            <PeakBar peak={input.muted ? 0 : (mixer?.inputPeak ?? 0)} />
          </div>
        </>
      )}

      <button
        type="button"
        className="ws-flyout__link"
        onClick={() =>
          backend
            .call("launch", { kind: "url", target: "ms-settings:sound" })
            .catch((e) => console.error(e))
        }
      >
        <Icon name="external" size={14} />
        Параметры звука Windows
      </button>
    </>
  );
}
