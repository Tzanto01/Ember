import { Store } from '../store';
import { calmMode } from '../theme';

const key = 'ember.timerSound';

function stored(): boolean {
  try {
    return localStorage.getItem(key) === '1';
  } catch {
    return false;
  }
}

/** Per-device setting "Sound when a timer ends", off by default, reset when the session ends. */
export const timerSound = new Store<boolean>(stored());

timerSound.subscribe(() => {
  try {
    if (timerSound.get()) localStorage.setItem(key, '1');
    else localStorage.removeItem(key);
  } catch {
    return;
  }
});

/** True when a timer's end may sound: the setting is on and calm mode is off. */
export function timerSounds(): boolean {
  return timerSound.get() && !calmMode.get();
}

let context: AudioContext | null = null;

function ready(): AudioContext | null {
  if (!timerSounds() || typeof AudioContext === 'undefined') return null;
  context ??= new AudioContext();
  if (context.state === 'suspended') void context.resume().catch(() => undefined);
  return context;
}

/** Unlocks the audio output during the tap that starts a timer; does nothing while the timer stays silent. Never throws. */
export function prepareChime(): void {
  try {
    ready();
  } catch {
    return;
  }
}

/** Plays two short, soft notes, only while {@link timerSounds}. Never throws. */
export function playChime(): void {
  try {
    const audio = ready();
    if (!audio) return;
    [523.25, 659.25].forEach((frequency, index) => {
      const from = audio.currentTime + index * 0.35;
      const note = audio.createOscillator();
      const volume = audio.createGain();
      note.frequency.value = frequency;
      volume.gain.setValueAtTime(0, from);
      volume.gain.linearRampToValueAtTime(0.08, from + 0.04);
      volume.gain.exponentialRampToValueAtTime(0.0001, from + 1.1);
      note.connect(volume).connect(audio.destination);
      note.start(from);
      note.stop(from + 1.15);
    });
  } catch {
    return;
  }
}
