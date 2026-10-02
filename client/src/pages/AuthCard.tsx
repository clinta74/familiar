import type { ReactNode } from 'react';
import { FamiliarMark } from '../components/FamiliarMark';

export function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="auth-page">
      <section className="card auth-card">
        <FamiliarMark size={56} />
        <h1>{title}</h1>
        {children}
      </section>
    </main>
  );
}
