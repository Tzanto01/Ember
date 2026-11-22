import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { controlClass } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { momentCopy } from '@/lib/moment/copy';
import { reflectQuestions } from '@/lib/moment/questions';
import { cn } from '@/lib/utils';
import { CharactersLeft, maxReflectText } from './characters-left';

/**
 * The optional questions on the reflect step: one at a time, each with an optional answer box. "Another question"
 * cycles through them and keeps every answer by question id in `answers`; "Skip the questions" hides them, calls
 * `onSkip` and returns focus to the step heading without scrolling.
 */
export function ReflectQuestions({
  answers,
  onAnswer,
  onSkip,
}: {
  answers: Record<string, string>;
  onAnswer: (questionId: string, text: string) => void;
  onSkip: () => void;
}) {
  const { t } = useTranslation();
  const id = useId();
  const questions = reflectQuestions();
  const [index, setIndex] = useState<number | null>(0);
  const sectionRef = useRef<HTMLElement>(null);

  if (index === null) return null;
  const question = questions[index % questions.length]!;
  const answer = answers[question.id] ?? '';

  function skip(): void {
    sectionRef.current?.parentElement
      ?.querySelector<HTMLElement>('h1')
      ?.focus({ preventScroll: true });
    onSkip();
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
      <div className="flex flex-col gap-2">
        <Label htmlFor={`${id}-answer`} className="text-[1.125rem] leading-relaxed font-normal">
          <span aria-live="polite" aria-atomic="true">
            {question.text}
          </span>
          <span className="text-muted-foreground"> {t('form.optional')}</span>
        </Label>
        <textarea
          id={`${id}-answer`}
          aria-describedby={`${id}-count`}
          value={answer}
          rows={3}
          maxLength={maxReflectText}
          onChange={(event) => onAnswer(question.id, event.target.value)}
          className={cn(controlClass, 'min-h-[6rem] resize-y leading-relaxed')}
        />
        <CharactersLeft id={`${id}-count`} text={answer} />
      </div>
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
