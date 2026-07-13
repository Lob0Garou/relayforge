import { defineConfig } from 'vitest/config';
import { loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig(({mode})=>{const env=loadEnv(mode,process.cwd(),'');return {plugins:[react()],server:{proxy:{'/api':env.VITE_API_PROXY_TARGET??'http://localhost:5000','/health':env.VITE_API_PROXY_TARGET??'http://localhost:5000'}},test:{environment:'jsdom',setupFiles:'./tests/setup.ts',css:true,exclude:['e2e/**','node_modules/**']}}});
