import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import App from './App.vue'
import { router } from './router'
import { queryClient } from './lib/queryClient'
import './design-system/tokens.css'

const app = createApp(App)

app.use(createPinia())
app.use(router)
// The client is created in lib/queryClient so the navigation guard and the components share one
// cache: the guard resolves the signed-in user before any component mounts, and every component
// then reads that same answer instead of fetching it again.
app.use(VueQueryPlugin, { queryClient })

app.mount('#app')
