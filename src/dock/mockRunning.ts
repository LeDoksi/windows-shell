import type { RunningApp } from "./model";

/** Запущенные приложения для браузерного режима. */
export const mockRunning: RunningApp[] = [
  { id: "explorer", title: "Проводник", windows: 2 },
  { id: "chrome", title: "Chrome", windows: 3, active: true },
  { id: "telegram", title: "Telegram", windows: 1, attention: true },
  { id: "figma", title: "Figma", windows: 1 },
];
