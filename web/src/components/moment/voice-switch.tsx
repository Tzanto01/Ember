import { useId } from 'react';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { env } from '@/lib/env';
import { momentCopy } from '@/lib/moment/copy';
import { setVoiceOn, useVoiceGuide, voiceOn } from '@/lib/moment/voice';
import { useStore } from '@/lib/store';

/** "Guide me by voice": shown only when the device has an offline voice in the app language. */
export function VoiceSwitch() {
  const id = useId();
  const on = useStore(voiceOn);
  const { available } = useVoiceGuide();
  if (!available) return null;
  return (
    <div className="flex flex-col gap-1">
      <div className="flex min-h-[44px] items-center justify-between gap-4">
        <Label htmlFor={id} className="min-w-0 font-normal">
          {momentCopy.voiceLabel}
        </Label>
        <Switch id={id} aria-describedby={`${id}-hint`} checked={on} onCheckedChange={setVoiceOn} />
      </div>
      <p id={`${id}-hint`} className="text-[0.875rem] text-muted-foreground">
        {env.native ? momentCopy.voiceHintVibrate : momentCopy.voiceHint}
      </p>
    </div>
  );
}
