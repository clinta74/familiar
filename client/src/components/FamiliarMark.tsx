/** The little fleecy llama, as a static mark for the top bar and the sign-in card. */
export function FamiliarMark({ size = 40 }: { size?: number }) {
  const fleece: [number, number, number][] = [
    [9.5, 12, 4], [8.5, 17, 4], [10, 22, 4], [14, 25, 4], [18, 25, 4], [22, 22, 4], [23.5, 17, 4], [22.5, 12, 4],
    [11, 9, 3.6], [16, 7, 4.2], [21, 9, 3.6], [13.5, 11, 3.2], [18.5, 11, 3.2],
  ];
  return (
    <svg className="familiar-mark" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <path d="M9 10C7.5 6.5 8 2.6 9.6 1c1.7 1.6 2.4 5.4 1.6 9z" fill="var(--plush-wool)" />
      <path d="M23 10c1.5-3.5 1-7.4-.6-9-1.7 1.6-2.4 5.4-1.6 9z" fill="var(--plush-wool)" />
      <path d="M9.6 8.6c-.6-2-.4-4 .3-5.1.7 1.1.9 3.1.5 5.1z" fill="var(--plush-ear)" />
      <path d="M22.4 8.6c.6-2 .4-4-.3-5.1-.7 1.1-.9 3.1-.5 5.1z" fill="var(--plush-ear)" />
      {fleece.map(([cx, cy, r]) => <circle key={`${cx},${cy}`} cx={cx} cy={cy} r={r} fill="var(--plush-wool)" />)}
      <ellipse cx="16" cy="18" rx="5.6" ry="6" fill="var(--plush-skin)" />
      {fleece.slice(8).map(([cx, cy, r]) => <circle key={`f${cx},${cy}`} cx={cx} cy={cy} r={r} fill="var(--plush-wool)" />)}
      <circle cx="13.6" cy="16.4" r="1.4" fill="var(--plush-button)" />
      <circle cx="18.4" cy="16.4" r="1.4" fill="var(--plush-button)" />
      <ellipse cx="16" cy="21" rx="3.2" ry="2.6" fill="var(--plush-snout)" />
      <ellipse cx="16" cy="20.2" rx="1.3" ry="0.8" fill="var(--plush-nose)" />
    </svg>
  );
}
