import { useQuery } from '@tanstack/react-query';
import { Flame } from 'lucide-react';
import { NavLink, Outlet } from 'react-router';
import { sessionQuery } from '@/api/client';

const links = [
  { to: '/', label: 'Overview' },
  { to: '/reminders', label: 'Reminders' },
  { to: '/feedback', label: 'Feedback' },
  { to: '/system', label: 'System' },
];

export function Layout() {
  const session = useQuery(sessionQuery);
  return (
    <>
      <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:rounded-md focus:bg-card focus:px-3 focus:py-2">
        Skip to content
      </a>
      <header className="border-b border-border bg-card">
        <div className="mx-auto flex max-w-7xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
          <span className="flex items-center gap-2 text-lg font-extrabold">
            <Flame aria-hidden="true" className="size-5 text-primary" />
            Ember ops
          </span>
          <nav aria-label="Main" className="order-3 w-full sm:order-none sm:w-auto">
            <ul className="m-0 flex list-none gap-1 overflow-x-auto p-0">
              {links.map((l) => (
                <li key={l.to}>
                  <NavLink
                    to={l.to}
                    end
                    className={({ isActive }) =>
                      `inline-flex min-h-10 items-center rounded-full px-3 font-bold no-underline ${
                        isActive ? 'bg-primary text-primary-foreground' : 'text-foreground hover:bg-secondary'
                      }`
                    }
                  >
                    {l.label}
                  </NavLink>
                </li>
              ))}
            </ul>
          </nav>
          <p className="m-0 ml-auto text-sm text-muted-foreground">
            {session.data ? (
              <>
                Signed in as <span className="font-bold text-foreground">{session.data.author}</span>
              </>
            ) : session.isError ? (
              'Not signed in'
            ) : null}
          </p>
        </div>
      </header>
      <main id="main" tabIndex={-1} className="mx-auto max-w-7xl px-4 py-6 outline-none">
        <Outlet />
      </main>
    </>
  );
}
