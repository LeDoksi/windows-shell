/** Время и короткая дата для часов хвоста: «12:23» и «2 окт». */
export function clockLines(date: Date, locale = "ru-RU") {
  const time = date.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" });
  const day = date.toLocaleDateString(locale, { day: "numeric", month: "short" }).replace(".", "");
  return { time, day };
}

/** Недели месяца, начиная с понедельника; null — пустые клетки. */
export function monthGrid(year: number, month: number): (number | null)[][] {
  const first = new Date(year, month, 1);
  const days = new Date(year, month + 1, 0).getDate();
  const offset = (first.getDay() + 6) % 7;
  const cells: (number | null)[] = [
    ...Array<null>(offset).fill(null),
    ...Array.from({ length: days }, (_, i) => i + 1),
  ];
  while (cells.length % 7) cells.push(null);
  return Array.from({ length: cells.length / 7 }, (_, w) => cells.slice(w * 7, w * 7 + 7));
}

/** «3 ч 34 мин» */
export function duration(minutes: number) {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h ? `${h} ч ${m} мин` : `${m} мин`;
}
