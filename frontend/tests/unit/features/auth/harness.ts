import { vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { Component } from 'vue'

/*
  The pages before sign-in, mounted with the real query layer and a real router, with only the
  network replaced: what they send and how they read the answer are the behaviour under test.
*/

export interface Reply {
  status: number
  body?: unknown
}

/** A stand-in for the API: answers by path, and remembers what it was sent. */
export function fakeApi(routes: Record<string, Reply | Reply[]>) {
  const sent: Array<{ path: string; body: unknown }> = []

  const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
    const path = new URL(url, 'http://test').pathname
    sent.push({ path, body: init?.body ? JSON.parse(init.body as string) : null })

    const route = routes[path]
    const reply = Array.isArray(route) ? (route.length > 1 ? route.shift()! : route[0]!) : route

    if (!reply) return new Response(null, { status: 404 })

    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), {
      status: reply.status,
      headers: { 'Content-Type': 'application/json' },
    })
  })

  vi.stubGlobal('fetch', fetchMock)

  return { sent, bodyOf: (path: string) => sent.filter((s) => s.path === path).at(-1)?.body }
}

export const OPTIONS = {
  recoverByEmail: true,
  recoverBySms: true,
  twoStepAvailable: true,
  rememberDays: 7,
  codeMinutes: 10,
}

export const USER = { id: 1, username: 'a.person', permissions: [], mustChangePassword: false }

/** Mounts a page at a route, with somewhere to go. */
export async function mountPage(page: Component, at = '/login') {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/login', name: 'login', component: page },
      { path: '/recover', name: 'recover', component: page },
      { path: '/', name: 'home', component: { template: '<p>home</p>' } },
      { path: '/deep/page', component: { template: '<p>deep</p>' } },
    ],
  })

  await router.push(at)
  await router.isReady()

  const wrapper = mount(page, {
    attachTo: document.body,
    global: {
      plugins: [
        router,
        [VueQueryPlugin, { queryClient: new QueryClient({ defaultOptions: { queries: { retry: false } } }) }],
      ],
    },
  })

  await flushPromises()

  return { wrapper, router }
}

/** Long enough for a request, its answer and the render after it. */
export async function settle() {
  for (let i = 0; i < 6; i++) {
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 0))
  }
}
