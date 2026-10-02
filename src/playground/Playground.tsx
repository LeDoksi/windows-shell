import { useState, type ReactNode } from "react";
import {
  AddTile,
  CategoryPill,
  ContextMenu,
  Flyout,
  GlassSurface,
  Icon,
  IconButton,
  SearchField,
  Select,
  Slider,
  Tile,
  Toggle,
  Tooltip,
  type MenuEntry,
} from "../shared/ui";
import "./playground.css";

const outputs = [
  { value: "sony", label: "Наушники (Sony WH-1000XM5)" },
  { value: "realtek", label: "Динамики (Realtek)" },
  { value: "dell", label: "Монитор DELL (HDMI)" },
] as const;

type Output = (typeof outputs)[number]["value"];

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="pg-section">
      <h2>{title}</h2>
      <div className="pg-row">{children}</div>
    </section>
  );
}

export function Playground() {
  const [query, setQuery] = useState("");
  const [volume, setVolume] = useState(64);
  const [muted, setMuted] = useState(false);
  const [mic, setMic] = useState(true);
  const [output, setOutput] = useState<Output>("sony");
  const [category, setCategory] = useState("work");
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const [flyout, setFlyout] = useState(false);
  const [glass, setGlass] = useState(true);

  const menuItems: MenuEntry[] = [
    { label: "Открыть", shortcut: "Enter", onSelect: () => {} },
    { label: "Открыть расположение", onSelect: () => {} },
    { label: "Запустить от имени администратора", disabled: true, onSelect: () => {} },
    { kind: "separator" },
    { label: "Удалить из панели", danger: true, onSelect: () => {} },
  ];

  const categories = [
    { id: "work", label: "Работа", color: "#3D8EF0" },
    { id: "games", label: "Игры", color: "#9B6BF0" },
    { id: "docs", label: "Документы", color: "#F0B43D" },
    { id: "media", label: "Медиа", color: "#F06B8E" },
  ];

  return (
    <main className="pg" data-glass={glass ? undefined : "off"}>
      <header className="pg-header">
        <h1>Shell Glass · витрина компонентов</h1>
        <label className="pg-line">
          <span className="pg-muted">Размытие</span>
          <Toggle checked={glass} onChange={setGlass} label="Размытие" />
        </label>
      </header>

      <Section title="GlassSurface">
        <GlassSurface level="bar" className="pg-swatch">
          bar
        </GlassSurface>
        <GlassSurface level="panel" className="pg-swatch">
          panel
        </GlassSurface>
        <GlassSurface level="menu" className="pg-swatch">
          menu
        </GlassSurface>
      </Section>

      <Section title="IconButton · Tooltip">
        <GlassSurface level="bar" className="pg-bar">
          <Tooltip text="Поиск" placement="bottom">
            <IconButton icon="search" label="Поиск" />
          </Tooltip>
          <Tooltip text="Добавить" placement="bottom">
            <IconButton icon="plus" label="Добавить" />
          </Tooltip>
          <Tooltip text="Настройки" placement="bottom">
            <IconButton icon="settings" label="Настройки" />
          </Tooltip>
          <IconButton icon="close" label="Недоступно" disabled />
        </GlassSurface>
      </Section>

      <Section title="CategoryPill">
        <GlassSurface level="bar" className="pg-bar">
          {categories.map((c) => (
            <CategoryPill
              key={c.id}
              label={c.label}
              color={c.color}
              selected={category === c.id}
              onClick={() => setCategory(c.id)}
            />
          ))}
          <CategoryPill label="Перетащите сюда" color="#3DD68C" dragOver />
        </GlassSurface>
      </Section>

      <Section title="Tile · AddTile">
        <GlassSurface level="panel" className="pg-grid">
          <Tile icon={<Icon name="folder" size={28} />} title="Проекты" meta="Папка" />
          <Tile
            icon={<Icon name="folder" size={28} />}
            title="Отчёт.docx"
            meta="Документ · 2.4 МБ"
            selected
          />
          <Tile
            icon={<Icon name="folder" size={28} />}
            title="Старый ярлык"
            meta="Объект не найден"
            missing
          />
          <AddTile />
        </GlassSurface>
      </Section>

      <Section title="SearchField">
        <div className="pg-field">
          <SearchField value={query} onChange={setQuery} placeholder="Поиск в категории" />
        </div>
      </Section>

      <Section title="Slider · Toggle · Select">
        <GlassSurface level="panel" className="pg-audio">
          <div className="pg-line">
            <IconButton
              icon={muted ? "speakerMuted" : "speaker"}
              label={muted ? "Включить звук" : "Выключить звук"}
              onClick={() => setMuted(!muted)}
            />
            <Slider value={volume} onChange={setVolume} label="Громкость" disabled={muted} />
            <span className="pg-value">{volume}%</span>
          </div>
          <Select value={output} options={[...outputs]} onChange={setOutput} label="Вывод" />
          <div className="pg-line pg-line--between">
            <span>Микрофон</span>
            <Toggle checked={mic} onChange={setMic} label="Микрофон" />
          </div>
          <div className="pg-line pg-line--between">
            <span>Недоступно</span>
            <Toggle checked={false} onChange={() => {}} label="Недоступно" disabled />
          </div>
        </GlassSurface>
      </Section>

      <Section title="ContextMenu · Flyout">
        <button
          type="button"
          className="pg-target"
          onClick={(e) => setMenu({ x: e.clientX, y: e.clientY })}
          onContextMenu={(e) => {
            e.preventDefault();
            setMenu({ x: e.clientX, y: e.clientY });
          }}
        >
          Клик или ПКМ — контекстное меню
        </button>
        <div className="pg-flyout-host">
          <button type="button" className="pg-target" onClick={() => setFlyout(!flyout)}>
            Открыть flyout
          </button>
          <Flyout
            open={flyout}
            onClose={() => setFlyout(false)}
            label="Пример flyout"
            anchor="launcher"
            className="pg-flyout"
          >
            <strong>Flyout под панелью</strong>
            <span className="pg-muted">Закрывается по Esc и клику мимо.</span>
          </Flyout>
        </div>
      </Section>

      <ContextMenu
        open={menu !== null}
        x={menu?.x ?? 0}
        y={menu?.y ?? 0}
        items={menuItems}
        onClose={() => setMenu(null)}
      />
    </main>
  );
}
