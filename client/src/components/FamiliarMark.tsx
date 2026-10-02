/** Placeholder face until the VRM avatar arrives in phase 1. */
export function FamiliarMark({ size = 40 }: { size?: number }) {
  return (
    <svg className="familiar-mark" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <circle cx="16" cy="16" r="14" fill="var(--accent)" />
      <circle cx="11.5" cy="14" r="2.2" fill="var(--on-accent)" />
      <circle cx="20.5" cy="14" r="2.2" fill="var(--on-accent)" />
      <path d="M11 20.5c2.8 2.4 7.2 2.4 10 0" stroke="var(--on-accent)" strokeWidth="2" fill="none" strokeLinecap="round" />
    </svg>
  );
}
