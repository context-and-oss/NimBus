import js from "@eslint/js";
import globals from "globals";
import tsParser from "@typescript-eslint/parser";
import tsPlugin from "@typescript-eslint/eslint-plugin";
import reactHooks from "eslint-plugin-react-hooks";
import prettier from "eslint-config-prettier";

export default [
  js.configs.recommended,
  {
    files: ["src/**/*.{ts,tsx}"],
    languageOptions: {
      parser: tsParser,
      parserOptions: {
        ecmaFeatures: { jsx: true },
      },
      globals: {
        ...globals.browser,
        ...globals.node,
        JSX: "readonly",
        moment: "readonly",
      },
    },
    plugins: {
      "@typescript-eslint": tsPlugin,
      "react-hooks": reactHooks,
    },
    rules: {
      // The rule set of eslint-plugin-react-hooks 5's recommended preset. From 7 the preset
      // also turns on the React Compiler rules as errors; adopting those is its own change.
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
      "max-classes-per-file": "off",
      "no-unused-vars": "off",
      // TypeScript itself checks undefined symbols (incl. type-only React
      // namespace refs that core no-undef false-positives on).
      "no-undef": "off",
    },
  },
  prettier,
];
