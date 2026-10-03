import { Shell } from './Shell';
import { UserAdmin } from './UserAdmin';

/** Administration, for admins only (App guards the route; the server's Admin policy guards the API). */
export function AdminPage() {
  return (
    <Shell>
      <div className="settings">
        <section className="card">
          <h2>Users</h2>
          <UserAdmin />
        </section>
      </div>
    </Shell>
  );
}
