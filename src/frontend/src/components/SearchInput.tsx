import { useEffect, useEffectEvent, useId, useState } from 'react';
import { Input } from './ui';

/** Search box that reports changes after the user pauses typing (avoids one request per keystroke). */
export function SearchInput({ value, onChange, placeholder, label, delay = 300 }: {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  label: string;
  delay?: number;
}) {
  const id = useId();
  const [draft, setDraft] = useState(value);
  const emit = useEffectEvent((next: string) => onChange(next));

  useEffect(() => {
    if (draft === value) return;
    const timer = window.setTimeout(() => emit(draft), delay);
    return () => window.clearTimeout(timer);
  }, [draft, value, delay]);

  return (
    <>
      <label className="sr-only" htmlFor={id}>
        {label}
      </label>
      <Input id={id} type="search" placeholder={placeholder} value={draft} onChange={(e) => setDraft(e.target.value)} />
    </>
  );
}
