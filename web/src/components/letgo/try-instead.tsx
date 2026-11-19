import { Smartphone } from 'lucide-react';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useJournal } from '@/components/journal/journal-context';
import { Button } from '@/components/ui/button';
import { env } from '@/lib/env';
import { capacitorWidget } from '@/lib/native/widget';
import { helps } from '@/lib/services';
import { useStore } from '@/lib/store';

/** The built-in suggestions, shown when the private journal is off or locked or "What helps me" is empty. */
export const tryInsteadIdeas = ['reachOut', 'stepOutside', 'meetFriend', 'read', 'phoneAway'] as const;

/** True once the Android app says it can open the phone's own screen-time overview; false on the web and on older builds. */
function useScreenTime(): boolean {
  const [available, setAvailable] = useState(false);
  useEffect(() => {
    if (!env.native) return;
    let live = true;
    capacitorWidget()
      .then(({ EmberWidget }) => EmberWidget.canOpenScreenTime?.())
      .then((result) => {
        if (live) setAvailable(result?.available === true);
      })
      .catch(() => undefined);
    return () => {
      live = false;
    };
  }, []);
  return available;
}

function openScreenTime(): void {
  capacitorWidget()
    .then(({ EmberWidget }) => EmberWidget.openScreenTime?.())
    .catch(() => undefined);
}

/**
 * A few gentle things to do instead: the user's own "What helps me" items while the private journal is unlocked,
 * otherwise a fixed built-in list. In the Android app it can also open the phone's screen-time overview.
 */
export function TryInstead({ heading = true }: { heading?: boolean }) {
  const { t } = useTranslation();
  const headingId = useId();
  const journal = useJournal();
  const state = useStore(journal.state);
  const turningOff = useStore(journal.turningOff);
  const items = useStore(helps.items);
  const screenTime = useScreenTime();
  const own = state === 'unlocked' && !turningOff && items?.length ? items.map((item) => item.text) : null;
  const ideas = own ?? tryInsteadIdeas.map((id) => t(`letgo.tryInstead.${id}`));

  return (
    <div className="flex flex-col gap-2">
      {heading && (
        <p id={headingId} className="font-medium">
          {t('letgo.tryInstead.heading')}
        </p>
      )}
      <ul
        role="list"
        aria-labelledby={heading ? headingId : undefined}
        aria-label={heading ? undefined : t('letgo.tryInstead.heading')}
        className="flex list-disc flex-col gap-1 pl-5 text-muted-foreground [overflow-wrap:anywhere]"
      >
        {ideas.map((idea, index) => (
          <li key={index}>{idea}</li>
        ))}
      </ul>
      {screenTime && (
        <Button variant="secondary" className="self-start" onClick={openScreenTime}>
          <Smartphone aria-hidden />
          {t('letgo.tryInstead.screenTime')}
        </Button>
      )}
    </div>
  );
}
