import { describe, expect, it } from 'vitest'

/*
  Class lists that switch each other off, found in the sources rather than in a browser.

  `line-clamp-N` works by setting `display: -webkit-box`. A display utility in the same class list -
  `block`, `flex`, `grid`, … - sets display too, and Tailwind emits it later in the stylesheet, so it
  wins and the clamp does nothing. The Add widget catalogue had `line-clamp-2 block` on every card's
  description, and the KPI widgets printed their full, paragraph-long definitions (2026-10-20). The
  test DOM has no Tailwind, so no component test could see it.
*/
const sources = import.meta.glob('/src/**/*.vue', { query: '?raw', import: 'default', eager: true }) as Record<string, string>

const DISPLAY = new Set(['block', 'inline-block', 'inline', 'flex', 'inline-flex', 'grid', 'inline-grid', 'table', 'contents'])

/** Static class attributes, and string literals inside bound ones. */
function classLists(source: string): string[] {
  const lists: string[] = []
  for (const m of source.matchAll(/\sclass="([^"]*)"/g)) lists.push(m[1] ?? '')
  for (const m of source.matchAll(/:class="([^"]*)"/g)) {
    for (const literal of (m[1] ?? '').matchAll(/'([^']*)'/g)) lists.push(literal[1] ?? '')
  }
  return lists
}

describe('class lists', () => {
  it('never pair line-clamp with a display utility, which switches the clamp off', () => {
    const found: string[] = []
    for (const [file, source] of Object.entries(sources)) {
      for (const list of classLists(source)) {
        const tokens = list.split(/\s+/)
        if (tokens.some((t) => /^line-clamp-\d+$/.test(t)) && tokens.some((t) => DISPLAY.has(t))) {
          found.push(`${file}: "${list}"`)
        }
      }
    }
    expect(found).toEqual([])
  })

  it('finds the vue sources it checks', () => {
    expect(Object.keys(sources).length).toBeGreaterThan(100)
  })
})
