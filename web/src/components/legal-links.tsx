import { useTranslation } from 'react-i18next';
import { Link, useLocation } from 'react-router';
import { isTipFree, tipJarAllowed, tipJarUrl } from '@/lib/tip-jar';

const links = [
  { to: '/legal/privacy', label: 'legalLinks.privacy' },
  { to: '/legal/terms', label: 'legalLinks.terms' },
  { to: '/legal/imprint', label: 'legalLinks.imprint' },
  { to: '/help', label: 'legalLinks.help' },
  { to: '/about', label: 'about.link' },
] as const;

const linkStyle =
  'inline-flex min-h-[44px] min-w-[44px] items-center justify-center text-muted-foreground underline-offset-4 hover:underline';

/**
 * Footer navigation to the three legal pages, the crisis lines page and About Ember, and the tip page where
 * {@link tipJarAllowed} and not {@link isTipFree}.
 */
export function LegalLinks() {
  const { t } = useTranslation();
  const { pathname } = useLocation();
  return (
    <nav
      aria-label={t('legalLinks.label')}
      className="flex flex-wrap justify-center gap-x-5 gap-y-2 text-[0.875rem]"
    >
      {links.map((link) => (
        <Link key={link.to} to={link.to} className={linkStyle}>
          {t(link.label)}
        </Link>
      ))}
      {tipJarAllowed() && !isTipFree(pathname) && (
        <a href={tipJarUrl} target="_blank" rel="noopener noreferrer" className={linkStyle}>
          {t('tipJar.link')} <span className="sr-only">{t('about.newTab')}</span>
        </a>
      )}
    </nav>
  );
}
