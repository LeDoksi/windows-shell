import { Icon, IconButton, Slider, Toggle } from "../shared/ui";
import { duration, monthGrid } from "./format";
import { WifiGlyph } from "./glyphs";
import type { AudioState, BatteryState, NetworkState, TrayIcon, WifiNetwork } from "./model";
import { AppGlyph } from "../dock/AppGlyph";

export function TrayFlyout({ icons }: { icons: TrayIcon[] }) {
  return (
    <>
      <div className="ws-flyout__section">Фоновые приложения</div>
      <div className="ws-tray-grid">
        {icons.map((icon) => (
          <button
            key={icon.id}
            type="button"
            className="ws-tray-icon"
            aria-label={icon.attention ? `${icon.title}, требует внимания` : icon.title}
            title={icon.title}
          >
            <AppGlyph id={icon.id} title={icon.title} />
            {icon.attention && <span className="ws-attention-dot" aria-hidden="true" />}
          </button>
        ))}
      </div>
    </>
  );
}

type NetworkProps = {
  network: NetworkState;
  wifiEnabled: boolean;
  networks: WifiNetwork[];
  onWifiEnabled: (enabled: boolean) => void;
};

export function NetworkFlyout({ network, wifiEnabled, networks, onWifiEnabled }: NetworkProps) {
  const connected = network.kind === "wifi" ? network.ssid : null;
  return (
    <>
      <div className="ws-flyout__row">
        <div>
          <div className="ws-flyout__title">
            {network.kind === "ethernet"
              ? network.name
              : network.kind === "wifi"
                ? network.ssid
                : "Нет подключения"}
          </div>
          <div className="ws-flyout__hint">
            {network.kind === "none" ? "Интернет недоступен" : "Подключено, есть интернет"}
          </div>
        </div>
        <Toggle checked={wifiEnabled} onChange={onWifiEnabled} label="Wi-Fi" />
      </div>
      {wifiEnabled && (
        <>
          <div className="ws-flyout__section">Сети Wi-Fi</div>
          <ul className="ws-flyout__list" aria-label="Сети Wi-Fi">
            {networks.map((n) => (
              <li key={n.ssid}>
                <button
                  type="button"
                  className="ws-flyout__option"
                  aria-current={n.ssid === connected}
                >
                  <WifiGlyph signal={n.signal} size={18} />
                  <span className="ws-flyout__option-text">{n.ssid}</span>
                  <span className="ws-flyout__hint">
                    {n.ssid === connected ? "Подключено" : n.secured ? "Защищена" : "Открытая"}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
      <button type="button" className="ws-flyout__link">
        <Icon name="external" size={14} />
        Параметры сети
      </button>
    </>
  );
}

type AudioProps = {
  audio: AudioState;
  onDevice: (id: string) => void;
  onVolume: (volume: number) => void;
  onMute: (muted: boolean) => void;
};

/** Мок-версия аудио-виджета: полная версия с приложениями и микрофоном — этап 5. */
export function AudioFlyout({ audio, onDevice, onVolume, onMute }: AudioProps) {
  return (
    <>
      <div className="ws-flyout__section">Вывод</div>
      <div role="radiogroup" aria-label="Устройство вывода" className="ws-flyout__list">
        {audio.devices.map((d) => (
          <button
            key={d.id}
            type="button"
            role="radio"
            aria-checked={d.id === audio.defaultId}
            className="ws-flyout__option"
            onClick={() => onDevice(d.id)}
          >
            <span className="ws-radio" aria-hidden="true" />
            <span className="ws-flyout__option-text">{d.name}</span>
          </button>
        ))}
      </div>
      <div className="ws-flyout__row ws-flyout__row--volume">
        <IconButton
          icon={audio.muted ? "speakerMuted" : "speaker"}
          label={audio.muted ? "Включить звук" : "Выключить звук"}
          aria-pressed={audio.muted}
          onClick={() => onMute(!audio.muted)}
        />
        <Slider value={audio.volume} onChange={onVolume} label="Общая громкость" />
        <span className="ws-flyout__value">{audio.volume}%</span>
      </div>
      <button type="button" className="ws-flyout__link">
        <Icon name="external" size={14} />
        Параметры звука Windows
      </button>
    </>
  );
}

export function BatteryFlyout({ battery }: { battery: BatteryState }) {
  return (
    <>
      <div className="ws-flyout__big">{battery.percent}%</div>
      <div className="ws-flyout__hint">
        {battery.charging
          ? battery.minutesLeft
            ? `Заряжается, до полного ${duration(battery.minutesLeft)}`
            : "Заряжается"
          : battery.minutesLeft
            ? `Осталось ${duration(battery.minutesLeft)}`
            : "От батареи"}
      </div>
      <button type="button" className="ws-flyout__link">
        <Icon name="external" size={14} />
        Режим питания
      </button>
    </>
  );
}

const weekdays = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

export function ClockFlyout({ now }: { now: Date }) {
  const grid = monthGrid(now.getFullYear(), now.getMonth());
  const title = now.toLocaleDateString("ru-RU", { weekday: "long", day: "numeric", month: "long" });
  const month = now.toLocaleDateString("ru-RU", { month: "long", year: "numeric" });
  return (
    <>
      <div className="ws-flyout__title ws-capitalize">{title}</div>
      <div className="ws-flyout__section ws-capitalize">{month.replace(" г.", "")}</div>
      <table className="ws-calendar">
        <thead>
          <tr>
            {weekdays.map((d) => (
              <th key={d} scope="col">
                {d}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {grid.map((week, w) => (
            <tr key={w}>
              {week.map((day, i) => (
                <td key={i} aria-current={day === now.getDate() ? "date" : undefined}>
                  {day && <span>{day}</span>}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}
