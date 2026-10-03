import { useState } from "react";
import { Icon, IconButton, Toggle } from "../shared/ui";
import { useWidgets } from "./useWidgets";

/**
 * Настройки виджетов (WSH-34): какие показывать, в каком порядке и свои настройки каждого.
 * Открывается шестерёнкой верхней панели; полное окно настроек — этап 7.
 */
export function WidgetSettings() {
  const { widgets, setEnabled, move, updateSettings } = useWidgets();
  const [expanded, setExpanded] = useState<string | null>(null);
  return (
    <>
      <div className="ws-flyout__title">Виджеты</div>
      <ul className="ws-widget-settings" aria-label="Виджеты">
        {widgets.map(({ def, enabled, settings }) => {
          const slotList = widgets.filter((w) => w.def.slot === def.slot);
          const index = slotList.findIndex((w) => w.def.id === def.id);
          const open = expanded === def.id;
          return (
            <li key={def.id} className="ws-widget-settings__item">
              <div className="ws-flyout__row">
                <span className="ws-widget-settings__icon">
                  <Icon name={def.icon} size={18} />
                </span>
                <div className="ws-widget-settings__text">
                  <div className="ws-flyout__title">{def.name}</div>
                  <div className="ws-flyout__hint">{def.description}</div>
                </div>
                <Toggle
                  checked={enabled}
                  onChange={(on) => void setEnabled(def.id, on)}
                  label={`Показывать «${def.name}»`}
                />
              </div>
              <div className="ws-widget-settings__actions">
                {def.slot === "launcher" && slotList.length > 1 && (
                  <>
                    <IconButton
                      icon="arrowLeft"
                      label={`Левее: ${def.name}`}
                      disabled={index === 0}
                      onClick={() => void move(def.id, -1)}
                    />
                    <IconButton
                      icon="arrowRight"
                      label={`Правее: ${def.name}`}
                      disabled={index === slotList.length - 1}
                      onClick={() => void move(def.id, 1)}
                    />
                  </>
                )}
                {def.Settings && (
                  <button
                    type="button"
                    className="ws-flyout__link"
                    aria-expanded={open}
                    onClick={() => setExpanded(open ? null : def.id)}
                  >
                    <Icon name={open ? "chevronUp" : "chevronDown"} size={14} />
                    Настроить
                  </button>
                )}
              </div>
              {open && def.Settings && (
                <def.Settings
                  settings={settings}
                  updateSettings={(change) => void updateSettings(def.id, change)}
                />
              )}
            </li>
          );
        })}
      </ul>
    </>
  );
}
