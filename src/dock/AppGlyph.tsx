import { useSystemIcon } from "../shared/useSystemIcon";

const palette = [
  ["#4C9BF5", "#2F6FD6"],
  ["#B07CF0", "#7A4FD0"],
  ["#56D27A", "#2E9E57"],
  ["#F0A64A", "#D07A22"],
  ["#F06B8E", "#C9466A"],
  ["#4CC3D9", "#2A8FA8"],
];

function hash(text: string) {
  let h = 0;
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) | 0;
  return Math.abs(h);
}

/** Иконка приложения из Windows; пока её нет (или браузерный режим) — плашка с буквой. */
export function AppGlyph({
  id,
  title,
  target = "",
}: {
  id: string;
  title: string;
  target?: string;
}) {
  const src = useSystemIcon("app", target);
  if (src)
    return <img className="ws-app-glyph ws-app-glyph--system" src={src} alt="" draggable={false} />;
  const [from, to] = palette[hash(id) % palette.length];
  return (
    <span
      className="ws-app-glyph"
      style={{ background: `linear-gradient(160deg, ${from}, ${to})` }}
      aria-hidden="true"
    >
      {title.charAt(0)}
    </span>
  );
}
