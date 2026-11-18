import { X } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { Notice } from '@/components/ui/notice';
import { currentLanguage } from '@/lib/i18n';
import { activeNotice, dismissedNotices, dismissNotice, liveNotices, noticeText } from '@/lib/notices';
import { useStore } from '@/lib/store';

/** The first active live notice the device hasn't dismissed, as plain text with a dismiss button; else nothing. */
export function LiveNoticeBanner() {
  const { t } = useTranslation();
  const notice = activeNotice(useStore(liveNotices), { dismissed: useStore(dismissedNotices) });
  if (!notice) return null;
  return (
    <Notice tone="info" aria-label={t('liveNotice.label')}>
      <div className="flex w-full items-start gap-2">
        <p className="whitespace-pre-line">{noticeText(notice, currentLanguage())}</p>
        <Button
          variant="ghost"
          size="icon"
          aria-label={t('liveNotice.dismiss')}
          className="-my-2 -mr-2 ml-auto"
          onClick={() => dismissNotice(notice.id)}
        >
          <X aria-hidden />
        </Button>
      </div>
    </Notice>
  );
}

/** The active maintenance notice for the "can't be reached" state, shown even when dismissed; else nothing. */
export function MaintenanceNotice() {
  const notice = activeNotice(useStore(liveNotices), { kind: 'maintenance' });
  return notice ? (
    <Notice tone="info">
      <p className="whitespace-pre-line">{noticeText(notice, currentLanguage())}</p>
    </Notice>
  ) : null;
}
