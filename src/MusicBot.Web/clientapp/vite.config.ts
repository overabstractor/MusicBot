import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'build',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:3050',
        changeOrigin: true,
      },
      '/hub': {
        target: 'http://127.0.0.1:3050',
        ws: true,
        changeOrigin: true,
      },
    },
  },
})
