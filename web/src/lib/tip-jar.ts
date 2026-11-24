import { matchPath } from 'react-router';
import { env } from './env';
import { updaterAvailable } from './native/updater';

/** Nico's tip page. */
export const tipJarUrl = 'https://ko-fi.com/nicowoellnitz';

/**
 * Whether this build may link {@link tipJarUrl}: the web and the sideloaded beta app. Google Play forbids leading users to
 * a payment outside Play billing, so every other native build counts as Play.
 */
export const tipJarAllowed = (): boolean => !env.native || updaterAvailable();

const quietPaths = [
  '/help',
  '/settings/help',
  '/moment',
  '/moments',
  '/delete-account',
  '/consent',
];

/** Whether `pathname` never shows the tip link: crisis lines, the moment flow, the consent question and account deletion. */
export const isTipFree = (pathname: string): boolean =>
  quietPaths.some((path) => matchPath(path, pathname) !== null);
