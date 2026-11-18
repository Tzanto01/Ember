import { useQuery } from '@tanstack/react-query';
import { remindersQuery } from '@/api/client';
import type { ChannelDay } from '@/api/types';
import { BarChart } from '@/components/bar-chart';
import { Badge, Loaded, Note, PageHeader, Panel, Stats, Tile } from '@/components/ui';
import { formatDay, formatNumber, formatStamp } from '@/lib/format';

const channels = [
  { label: 'Discord', color: 'var(--chart-1)' },
  { label: 'Push', color: 'var(--chart-2)' },
];
const perChannel = (list: ChannelDay[]) => list.map((d) => ({ label: formatDay(d.date), values: [d.discord, d.push] }));

export function RemindersPage() {
  const query = useQuery(remindersQuery);
  return (
    <Loaded query={query} title="Reminders">
      {(r) => {
        const overdue = r.overdue.habits + r.overdue.routines;
        return (
          <>
            <PageHeader title="Reminders" at={r.generatedAt} fetching={query.isFetching} stale={query.isRefetchError} />
            <dl className="m-0 mb-6 grid grid-cols-2 gap-3 md:grid-cols-4">
              <Tile
                label="Overdue reminders"
                value={formatNumber(overdue)}
                hint={
                  overdue === 0 ? (
                    <Badge tone="ok">Advancing</Badge>
                  ) : (
                    <Badge tone="bad">{`${r.overdue.habits} habits, ${r.overdue.routines} routines`}</Badge>
                  )
                }
              />
              <Tile label="Discord DMs on" value={formatNumber(r.discord.remindersOn)} />
              <Tile label="Second reminders on" value={formatNumber(r.secondReminders.usersOn)} hint={`${formatNumber(r.secondReminders.pendingNow)} pending now`} />
              <Tile label="Push enabled" value={formatNumber(r.push.enabled)} hint={`of ${formatNumber(r.push.subscriptions)} subscriptions`} />
            </dl>
            <div className="grid gap-4 lg:grid-cols-2">
              <Panel title="Still open">
                <BarChart
                  title="Still-open reminders per day"
                  note="Ember deletes reminder rows when they close, so these are only the ones still open"
                  series={channels}
                  bars={perChannel(r.openPerDay)}
                />
              </Panel>
              <Panel title="History">
                {r.history.since === null ? (
                  <Note>No snapshots yet</Note>
                ) : (
                  <BarChart
                    title="Reminders sent per day"
                    note={`history since ${formatStamp(r.history.since)}; includes second reminders and resends`}
                    series={channels}
                    bars={perChannel(r.history.perDay)}
                  />
                )}
              </Panel>
              <Panel title="Discord DMs">
                <Stats
                  items={[
                    ['Users with DM reminders', formatNumber(r.discord.remindersOn)],
                    ['Failing', r.discord.failing > 0 ? <Badge tone="bad">{formatNumber(r.discord.failing)}</Badge> : '0'],
                    ['Paused', formatNumber(r.discord.paused)],
                  ]}
                />
              </Panel>
              <Panel title="Second reminders">
                <Stats
                  items={[
                    ['Users on', formatNumber(r.secondReminders.usersOn)],
                    ['Pending now', formatNumber(r.secondReminders.pendingNow)],
                  ]}
                />
                <div className="mt-4">
                  <BarChart
                    title="Users by delay"
                    series={[{ label: 'Users', color: 'var(--chart-3)' }]}
                    bars={r.secondReminders.byMinutes.map((m) => ({ label: `${m.minutes} min`, values: [m.count] }))}
                  />
                </div>
              </Panel>
              <Panel title="Push and phones">
                <Stats
                  items={[
                    ['Push subscriptions', formatNumber(r.push.subscriptions)],
                    ['Push enabled', formatNumber(r.push.enabled)],
                    ['Phones', formatNumber(r.phones.total)],
                    ['Phones enabled', formatNumber(r.phones.enabled)],
                  ]}
                />
              </Panel>
              <Panel title="Overdue">
                <Stats
                  items={[
                    ['Habits', formatNumber(r.overdue.habits)],
                    ['Routines', formatNumber(r.overdue.routines)],
                  ]}
                />
                <p className="mt-3 mb-0 text-sm text-muted-foreground">Next reminder more than 15 minutes in the past.</p>
              </Panel>
            </div>
          </>
        );
      }}
    </Loaded>
  );
}
