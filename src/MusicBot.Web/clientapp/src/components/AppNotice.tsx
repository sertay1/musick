import { uiText } from "../i18n/ui";
import { useEffect, useState } from "react";
export function AppNotice() {
    const [message, setMessage] = useState("");
    useEffect(() => { let timer: ReturnType<typeof setTimeout>; const show = (event: Event) => { setMessage((event as CustomEvent<string>).detail); clearTimeout(timer); timer = setTimeout(() => setMessage(""), 9000); }; window.addEventListener("musick:notice", show); return () => { clearTimeout(timer); window.removeEventListener("musick:notice", show); }; }, []);
    return message ? <div role="alert" style={{ position: "fixed", top: 75, right: 18, maxWidth: 460, zIndex: 9999, background: "var(--surface)", color: "#f59e0b", border: uiText("1px solid #f59e0b"), borderRadius: 10, padding: 15 }}><button aria-label={uiText("Kapat")} onClick={() => setMessage("")} style={{ float: "right", color: "inherit", background: "none", border: 0, cursor: "pointer" }}>×</button>{message}</div> : null;
}
