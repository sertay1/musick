import React, { useEffect, useState } from "react";
import { Music } from "lucide-react";

export function Artwork({ trackId, coverUrl, className, style }: { trackId: string; coverUrl?: string | null; className?: string; style?: React.CSSProperties }) {
  const [failed, setFailed] = useState(0);
  useEffect(() => setFailed(0), [trackId, coverUrl]);
  const base = import.meta.env.VITE_API_URL || "";
  const fallback = `${base}/api/artwork/image?trackId=${encodeURIComponent(trackId)}`;
  const src = failed === 0 && coverUrl ? coverUrl : fallback;
  if (failed >= (coverUrl ? 2 : 1)) return <span className={className} style={{ display: "inline-flex", alignItems: "center", justifyContent: "center", width: 40, height: 40, flexShrink: 0, ...style }}><Music size={20} /></span>;
  return <img src={src} alt="" loading="lazy" referrerPolicy="no-referrer" className={className} style={style} onError={() => setFailed(n => n + 1)} />;
}
