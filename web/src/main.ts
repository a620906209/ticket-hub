import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import './styles/morandi.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { useAuthStore } from './stores/auth'

const app = createApp(App)

app.use(createPinia())
app.use(ElementPlus)

// 查證後發現（真的用瀏覽器跑過才抓到）：Vue Router 在 app.use(router) 當下就會非同步開始
// 解析初始路由，不是等 app.mount() 才開始——如果這裡在 bootstrap 完成前就 app.use(router)，
// beforeEach 的第一次導覽會在 isAuthenticated 還是 false 的狀態下跑完並導去 /login，
// 之後就算 bootstrapAsync() 換發成功，畫面也不會自動導回來（沒有東西會重新觸發那次導覽）。
// 修法：app.use(router) 延後到 bootstrapAsync() 確定跑完之後才做，讓路由的初始導覽拿到的
// 一定是 bootstrap 完成後的登入狀態。
const authStore = useAuthStore()
await authStore.bootstrapAsync()

app.use(router)
app.mount('#app')
