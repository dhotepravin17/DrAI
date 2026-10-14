import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API runs on the "http" launch profile of SmartQueue.Api.
const apiTarget = process.env.SMARTQUEUE_API_URL ?? 'http://localhost:5221'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true },
      '/hubs': { target: apiTarget, changeOrigin: true, ws: true },
    },
  },
})
