/** Следующий доступный индекс при навигации стрелками, с заворотом по кругу. */
export function nextEnabledIndex(
  disabled: readonly boolean[],
  current: number,
  direction: 1 | -1,
): number {
  const n = disabled.length;
  for (let step = 1; step <= n; step++) {
    const i = (((current + direction * step) % n) + n) % n;
    if (!disabled[i]) return i;
  }
  return current;
}
