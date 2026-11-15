import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import tr from "./locales/tr.json";
import en from "./locales/en.json";

export const SUPPORTED_LANGUAGES = ["tr", "en"] as const;
export type AppLanguage = (typeof SUPPORTED_LANGUAGES)[number];

i18n
  .use(initReactI18next)
  .init({
    resources: {
      tr: { translation: tr },
      en: { translation: en },
    },
    lng: localStorage.getItem("musick-language") === "en" ? "en" : "tr",
    fallbackLng: "tr",
    supportedLngs: ["tr", "en"],
    interpolation: {
      escapeValue: false,
    },
  });

export default i18n;
