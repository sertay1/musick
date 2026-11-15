import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    rollupOptions: { output: { manualChunks: { 'vendor-react': ['react', 'react-dom', 'react-dom/client'], 'vendor-signalr': ['@microsoft/signalr'] } } },
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
