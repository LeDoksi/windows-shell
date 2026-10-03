/** Flyout системного хвоста дока; `audio` — flyout аудио-виджета. */
export type FlyoutId = "tray" | "network" | "audio" | "battery" | "clock";

export const flyoutLabels: Record<FlyoutId, string> = {
  tray: "Фоновые приложения",
  network: "Сеть",
  audio: "Звук",
  battery: "Батарея",
  clock: "Календарь",
};
