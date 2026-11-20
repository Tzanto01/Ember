import { useTranslation } from 'react-i18next';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { useMePatchField } from '@/hooks/use-me-patch';
import type { Me } from '@/lib/models';

/** Whether checking in on a habit offers a short note. */
export function HabitNotesCard({ title, me }: { title: string; me: Me }) {
  const { t } = useTranslation();
  const { fail, outcome, savedStatus, patch } = useSettingsStatus();
  const [offerNote, changeOfferNote] = useMePatchField(
    (value: boolean) => patch('habitNotes', { offerHabitNote: value }),
    { onError: (error) => fail('habitNotes', error) },
  );

  return (
    <SettingsCard id="habit-notes" title={title}>
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor="offer-habit-note" className="min-w-0 font-normal">
          {t('settings.habitNotes.label')}
        </Label>
        <Switch
          id="offer-habit-note"
          aria-describedby="offer-habit-note-hint"
          checked={offerNote ?? me.offerHabitNote}
          onCheckedChange={changeOfferNote}
        />
      </div>
      <p id="offer-habit-note-hint" className="text-[0.875rem] text-muted-foreground">
        {t('settings.habitNotes.hint')}
      </p>
      {savedStatus('habitNotes')}
      {outcome('habitNotes')}
    </SettingsCard>
  );
}
