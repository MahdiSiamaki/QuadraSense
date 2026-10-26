import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import { api } from '@/api/client'
import { handOver } from '@/lib/prefill'
import SimpleExplorer from '@/features/explorer/SimpleExplorer.vue'

/*
  The Explorer's Simple mode: what it offers to whom, which audited requests it sends and when, and
  that no identifier reaches the address. The server's answers are stand-ins; what is under test is
  what is asked for.
*/
const held = vi.hoisted(() => ({ permissions: new Set<string>() }))

vi.mock('@/features/auth/useAuth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/useAuth')>()
  return {
    ...actual,
    useAuth: () => ({
      can: (p: string) => held.permissions.has(p),
      canAny: (...ps: string[]) => ps.some((p) => held.permissions.has(p)),
    }),
  }
})

const NUMBER = '9121234567'
const SIM = '432113991761332'
const HANDSET = '35004012345678'

/** A stand-in answer for each endpoint the views call. */
function answer(path: string): unknown {
  if (path === '/api/v1/lookup/msisdn') {
    return { msisdn: NUMBER, wellFormed: true, count: 0, bindings: [], identifiers: 'Revealed' }
  }
  if (path === '/api/v1/relationships/explore') {
    return {
      centre: NUMBER, kind: 'Msisdn', found: true, truncated: false, identifiers: 'Revealed', withheld: [],
      subscribers: [], sims: [{ kind: 'Imsi', value: SIM, activeBindings: 1, lastConfirmed: null }],
      handsets: [{ kind: 'Imei', value: HANDSET, activeBindings: 1, lastConfirmed: null }],
      paired: [], elapsedMs: 3, rowsExamined: 10,
    }
  }
  return new Promise(() => {})
}

let post: ReturnType<typeof vi.spyOn>
let wrapper: VueWrapper | null = null
let router: Router

async function open(permissions: string[], at = '/explorer') {
  held.permissions = new Set(permissions)
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/explorer', name: 'explorer', component: { template: '<div />' } },
      { path: '/devices/:tac', name: 'device-detail', component: { template: '<div />' } },
    ],
  })
  await router.push(at)
  wrapper = mount(SimpleExplorer, {
    global: { plugins: [router, [VueQueryPlugin, { queryClient: new QueryClient() }]] },
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}

const posted = () => post.mock.calls.map((c: unknown[]) => c[0] as string)

async function search(w: VueWrapper, value: string) {
  await w.find('#simple-identifier').setValue(value)
  await w.find('form').trigger('submit')
  await flushPromises()
}

async function showView(w: VueWrapper, name: string) {
  const tab = w.findAll('[role="tab"]').find((t) => t.text() === name)
  if (!tab) throw new Error(`no "${name}" tab`)
  await tab.trigger('click')
  await flushPromises()
}

function typeOptions(w: VueWrapper) {
  return w.findAll('[role="radiogroup"] button').map((b) => ({
    label: b.text(),
    disabled: b.attributes('aria-disabled') === 'true',
  }))
}

beforeEach(() => {
  post = vi.spyOn(api, 'post').mockImplementation((path: string) => Promise.resolve(answer(path)) as never)
  // The IMSI filters' value lists; nothing here is about them.
  vi.spyOn(api, 'get').mockImplementation(() => new Promise(() => {}))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = null
  vi.restoreAllMocks()
})

describe('the Explorer simple search', () => {
  it('offers only the lookups the user may run', async () => {
    const w = await open(['lookup.subscriber'])

    expect(typeOptions(w)).toEqual([
      { label: 'Auto', disabled: false },
      { label: 'MSISDN', disabled: false },
    ])
  })

  it('offers ICCID with the IMSI, and searches one as the IMSI it was derived from', async () => {
    const w = await open(['lookup.imsi'])
    expect(typeOptions(w)).toEqual([
      { label: 'Auto', disabled: false },
      { label: 'IMSI', disabled: false },
      { label: 'ICCID', disabled: false },
    ])

    // SIM's ICCID: 8998, its digits 4-7, 000, its last eight.
    await search(w, '8998113900091761332')

    expect(posted()).toEqual(['/api/v1/lookup/imsi/search'])
    expect(JSON.stringify(post.mock.calls[0]?.[1])).toContain(SIM)
  })

  it('refuses, before sending anything, a lookup the user may not run', async () => {
    const w = await open(['lookup.subscriber'])
    await search(w, SIM)

    expect(w.find('#simple-identifier-help').text()).toContain('lookup.imsi')
    expect(posted()).toEqual([])
  })

  it('sends one lookup for the details, and each other view only when it is opened, once', async () => {
    const w = await open(['lookup.subscriber', 'lookup.imsi', 'lookup.imei'])
    await search(w, '0912 123 4567')
    expect(posted()).toEqual(['/api/v1/lookup/msisdn'])
    expect(post.mock.calls[0]?.[1]).toEqual({ msisdn: NUMBER })

    await showView(w, 'Relationships')
    await showView(w, 'Details')
    await showView(w, 'Relationships')
    expect(posted()).toEqual(['/api/v1/lookup/msisdn', '/api/v1/relationships/explore'])

    // The same search again changes nothing, so it sends nothing.
    await search(w, NUMBER)
    expect(posted()).toHaveLength(2)
  })

  it('follows a link and comes back, with no identifier ever in the address', async () => {
    const w = await open(['lookup.subscriber', 'lookup.imsi', 'lookup.imei'])
    await search(w, NUMBER)
    await showView(w, 'Relationships')

    const sim = w.findAll('button').find((b) => b.text().includes(SIM))
    await sim!.trigger('click')
    await flushPromises()

    expect((w.find('#simple-identifier').element as HTMLInputElement).value).toBe(SIM)
    expect(w.text()).toContain('SIM (IMSI)')
    expect(posted().at(-1)).toBe('/api/v1/lookup/imsi/search')

    await w.findAll('button').find((b) => b.text().startsWith('← Back'))!.trigger('click')
    await flushPromises()
    expect((w.find('#simple-identifier').element as HTMLInputElement).value).toBe(NUMBER)

    for (const value of [NUMBER, SIM, HANDSET]) expect(router.currentRoute.value.fullPath).not.toContain(value)
    // The type is kept, so Refresh keeps it.
    expect(router.currentRoute.value.query['kind']).toBe('msisdn')
  })

  it('starts with the type an old /lookup link named', async () => {
    const w = await open(['lookup.subscriber', 'lookup.imsi'], '/explorer?kind=imsi')
    const chosen = w.findAll('[role="radiogroup"] button').find((b) => b.attributes('aria-checked') === 'true')
    expect(chosen?.text()).toBe('IMSI')
  })

  it("takes a dashboard's hand-over once, and its relationships target opens Relationships", async () => {
    handOver('explorer:relationships', NUMBER)
    const w = await open(['lookup.subscriber'])

    expect(posted()).toEqual(['/api/v1/relationships/explore'])
    const selected = w.findAll('[role="tab"]').find((t) => t.attributes('aria-selected') === 'true')
    expect(selected?.text()).toBe('Relationships')
  })
})
