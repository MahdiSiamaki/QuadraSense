import { beforeEach, describe, expect, it } from 'vitest'
import { reactive } from 'vue'
import { createPinia, setActivePinia } from 'pinia'
import { MAX_HISTORY, useDashboardDraft } from '@/features/dashboard/composables/useDashboardDraft'
import { overlaps } from '@/features/dashboard/composables/layout'
import { normalizeConfig } from '@/features/dashboard/registry/validate'
import { defaultConfig } from '@/features/dashboard/registry/contract'
import { devicePopulation } from '@/features/dashboard/templates/devicePopulation'
import type { DashboardDefinition } from '@/features/dashboard/types/definition'

const saved: DashboardDefinition = JSON.parse(JSON.stringify(devicePopulation.definition))

function draft() {
  const store = useDashboardDraft()
  store.begin('42', saved, 'Operations', '')
  return store
}

describe('the dashboard draft', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('starts as a copy of the saved version, not a reference to it', () => {
    const store = draft()
    store.updateWidget('w_dpvendors', { title: 'Vendors' })
    expect(saved.widgets.find((w) => w.id === 'w_dpvendors')!.title).toBe('Top vendors')
    expect(store.isDirty).toBe(true)
  })

  it('adds a widget in the first free place with a new, unique id - one history entry', () => {
    const store = draft()
    const id = store.addWidget('metric.kpi', defaultConfig('metric.kpi'), 'Extra')!
    expect(id).toMatch(/^w_[a-z0-9]{6,32}$/)
    expect(saved.widgets.some((w) => w.id === id)).toBe(false)
    const added = store.widget(id)!
    for (const other of store.definition!.widgets) if (other.id !== id) expect(overlaps(added.layout, other.layout)).toBe(false)
    expect(store.canUndo).toBe(true)
    store.undo()
    expect(store.widget(id)).toBeUndefined()
    expect(store.isDirty).toBe(false)
  })

  it('duplicates with a new id and a config of its own', () => {
    const store = draft()
    const copyId = store.duplicateWidget('w_dpvendors')!
    expect(copyId).not.toBe('w_dpvendors')
    store.updateWidget(copyId, { config: { ...store.widget(copyId)!.config, limit: 20 } })
    expect(store.widget('w_dpvendors')!.config.limit).toBe(10)
    expect(store.widget(copyId)!.config.limit).toBe(20)
  })

  it('records a whole drag as one entry, and a drag that moved nothing as none', () => {
    const store = draft()
    const a = store.widget('w_dpchurn')!.layout
    const b = store.widget('w_dpvendormove')!.layout
    store.commitLayout([
      { id: 'w_dpchurn', ...b },
      { id: 'w_dpvendormove', ...a },
    ])
    expect(store.widget('w_dpchurn')!.layout).toEqual(b)
    store.commitLayout([{ id: 'w_dpchurn', ...b }])
    store.undo()
    expect(store.widget('w_dpchurn')!.layout).toEqual(a)
    expect(store.canUndo).toBe(false)
  })

  it('undoes and redoes exactly, and keeps every id', () => {
    const store = draft()
    const ids = store.definition!.widgets.map((w) => w.id)
    store.removeWidget('w_dpenrichment')
    store.moveWidget('w_dpmodels', 'start')
    store.resizeWidget('w_dpvendors', 'full')
    const changed = JSON.stringify(store.definition)
    store.undo()
    store.undo()
    store.undo()
    expect(store.definition!.widgets.map((w) => w.id)).toEqual(ids)
    expect(store.isDirty).toBe(false)
    store.redo()
    store.redo()
    store.redo()
    expect(JSON.stringify(store.definition)).toBe(changed)
  })

  it('forgets redo once something new is done', () => {
    const store = draft()
    store.removeWidget('w_dpenrichment')
    store.undo()
    store.rename('Ops')
    expect(store.canRedo).toBe(false)
  })

  it(`keeps at most ${MAX_HISTORY} steps`, () => {
    const store = draft()
    for (let i = 0; i < MAX_HISTORY + 20; i++) store.rename(`Name ${i}`)
    let steps = 0
    while (store.canUndo) {
      store.undo()
      steps++
    }
    expect(steps).toBe(MAX_HISTORY)
  })

  it('discards back to the saved version, and a save becomes the new base', () => {
    const store = draft()
    store.rename('Changed')
    store.discard()
    expect(store.name).toBe('Operations')
    expect(store.isDirty).toBe(false)

    store.rename('Saved name')
    store.markSaved(store.definition!, 'Saved name', '')
    expect(store.isDirty).toBe(false)
    expect(store.canUndo).toBe(false)
  })

  it('remembers the revision it was taken from until a save names the next one', () => {
    const store = useDashboardDraft()
    store.begin('42', saved, 'Operations', '', 3)
    expect(store.revision).toBe(3)
    store.rename('Changed')
    store.markSaved(store.definition!, 'Changed', '', undefined, 4)
    expect(store.revision).toBe(4)
    store.end()
    expect(store.revision).toBeNull()
  })

  it('adds a widget with its subtitle as one step: one undo removes it', () => {
    const store = draft()
    const id = store.addWidget('metric.kpi', defaultConfig('metric.kpi'), 'Extra', {}, 'Since Monday')!
    expect(store.widget(id)!.subtitle).toBe('Since Monday')
    store.undo()
    expect(store.widget(id)).toBeUndefined()
    expect(store.isDirty).toBe(false)
  })

  it("applies the settings dialog's three values as one step", () => {
    const store = draft()
    store.setSettings({ description: 'For the night shift', refreshMinutes: 15, defaults: { countBy: 'handsets' } })
    expect(store.description).toBe('For the night shift')
    expect(store.definition!.refreshMinutes).toBe(15)
    expect(store.definition!.defaults).toEqual({ countBy: 'handsets' })
    store.undo()
    expect(store.isDirty).toBe(false)
    expect(store.canUndo).toBe(false)
  })

  it('renames with the description the dialog showed as one step', () => {
    const store = draft()
    store.rename('Night shift', 'What the night shift watches')
    expect(store.name).toBe('Night shift')
    expect(store.description).toBe('What the night shift watches')
    store.undo()
    expect(store.isDirty).toBe(false)
    expect(store.canUndo).toBe(false)
  })

  it('takes settings straight from a reactive form, arrays included, and keeps a copy of its own', () => {
    // What the Add widget dialog hands over: its form is reactive, and every list in it a proxy.
    const form = reactive({ config: defaultConfig('change.daily') })
    const store = draft()
    const id = store.addWidget('change.daily', normalizeConfig('change.daily', form.config), 'Daily')!
    expect(store.widget(id)!.config.series).toEqual(['added', 'removed', 'cumulative'])

    store.updateWidget(id, { config: normalizeConfig('change.daily', { ...form.config, series: ['net'] }) })
    expect(store.widget(id)!.config.series).toEqual(['net'])
    ;(form.config.series as string[]).push('net')
    expect(store.widget(id)!.config.series).toEqual(['net'])
  })
})
