import { useAuth } from '../auth/AuthContext';
import { FamiliarMark } from '../components/FamiliarMark';
import { Shell } from './Shell';

export function HomePage() {
  const { account } = useAuth();

  return (
    <Shell>
      <section className="stage">
        <div className="avatar-placeholder">
          <FamiliarMark size={160} />
        </div>
        <h1>Hi, {account?.username}.</h1>
        <p className="muted">
          Your familiar is still getting ready. Conversation and the animated avatar come in the next phase.
        </p>
      </section>
    </Shell>
  );
}
