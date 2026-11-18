import { useQuery } from '@tanstack/react-query';
import { overviewQuery } from '@/api/client';
import type { DayCount } from '@/api/types';
import { BarChart } from '@/components/bar-chart';
import { Loaded, Note, PageHeader, Panel, Stats, Tile } from '@/components/ui';
import { formatDay, formatNumber, percent } from '@/lib/format';

const one = [{ label: 'Count', color: 'var(--chart-1)' }];
const days = (list: DayCount[]) => list.map((d) => ({ label: formatDay(d.date), values: [d.count] }));
const notYet = 'Not on production yet';

export function OverviewPage() {
  const query = useQuery(overviewQuery);
  return (
    <Loaded query={query} title="Overview">
      {(o) => (
        <>
          <PageHeader title="Overview" at={o.generatedAt} fetching={query.isFetching} stale={query.isRefetchError} />
          <dl className="m-0 mb-6 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
            <Tile label="Users" value={formatNumber(o.users.total)} />
            <Tile label="Active 7 days" value={formatNumber(o.users.active7)} hint={percent(o.users.active7, o.users.total)} />
            <Tile label="Active 30 days" value={formatNumber(o.users.active30)} hint={percent(o.users.active30, o.users.total)} />
            <Tile
              label="Habits build / let go"
              value={`${formatNumber(o.habits.build)} / ${o.habits.letGo === null ? '–' : formatNumber(o.habits.letGo)}`}
              hint={o.habits.letGo === null ? `Let go: ${notYet.toLowerCase()}` : `${formatNumber(o.habits.paused)} paused`}
            />
            <Tile label="Routines" value={formatNumber(o.routines.total)} hint={`${formatNumber(o.routines.users)} users`} />
            <Tile
              label="Streak users"
              value={formatNumber(o.streaks.users)}
              hint={`${percent(o.streaks.showStreaks, o.streaks.users)} show streaks`}
            />
          </dl>
          <div className="grid gap-4 lg:grid-cols-2">
            <Panel title="New users per week">
              <BarChart
                showTitle={false}
                title="New users per week"
                note="by first habit, proxy"
                series={one}
                bars={o.users.newPerWeek.map((w) => ({ label: formatDay(w.week), values: [w.count] }))}
              />
            </Panel>
            <Panel title="Check-ins per day">
              <BarChart showTitle={false} title="Check-ins per day" note="last 90 days, completed" series={one} bars={days(o.checkInsPerDay)} />
            </Panel>
            <Panel title="Journal days per day">
              <BarChart showTitle={false} title="Journal days per day" note="journal days (note or mood)" series={one} bars={days(o.journalPerDay)} />
            </Panel>
            <Panel title="Low days per day">
              <BarChart
                showTitle={false}
                title="Low days per day"
                series={[{ label: 'Low days', color: 'var(--chart-2)' }]}
                bars={days(o.lowDaysPerDay)}
              />
            </Panel>
            <Panel title="Users by grace days per week">
              <BarChart
                showTitle={false}
                title="Users by grace days per week"
                series={[{ label: 'Users', color: 'var(--chart-3)' }]}
                bars={o.streaks.grace.map((g) => ({ label: `${g.days} d`, values: [g.count] }))}
              />
              <div className="mt-3">
                <Stats
                  items={[
                    ['Habits paused', formatNumber(o.habits.paused)],
                    ['Habits with a reminder', formatNumber(o.habits.withReminder)],
                  ]}
                />
              </div>
            </Panel>
            <Panel title="Fireside">
              {o.fireside === null ? (
                <Note>{notYet}</Note>
              ) : (
                <div className="flex flex-col gap-4">
                  <Stats
                    items={[
                      ['Users with Fireside on', formatNumber(o.fireside.usersOn)],
                      ['Friendships', formatNumber(o.fireside.friendships)],
                    ]}
                  />
                  <BarChart
                    title="Warmth per day"
                    note="kept rows (30-day retention)"
                    series={one}
                    bars={days(o.fireside.warmthPerDay)}
                  />
                </div>
              )}
            </Panel>
            <Panel title="Let go">
              {o.letGoSlipsPerWeek === null ? (
                <Note>{notYet}</Note>
              ) : (
                <div className="flex flex-col gap-4">
                  <BarChart
                    title="Slips per week"
                    note="sum of slip counts"
                    series={[{ label: 'Slips', color: 'var(--chart-2)' }]}
                    bars={o.letGoSlipsPerWeek.map((w) => ({ label: formatDay(w.week), values: [w.slips] }))}
                  />
                  <Stats
                    items={[
                      ['Slips, 12 weeks', formatNumber(o.letGoSlipsPerWeek.reduce((s, w) => s + w.slips, 0))],
                      ['Days with a slip, 12 weeks', formatNumber(o.letGoSlipsPerWeek.reduce((s, w) => s + w.days, 0))],
                    ]}
                  />
                </div>
              )}
            </Panel>
          </div>
        </>
      )}
    </Loaded>
  );
}
