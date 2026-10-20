import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
// API_URL / WEB_PORT let the acceptance tests run an isolated pair next to `npm run dev`.
export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(process.env.WEB_PORT ?? 5173),
    strictPort: true,
    proxy: {
      '/api': process.env.API_URL ?? 'http://localhost:5110',
    },
  },
})
