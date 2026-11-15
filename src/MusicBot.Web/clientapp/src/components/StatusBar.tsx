import { uiText } from "../i18n/ui";
import React from "react";
import { useTranslation } from "react-i18next";
import { IntegrationStatus } from "../hooks/useSignalR";
interface Props {
    signalRConnected: boolean;
    kickStatus: IntegrationStatus;
}
function dotClass(status: "ok" | "warn" | "err" | "connecting"): string {
    return `status-dot ${status}`;
}
function statusToDot(status: IntegrationStatus): "ok" | "warn" | "err" | "connecting" {
    return status === "connected" ? "ok"
        : status === "connecting" ? "connecting"
            : status === "error" ? "err"
                : "warn";
}
export const StatusBar: React.FC<Props> = ({ signalRConnected, kickStatus }) => {
    const { t } = useTranslation();
    return (<div className="status-bar">
      <div className="status-indicator">
        <div className={`status-dot ${signalRConnected ? "ok" : "err"}`}/>
        <span>{signalRConnected ? t("status.overlay") : t("status.disconnected")}</span>
      </div>

      <div className="status-indicator" title={`Kick: ${kickStatus}`}>
        <div className={dotClass(statusToDot(kickStatus))}/>
        <span>{uiText("Kick")}</span>
      </div>
    </div>);
};
