import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import en from "./locales/en.json";
import es from "./locales/es.json";

export const SUPPORTED_LANGUAGES = ["en", "es"] as const;
export type AppLanguage = (typeof SUPPORTED_LANGUAGES)[number];

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: {
      en: { translation: en },
      es: { translation: es },
    },
    fallbackLng: "en",
    supportedLngs: SUPPORTED_LANGUAGES as unknown as string[],
    // es-ES / es-MX / es-419 → es ; en-US / en-GB → en
    nonExplicitSupportedLngs: true,
    load: "languageOnly",
    detection: {
      // First run with no saved choice → follow the browser; then persist the user's manual choice.
      order: ["localStorage", "navigator"],
      caches: ["localStorage"],
      lookupLocalStorage: "musicbot-lang",
    },
    interpolation: {
      // React already escapes values, so i18next must not double-escape.
      escapeValue: false,
    },
  });

export default i18n;
