import { cleanup, render, screen } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { env } from '@/lib/env';
import AboutPage from '@/pages/about';
import { inGerman } from '@/test/test-utils';
import { LegalLinks } from './legal-links';

const plugins = vi.hoisted(() => ({ list: [] as string[] }));

vi.mock('@/lib/env', async (original) => ({
  env: { ...(await original<typeof import('@/lib/env')>()).env },
}));
vi.mock('@capacitor/core', async (original) => ({
  ...(await original<typeof import('@capacitor/core')>()),
  Capacitor: { isPluginAvailable: (name: string) => plugins.list.includes(name) },
}));

function open(path: string) {
  render(
    <RouterProvider
      router={createMemoryRouter(
        [
          {
            path: '*',
            element: (
              <>
                <AboutPage />
                <LegalLinks />
              </>
            ),
          },
        ],
        { initialEntries: [path] },
      )}
    />,
  );
}

const footerTip = () => screen.queryByRole('link', { name: /^Tip a coffee \(/ });
const aboutTip = () => screen.queryByRole('link', { name: /^Tip a coffee on Ko-fi/ });

describe('the tip link', () => {
  afterEach(() => {
    env.native = false;
    plugins.list = [];
  });

  it('is a plain external link without referrer in the footer and on About, in both languages', async () => {
    open('/about');
    for (const link of [footerTip(), aboutTip()]) {
      expect(link).toHaveAttribute('href', 'https://ko-fi.com/nicowoellnitz');
      expect(link).toHaveAttribute('rel', 'noopener noreferrer');
      expect(link).toHaveAttribute('target', '_blank');
    }
    cleanup();

    await inGerman();
    open('/about');
    expect(screen.getAllByRole('link', { name: /Kaffee ausgeben/ })).toHaveLength(2);
    expect(document.body).not.toHaveTextContent(/spende/i);
  });

  it('shows in the sideloaded beta app and stays out of the Play app', () => {
    env.native = true;
    plugins.list = ['EmberUpdater', 'EmberWidget'];
    open('/about');
    expect(footerTip()).toBeInTheDocument();
    expect(aboutTip()).toBeInTheDocument();
    cleanup();

    plugins.list = ['EmberWidget'];
    open('/about');
    expect(footerTip()).toBeNull();
    expect(aboutTip()).toBeNull();
    expect(document.body).not.toHaveTextContent(/ko-fi|coffee/i);
  });

  it.each([
    '/help',
    '/Help/',
    '/settings/help',
    '/moment',
    '/moment/',
    '/moments',
    '/delete-account',
    '/consent',
  ])('stays out of the footer on %s', (path) => {
    open(path);
    expect(footerTip()).toBeNull();
    expect(screen.getByRole('link', { name: 'Help right now' })).toBeInTheDocument();
  });

  it.each(['/today', '/settings/privacy', '/helpful'])('shows in the footer on %s', (path) => {
    open(path);
    expect(footerTip()).toBeInTheDocument();
  });
});
