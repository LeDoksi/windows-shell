import type { WindowInfo } from "../shared/backend";

const w = (hwnd: number, title: string, app: Partial<WindowInfo>): WindowInfo => ({
  hwnd,
  title,
  aumid: null,
  exe: null,
  appName: "",
  minimized: false,
  active: false,
  attention: false,
  seq: hwnd,
  focused: 0,
  ...app,
});

const explorer = { aumid: "Microsoft.Windows.Explorer", appName: "Проводник" };
const chrome = {
  aumid: "Chrome",
  exe: "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
  appName: "Google Chrome",
};

/** Окна программ для браузерного режима и спайка: как будто их прислал трекер в Rust. */
export const mockWindows: WindowInfo[] = [
  w(1, "Документы", { ...explorer, focused: 2 }),
  w(2, "Загрузки", { ...explorer, minimized: true }),
  w(3, "Почта — Google Chrome", { ...chrome, active: true, focused: 5 }),
  w(4, "Windows Shell — GitHub — Google Chrome", { ...chrome, focused: 3 }),
  w(5, "YouTube — Google Chrome", { ...chrome, minimized: true }),
  w(6, "Telegram", { aumid: "TelegramDesktop", appName: "Telegram", attention: true }),
  w(7, "Макет дока — Figma", {
    exe: "C:\\Users\\Demo\\AppData\\Local\\Figma\\Figma.exe",
    appName: "Figma",
    focused: 4,
  }),
];
