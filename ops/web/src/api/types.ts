/** `YYYY-MM-DD` calendar date. */
export type DateOnly = string;
/** ISO 8601 UTC timestamp. */
export type Timestamp = string;

export interface DayCount {
  date: DateOnly;
  count: number;
}

export interface Session {
  author: string;
  csrfToken: string;
}

export interface Overview {
  generatedAt: Timestamp;
  users: {
    total: number;
    active7: number;
    active30: number;
    /** Monday of each of the last 12 weeks; week of each user's first habit (proxy). */
    newPerWeek: { week: DateOnly; count: number }[];
  };
  checkInsPerDay: DayCount[];
  habits: { build: number; letGo: number | null; paused: number; withReminder: number };
  routines: { total: number; users: number };
  journalPerDay: DayCount[];
  lowDaysPerDay: DayCount[];
  streaks: { users: number; showStreaks: number; grace: { days: number; count: number }[] };
  fireside: null | { usersOn: number; friendships: number; warmthPerDay: DayCount[] };
  letGoSlipsPerWeek: null | { week: DateOnly; slips: number; days: number }[];
}

export interface ChannelDay {
  date: DateOnly;
  discord: number;
  push: number;
}

export interface Reminders {
  generatedAt: Timestamp;
  openPerDay: ChannelDay[];
  history: { since: Timestamp | null; perDay: ChannelDay[] };
  discord: { remindersOn: number; failing: number; paused: number };
  secondReminders: { usersOn: number; byMinutes: { minutes: number; count: number }[]; pendingNow: number };
  push: { subscriptions: number; enabled: number };
  phones: { total: number; enabled: number };
  overdue: { habits: number; routines: number };
}

export const statuses = ['open', 'planned', 'implemented', 'released', 'declined'] as const;
export type FeedbackStatus = (typeof statuses)[number];

export interface FeedbackItem {
  id: number;
  title: string;
  text: string;
  platform: string;
  version: string | null;
  createdAt: Timestamp;
  status: FeedbackStatus;
  resolvedAt: Timestamp | null;
  anonymous: boolean;
  imageIds: string[];
  commentCount: number;
  position: number;
}

export interface FeedbackList {
  items: FeedbackItem[];
}

export interface Comment {
  id: number;
  author: string;
  body: string;
  createdAt: Timestamp;
}

export interface StatusMove {
  status: FeedbackStatus;
  position: number | null;
}

/** One container from the host status file, or the "Reminders advancing" check. */
export interface Service {
  name: string;
  ok: boolean;
  detail: string | null;
}

export interface CiRun {
  branch: string;
  status: string;
  conclusion: string | null;
  url: string;
  at: Timestamp;
  sha: string;
}

export interface System {
  generatedAt: Timestamp;
  /** When the host status file was written; null when it is missing or unreadable. */
  statusWrittenAt: Timestamp | null;
  services: Service[];
  database: { sizeBytes: number; latestMigration: string | null } | null;
  disk: { freeBytes: number; totalBytes: number } | null;
  /** Newest pre-deploy dump; its age is information, not a failure. */
  backup: { name: string; at: Timestamp; ageHours: number; sizeBytes: number } | null;
  deployed: { production: string | null; staging: string | null } | null;
  ci: CiRun[] | null;
  notice: unknown;
  apk: { versionName: string; versionCode: number } | null;
  dashboardSha: string | null;
  errors: Record<string, string>;
}

export type NoticeKind = 'info' | 'maintenance';

/** One in-app notice; texts are plain text, window bounds are UTC with seconds precision. */
export interface Notice {
  id: string;
  kind: NoticeKind;
  en: string;
  de: string;
  showFrom?: Timestamp;
  showUntil?: Timestamp;
}

export type NoticeState = 'scheduled' | 'live' | 'ended';

export interface NoticesView {
  /** False when the publish token or URLs are not configured; the list is then read-only. */
  publishing: boolean;
  notices: (Notice & { state: NoticeState })[];
}
