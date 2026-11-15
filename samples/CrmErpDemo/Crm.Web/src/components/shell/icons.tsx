import type { ReactNode } from 'react';

// Small stroke icons for the shell. 16px, currentColor, decorative (aria-hidden).

type IconProps = { className?: string };

function Svg({ className, children }: IconProps & { children: ReactNode }) {
  return (
    <svg
      aria-hidden="true"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.3}
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className ?? 'h-4 w-4'}
    >
      {children}
    </svg>
  );
}

export const PlusIcon = (p: IconProps) => <Svg {...p}><path d="M8 3v10M3 8h10" /></Svg>;
export const RefreshIcon = (p: IconProps) => (
  <Svg {...p}><path d="M13 8a5 5 0 1 1-1.5-3.6M13 2.5V5h-2.5" /></Svg>
);
export const TrashIcon = (p: IconProps) => (
  <Svg {...p}><path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.6 8.5h5.8l.6-8.5M7 7v4M9 7v4" /></Svg>
);
export const SaveIcon = (p: IconProps) => (
  <Svg {...p}><path d="M3 3h8l2 2v8H3zM5.5 3v3h5V3M5.5 13V9.5h5V13" /></Svg>
);
export const BackIcon = (p: IconProps) => <Svg {...p}><path d="M13 8H3M7 4 3 8l4 4" /></Svg>;
export const GearIcon = (p: IconProps) => (
  <Svg {...p}>
    <circle cx="8" cy="8" r="2" />
    <path d="M8 1.8v1.6M8 12.6v1.6M1.8 8h1.6M12.6 8h1.6M3.6 3.6l1.1 1.1M11.3 11.3l1.1 1.1M3.6 12.4l1.1-1.1M11.3 4.7l1.1-1.1" />
  </Svg>
);
export const CloseIcon = (p: IconProps) => <Svg {...p}><path d="M4 4l8 8M12 4l-8 8" /></Svg>;
export const SparkleIcon = (p: IconProps) => (
  <Svg {...p}><path d="M8 2.5 9.2 6.8 13.5 8 9.2 9.2 8 13.5 6.8 9.2 2.5 8l4.3-1.2z" /></Svg>
);
export const BuildingIcon = (p: IconProps) => (
  <Svg {...p}><path d="M3 13.5V3h6.5v10.5M9.5 6.5H13v7M2 13.5h12M5 5.5h2M5 8h2M5 10.5h2" /></Svg>
);
export const PersonIcon = (p: IconProps) => (
  <Svg {...p}><circle cx="8" cy="5.5" r="2.5" /><path d="M3 13.5c.6-2.6 2.6-4 5-4s4.4 1.4 5 4" /></Svg>
);
export const HandshakeIcon = (p: IconProps) => (
  <Svg {...p}><path d="M1.5 7 4 4.5l3 1 2-1 2.5.5L14.5 7M4 4.5 2 9l3.5 3 2-1.5M11.5 5l2 4-3.5 3-3-2.5 2-1.5" /></Svg>
);
export const HomeIcon = (p: IconProps) => (
  <Svg {...p}><path d="M2.5 7.5 8 3l5.5 4.5M4 6.5V13h3V9.5h2V13h3V6.5" /></Svg>
);
export const BellIcon = (p: IconProps) => (
  <Svg {...p}><path d="M4 11V7a4 4 0 0 1 8 0v4l1 1.5H3zM6.5 13.5a1.5 1.5 0 0 0 3 0" /></Svg>
);
export const LinkIcon = (p: IconProps) => (
  <Svg {...p}><path d="M7 9a2.5 2.5 0 0 0 3.5 0L13 6.5A2.5 2.5 0 0 0 9.5 3L8.5 4M9 7a2.5 2.5 0 0 0-3.5 0L3 9.5A2.5 2.5 0 0 0 6.5 13l1-1" /></Svg>
);
export const ChevronDownIcon = (p: IconProps) => <Svg {...p}><path d="M4 6l4 4 4-4" /></Svg>;
export const ChevronRightIcon = (p: IconProps) => <Svg {...p}><path d="M6 4l4 4-4 4" /></Svg>;

/** The nine-dot app launcher mark in the app bar. */
export function Waffle() {
  return (
    <span aria-hidden="true" className="grid grid-cols-3 gap-[3px]">
      {Array.from({ length: 9 }, (_, i) => <span key={i} className="h-1 w-1 bg-white/90" />)}
    </span>
  );
}
