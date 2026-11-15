import React from "react";
import { useTranslation } from "react-i18next";
import { AlertTriangle } from "lucide-react";
import { AlertDialog, AlertDialogContent, AlertDialogHeader, AlertDialogTitle, AlertDialogDescription, AlertDialogFooter } from "./ui/alert-dialog";
import { Button } from "./ui/button";

interface Props {
  title: string;
  message?: React.ReactNode;
  confirmText?: string;
  cancelText?: string;
  danger?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export const ConfirmModal: React.FC<Props> = ({
  title, message, confirmText, cancelText, danger = false, onConfirm, onCancel,
}) => {
  const { t } = useTranslation();
  const resolvedConfirm = confirmText ?? t("common.confirm");
  const resolvedCancel = cancelText ?? t("common.cancel");

  return (
    <AlertDialog open onOpenChange={open => { if (!open) onCancel(); }}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertTriangle size={26} style={{ color: danger ? "var(--red)" : "var(--accent)", marginBottom: 12 }}/>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          {message && <AlertDialogDescription render={<div/>}>{message}</AlertDialogDescription>}
        </AlertDialogHeader>
        <AlertDialogFooter>
          <Button variant="outline" onClick={onCancel}>{resolvedCancel}</Button>
          <Button variant={danger ? "destructive" : "default"} onClick={onConfirm}>{resolvedConfirm}</Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
};
