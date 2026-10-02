const paths = {
  search: "M8.5 3a5.5 5.5 0 1 0 0 11 5.5 5.5 0 0 0 0-11zM13 13l4.5 4.5",
  plus: "M10 4v12M4 10h12",
  settings:
    "M10 7.4a2.6 2.6 0 1 0 0 5.2 2.6 2.6 0 0 0 0-5.2zM10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M4.7 15.3l1.4-1.4M13.9 6.1l1.4-1.4",
  close: "M5 5l10 10M15 5 5 15",
  speaker: "M3 8h3l4-3.5v11L6 12H3zM13 7.5a3.5 3.5 0 0 1 0 5M15.2 5.5a6.5 6.5 0 0 1 0 9",
  speakerMuted: "M3 8h3l4-3.5v11L6 12H3zM13.5 8l4 4M17.5 8l-4 4",
  wifi: "M2.5 8a11 11 0 0 1 15 0M5 11a7 7 0 0 1 10 0M7.6 13.8a3 3 0 0 1 4.8 0",
  chevronUp: "m6 12 4-4 4 4",
  chevronDown: "m6 8 4 4 4-4",
  chevronRight: "m8 6 4 4-4 4",
  folder: "M2.5 5.5h5l1.5 2h8.5v8.5h-15z",
  check: "m4.5 10.5 3.5 3.5 7.5-8",
} as const;

export type IconName = keyof typeof paths;

export function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 20 20"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.6}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name]} />
    </svg>
  );
}
