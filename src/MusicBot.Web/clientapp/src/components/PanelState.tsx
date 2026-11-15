import { AlertCircle } from "lucide-react";
import { Skeleton } from "./ui/skeleton";
import { Button } from "./ui/button";

export function LoadingState({ label = "Yükleniyor…" }: { label?: string }) {
  return <div className="ui-loading" role="status" aria-busy="true"><span>{label}</span><Skeleton/><Skeleton/><Skeleton/></div>;
}

export function ErrorState({ message, retry }: { message: string; retry: () => void }) {
  return <div className="ui-error" role="alert"><AlertCircle size={24}/><strong>Şu anda yüklenemedi</strong><p>{message}</p><Button variant="outline" onClick={retry}>Tekrar dene</Button></div>;
}
