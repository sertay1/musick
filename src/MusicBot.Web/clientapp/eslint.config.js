import js from "@eslint/js";
import ts from "typescript-eslint";
import hooks from "eslint-plugin-react-hooks";
import globals from "globals";

export default ts.config(
  { linterOptions: { reportUnusedDisableDirectives: false } },
  { ignores: ["node_modules/**", "../wwwroot/**", "src/**/*.test.*", "src/setupTests.*", "src/reportWebVitals.*"] },
  js.configs.recommended,
  ...ts.configs.recommended,
  {
    files: ["src/**/*.{ts,tsx}", "vite.config.ts"],
    languageOptions: { globals: { ...globals.browser, ...globals.node } },
    plugins: { "react-hooks": hooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      // Existing API contracts and optional callback arguments predate this presentation change.
      "@typescript-eslint/no-explicit-any": "off",
      "@typescript-eslint/no-unused-vars": "off",
      "no-empty": ["error", { allowEmptyCatch: true }],
      "@typescript-eslint/no-unused-expressions": ["error", { allowTernary: true }],
    },
  },
);
