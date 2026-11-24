import { ExternalLink } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { TapLink } from '@/components/auth/busy-button';
import { MomentDoor } from '@/components/moment/moment-door';
import { PageHeader } from '@/components/page-header';
import { pitch } from '@/content/pitch';
import { tipJarAllowed, tipJarUrl } from '@/lib/tip-jar';

/** Ember's product site. */
const productSite = 'https://ember.nicowoellnitz.de';

const externalLink = 'inline-flex min-h-[44px] items-center gap-2 font-medium text-primary underline underline-offset-4';

const sections = ['moment', 'habits', 'privacy'] as const;

/**
 * `/about`, public: what Ember is, the moment flow, gentle habits, privacy with the way to account deletion, the tip page
 * where {@link tipJarAllowed}, the product site and the beta note.
 */
export default function AboutPage() {
  const { t } = useTranslation();
  return (
    <div className="flex w-full flex-col gap-6">
      <PageHeader title={t('title.about')} lede={pitch.description} />
      {sections.map((id) => (
        <section key={id} aria-labelledby={`about-${id}`} className="flex flex-col gap-2">
          <h2 id={`about-${id}`} className="text-[1.375rem]">
            {t(`about.${id}.heading`)}
          </h2>
          <p>{t(`about.${id}.text`)}</p>
          {id === 'moment' && (
            <p>
              <MomentDoor variant="quiet" />
            </p>
          )}
          {id === 'privacy' && (
            <p>
              <TapLink to="/delete-account">{t('deleteAccount.link')}</TapLink>
            </p>
          )}
        </section>
      ))}
      {tipJarAllowed() && (
        <section aria-labelledby="about-tip" className="flex flex-col gap-2">
          <h2 id="about-tip" className="text-[1.375rem]">
            {t('tipJar.heading')}
          </h2>
          <p>{t('tipJar.text')}</p>
          <p>
            <a href={tipJarUrl} target="_blank" rel="noopener noreferrer" className={externalLink}>
              {t('tipJar.linkLong')} <span className="sr-only">{t('about.newTab')}</span>
              <ExternalLink aria-hidden className="size-[16px] shrink-0" />
            </a>
          </p>
        </section>
      )}
      <p className="text-muted-foreground">{t('about.beta')}</p>
      <div className="flex flex-wrap gap-x-6">
        <a
          href={productSite}
          target="_blank"
          rel="noopener"
          className={externalLink}
        >
          {t('about.site')}{' '}
          <span className="sr-only">{t('about.newTab')}</span>
          <ExternalLink aria-hidden className="size-[16px] shrink-0" />
        </a>
        <TapLink to="/">{t('help.back')}</TapLink>
      </div>
    </div>
  );
}
