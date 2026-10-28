import type { Config } from 'tailwindcss';
export default {
  content: ['./src/**/*.{ts,tsx}'],
  theme: { extend: { colors: { brand: { 50: '#eef6f4', 100: '#d5e9e4', 500: '#1f7a6b', 600: '#186356', 700: '#134d43', 900: '#0b2e28' } } } },
  plugins: [],
} satisfies Config;
