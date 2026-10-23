import type { ReactNode } from 'react';

/**
 * A section that starts closed: a header saying what's inside (an MCP server's tools, Home
 * Assistant's devices), opened when wanted. Long lists stay out of the way of the settings around them.
 */
export function Accordion({ title, detail, children }: { title: ReactNode; detail?: ReactNode; children: ReactNode }) {
  return (
    <details className="accordion">
      <summary>
        <span className="accordion-title">{title}</span>
        {detail && <span className="muted small">{detail}</span>}
      </summary>
      <div className="accordion-body">{children}</div>
    </details>
  );
}
