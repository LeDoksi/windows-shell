import { useEffect, useState, type FormEvent } from "react";
import { backend } from "../shared/backend";
import { Icon, SearchField, Tile } from "../shared/ui";
import { itemFromApp, itemFromUrl, type InstalledApp, type NewItem } from "./add";
import { ItemIcon } from "./ItemIcon";

type PanelProps = {
  color: string;
  onAdd: (items: NewItem[]) => void;
  onCancel: () => void;
};

/** Список «Пуска» читаем один раз за жизнь окна: Проводник отвечает не мгновенно. */
let appsRequest: Promise<InstalledApp[]> | null = null;

function useInstalledApps() {
  const [apps, setApps] = useState<InstalledApp[] | null>(null);
  useEffect(() => {
    let alive = true;
    appsRequest ??= backend.call("installed_apps").catch(() => []);
    void appsRequest.then((list) => alive && setApps(list));
    return () => {
      alive = false;
    };
  }, []);
  return apps;
}

/** Выбор приложений из «Пуска»: отметить несколько и добавить разом. */
export function AppPicker({ color, onAdd, onCancel }: PanelProps) {
  const apps = useInstalledApps();
  const [query, setQuery] = useState("");
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set());
  const q = query.trim().toLowerCase();
  const shown = (apps ?? []).filter((app) => !q || app.name.toLowerCase().includes(q));
  const toggle = (id: string) =>
    setChosen((set) => {
      const next = new Set(set);
      if (!next.delete(id)) next.add(id);
      return next;
    });

  return (
    <div className="ws-add-panel" onKeyDown={(e) => e.key === "Escape" && !query && onCancel()}>
      <div className="ws-add-panel__head">
        <h3>Приложения</h3>
        <SearchField
          value={query}
          onChange={setQuery}
          placeholder="Найти приложение"
          aria-label="Найти приложение"
          className="ws-category__search"
          autoFocus
        />
      </div>
      <div className="ws-category__grid">
        {shown.map((app) => {
          const item = { id: app.id, ...itemFromApp(app) };
          return (
            <Tile
              key={app.id}
              icon={<ItemIcon item={item} color={color} />}
              title={app.name}
              meta={chosen.has(app.id) ? "Выбрано" : ""}
              selected={chosen.has(app.id)}
              aria-pressed={chosen.has(app.id)}
              onClick={() => toggle(app.id)}
              onDoubleClick={() => onAdd([itemFromApp(app)])}
            />
          );
        })}
        {apps === null && <p className="ws-category__empty">Загружаю список…</p>}
        {apps !== null && shown.length === 0 && (
          <p className="ws-category__empty">Ничего не нашлось</p>
        )}
      </div>
      <div className="ws-add-panel__actions">
        <button type="button" className="ws-button" onClick={onCancel}>
          Отмена
        </button>
        <button
          type="button"
          className="ws-button ws-button--accent"
          disabled={chosen.size === 0}
          onClick={() => onAdd((apps ?? []).filter((a) => chosen.has(a.id)).map(itemFromApp))}
        >
          {chosen.size > 1 ? `Добавить (${chosen.size})` : "Добавить"}
        </button>
      </div>
    </div>
  );
}

/** Ссылка на сайт: адрес и, по желанию, название. */
export function LinkForm({ onAdd, onCancel }: Omit<PanelProps, "color">) {
  const [address, setAddress] = useState("");
  const [title, setTitle] = useState("");
  const [error, setError] = useState(false);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    const item = itemFromUrl(address, title);
    if (item) onAdd([item]);
    else setError(true);
  };

  return (
    <form
      className="ws-add-panel ws-add-panel--form"
      onSubmit={submit}
      onKeyDown={(e) => e.key === "Escape" && onCancel()}
    >
      <div className="ws-add-panel__head">
        <h3>Ссылка</h3>
      </div>
      <label className="ws-field">
        <span>Адрес</span>
        <span className="ws-search">
          <Icon name="globe" size={14} />
          <input
            value={address}
            onChange={(e) => {
              setAddress(e.target.value);
              setError(false);
            }}
            placeholder="youtube.com"
            autoFocus
            aria-invalid={error}
          />
        </span>
        {error && <small className="ws-field__error">Это не похоже на адрес сайта</small>}
      </label>
      <label className="ws-field">
        <span>Название</span>
        <span className="ws-search">
          <input
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="Как у сайта"
          />
        </span>
      </label>
      <div className="ws-add-panel__actions">
        <button type="button" className="ws-button" onClick={onCancel}>
          Отмена
        </button>
        <button type="submit" className="ws-button ws-button--accent" disabled={!address.trim()}>
          Добавить
        </button>
      </div>
    </form>
  );
}
