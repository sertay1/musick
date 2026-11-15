import { useCallback, useEffect, useRef, useState } from "react";
export function usePlayerAction() {
    const pending = useRef(false), mounted = useRef(true);
    const [busy,setBusy] = useState(false), [error,setError] = useState("");
    useEffect(() => { mounted.current=true; return () => { mounted.current=false; }; },[]);
    const reportError = useCallback((e:unknown) => { if(mounted.current)setError(e instanceof Error ? e.message : String(e)); },[]);
    const run = useCallback(async(action:()=>unknown):Promise<boolean> => {
        if(pending.current)return false;
        pending.current=true; if(mounted.current){setBusy(true);setError("");}
        try { await action(); return true; }
        catch(e){reportError(e);return false;}
        finally { pending.current=false;if(mounted.current)setBusy(false); }
    },[reportError]);
    return {busy,error,run,reportError};
}
