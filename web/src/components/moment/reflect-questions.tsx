import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { momentCopy } from '@/lib/moment/copy';
import { reflectQuestions } from '@/lib/moment/questions';

/**
 * The optional questions on the reflect step: one at a time, "Another question" cycles through them and "Skip the
 * questions" hides them and returns focus to the step heading without scrolling. A question is only something to
 * think about; nothing about it is stored.
 */
export function ReflectQuestions() {
  useTranslation();
  const id = useId();
  const questions = reflectQuestions();
  const [index, setIndex] = useState<number | null>(0);
  const sectionRef = useRef<HTMLElement>(null);

  if (index === null) return null;

  function skip(): void {
    sectionRef.current?.parentElement
      ?.querySelector<HTMLElement>('h1')
      ?.focus({ preventScroll: true });
    setIndex(null);
  }

  return (
    <section
      ref={sectionRef}
      aria-labelledby={`${id}-label`}
      className="flex flex-col gap-3 rounded-lg bg-secondary p-4 text-secondary-foreground"
    >
      <h2 id={`${id}-label`} className="text-[1rem] font-medium">
        {momentCopy.questionsLabel}
      </h2>
      <p aria-live="polite" aria-atomic="true" className="text-[1.125rem] leading-relaxed">
        {questions[index % questions.length]!.text}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" onClick={() => setIndex(index + 1)}>
          {momentCopy.anotherQuestion}
        </Button>
        <Button type="button" variant="ghost" onClick={skip}>
          {momentCopy.skipQuestions}
        </Button>
      </div>
    </section>
  );
}
