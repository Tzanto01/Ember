import { ChevronLeft } from 'lucide-react';
import type { MouseEvent } from 'react';
import { Link, useNavigate } from 'react-router';

export interface BackLinkProps {
  /** The parent screen. */
  to: string;
  /** Visible text, the parent's title. */
  label: string;
  /** Accessible name, "Back to …"; it contains `label`. */
  name: string;
  /** Goes back in history instead, for when the user came straight from `to`. */
  viaHistory?: boolean;
}

/** The "‹ Parent" link above a child screen's heading: a way up without the device back button. */
export function BackLink({ to, label, name, viaHistory }: BackLinkProps) {
  const navigate = useNavigate();

  function back(event: MouseEvent<HTMLAnchorElement>): void {
    if (!viaHistory) return;
    event.preventDefault();
    void navigate(-1);
  }

  return (
    <div>
      <Link
        to={to}
        onClick={back}
        aria-label={name}
        className="-ml-2 inline-flex min-h-[44px] items-center gap-1 rounded-md px-2 font-medium text-muted-foreground no-underline hover:bg-accent hover:text-accent-foreground"
      >
        <ChevronLeft aria-hidden className="size-[18px]" />
        {label}
      </Link>
    </div>
  );
}
