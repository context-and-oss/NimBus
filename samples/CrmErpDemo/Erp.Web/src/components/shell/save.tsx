import { useEffect, useState, type FormEvent } from 'react';
import { SaveCloseIcon, SaveIcon } from './icons';
import { CommandButton } from './ui';

// Save and Save & Close for a record form. Both buttons submit the form; the second one says
// "close" so the form's submit handler can tell them apart.

const CLOSE = 'close';

/** True when the form was submitted with Save & Close. Enter in a field counts as plain Save. */
export function wantsClose(e: FormEvent<HTMLFormElement>): boolean {
  const submitter = (e.nativeEvent as SubmitEvent).submitter;
  return submitter?.getAttribute('value') === CLOSE;
}

/** The two save commands, side by side in the command bar. */
export function SaveCommands({ formId, saving, disabled }: { formId: string; saving: boolean; disabled?: boolean }) {
  const off = saving || disabled;
  return (
    <>
      <CommandButton type="submit" form={formId} icon={<SaveIcon />} primary disabled={off}>
        {saving ? 'Saving…' : 'Save'}
      </CommandButton>
      <CommandButton type="submit" form={formId} name="intent" value={CLOSE} icon={<SaveCloseIcon />} disabled={off}>
        Save &amp; Close
      </CommandButton>
    </>
  );
}

/** "Saved" flag that clears itself after a few seconds. */
export function useSavedNotice(initial = false) {
  const [saved, setSaved] = useState(initial);
  useEffect(() => {
    if (!saved) return;
    const timer = setTimeout(() => setSaved(false), 3000);
    return () => clearTimeout(timer);
  }, [saved]);
  return [saved, setSaved] as const;
}

export function SavedBar() {
  return <div role="status" className="border-b border-[#bfdcc6] bg-[#e6f4ea] px-5 py-2 text-[13px] text-[#1e6b34]">Saved</div>;
}
