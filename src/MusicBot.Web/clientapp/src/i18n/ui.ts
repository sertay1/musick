import i18n from "./index";
import english from "./locales/en-ui.json";
export function uiText(text: string, values?: Record<string, unknown>): string {
  let result = i18n.language.startsWith("en") ? (english as Record<string, string>)[text] ?? text : text;
  if (values) for (const [key, value] of Object.entries(values)) result = result.split("{{" + key + "}}").join(String(value));
  return result;
}
