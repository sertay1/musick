import { useEffect, useRef, useState } from "react";

/** Saves the latest edit after a short pause, including when navigating to another page. */
export function useAutoSave<T>(value: T, save: (value: T) => Promise<unknown>, enabled: boolean) {
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const latest = useRef({ value, save, enabled });
  latest.current = { value, save, enabled };
  const baseline = useRef<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const mounted = useRef(true);
  const chain = useRef(Promise.resolve());
  const serialized = JSON.stringify(value);
  const flush = () => {
    const snapshot = latest.current;
    const json = JSON.stringify(snapshot.value);
    if (!snapshot.enabled || baseline.current === null || json === baseline.current) return;
    baseline.current = json;
    if (mounted.current) setStatus("saving");
    chain.current = chain.current.catch(() => {}).then(async () => {
      try {
        await snapshot.save(snapshot.value);
        if (mounted.current && JSON.stringify(latest.current.value) === json) setStatus("saved");
      } catch {
        if (baseline.current === json) baseline.current = "";
        if (mounted.current) setStatus("error");
      }
    });
  };
  const flushRef = useRef(flush);
  flushRef.current = flush;
  useEffect(() => {
    if (!enabled) return;
    if (baseline.current === null) { baseline.current = serialized; return; }
    if (serialized === baseline.current) return;
    setStatus("saving");
    timer.current = setTimeout(() => flushRef.current(), 400);
    return () => { if (timer.current) clearTimeout(timer.current); };
  }, [serialized, enabled]);
  useEffect(() => {
    mounted.current = true;
    const onPageHide = () => flushRef.current();
    window.addEventListener("pagehide", onPageHide);
    return () => {
      mounted.current = false;
      window.removeEventListener("pagehide", onPageHide);
      if (timer.current) clearTimeout(timer.current);
      flushRef.current();
    };
  }, []);
  return { status, retry: flush };
}
