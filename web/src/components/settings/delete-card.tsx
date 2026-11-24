import { type ReactNode, type SubmitEvent, useRef, useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';
import { BusyButton } from '@/components/auth/busy-button';
import { useField, type Validator, validateAll } from '@/components/auth/use-field';
import { FormField, focusFirstInvalid } from '@/components/form-field';
import { DeleteDeviceData } from '@/components/local/device-data-settings';
import { SettingsCard } from '@/components/settings/settings-card';
import { useSettingsStatus } from '@/components/settings/settings-status';
import { Input, PasswordInput } from '@/components/ui/input';
import { t } from '@/lib/i18n';
import type { Me } from '@/lib/models';
import { asApiError } from '@/lib/problem';
import { api, auth, push } from '@/lib/services';

const passwordRequired: Validator = (value) => (value ? null : t('settings.passwordRequired'));
const noPassword: Validator = () => null;
const deleteWord: Validator = (value) => {
  const word = value.trim().normalize('NFC');
  const expected = t('delete.word');
  if (!word) return t('settings.delete.typeDelete', { word: expected });
  return word === expected ? null : t('settings.delete.typeDeleteCaps', { word: expected });
};

/**
 * Deletes the account after its password (when it has one) and the typed delete word, then this device's copy, and
 * signs out; without an account, deletes everything on this device instead. `more` follows the form.
 */
export function DeleteCard({ title, me, local, more }: { title: string; me: Me; local: boolean; more?: ReactNode }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { busy, isBusy, start, finish, fail, outcome } = useSettingsStatus();
  const deleteForm = useRef<HTMLFormElement>(null);
  const [deleteErrors, setDeleteErrors] = useState<{ password?: string; confirm?: string }>({});
  const password = useField(me.hasPassword ? passwordRequired : noPassword);
  const confirm = useField(deleteWord);

  async function deleteAccount(event: SubmitEvent<HTMLFormElement>): Promise<void> {
    event.preventDefault();
    if (isBusy()) return;
    if (!validateAll(deleteForm.current, me.hasPassword ? [password, confirm] : [confirm])) return;
    start('delete');
    setDeleteErrors({});
    try {
      await api.deleteAccount(
        me.hasPassword ? { password: password.value, confirm: 'DELETE' } : { confirm: 'DELETE' },
      );
    } catch (error) {
      const apiError = asApiError(error);
      if (apiError.status === 400) {
        flushSync(() =>
          setDeleteErrors(
            me.hasPassword
              ? { password: t('settings.passwordMismatch') }
              : { confirm: t('settings.delete.typeDeleteCaps', { word: t('delete.word') }) },
          ),
        );
        focusFirstInvalid(deleteForm.current);
      } else {
        fail('delete', apiError);
      }
      finish();
      return;
    }
    await push.forgetDevice();
    await auth.wipeDevice();
    auth.clearSession();
    await auth.refreshAntiforgery();
    await navigate('/login?reason=deleted');
  }

  return (
    <SettingsCard id="delete" title={title} farewell>
      {local ? (
        <DeleteDeviceData />
      ) : (
        <>
          <p>{t('settings.delete.intro')}</p>
          <form
            ref={deleteForm}
            noValidate
            onSubmit={(event) => void deleteAccount(event)}
            className="flex flex-col gap-3"
          >
            {me.hasPassword && (
              <>
                <input
                  className="sr-only"
                  type="email"
                  name="username"
                  autoComplete="username"
                  value={me.email ?? ''}
                  readOnly
                  tabIndex={-1}
                  aria-hidden="true"
                />
                <FormField label={t('settings.yourPassword')} error={password.error ?? deleteErrors.password ?? null}>
                  {(field) => (
                    <PasswordInput {...field} {...password.input} autoComplete="current-password" />
                  )}
                </FormField>
              </>
            )}
            <FormField
              label={t('settings.delete.confirmLabel', { word: t('delete.word') })}
              error={confirm.error ?? deleteErrors.confirm ?? null}
            >
              {(field) => (
                <Input
                  {...field}
                  {...confirm.input}
                  type="text"
                  autoComplete="off"
                  autoCapitalize="characters"
                  spellCheck={false}
                />
              )}
            </FormField>
            {outcome('delete')}
            <div>
              <BusyButton
                type="submit"
                variant="destructive"
                busy={busy === 'delete'}
                busyLabel={t('settings.delete.deleting')}
              >
                {t('settings.delete.submit')}
              </BusyButton>
            </div>
          </form>
        </>
      )}
      {more}
    </SettingsCard>
  );
}
