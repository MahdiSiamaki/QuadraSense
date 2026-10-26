import { afterEach, describe, expect, it, vi } from 'vitest'

/*
 * A cancel that arrives while the preflight is being answered. The abort event has already fired
 * by the time the upload request exists, so a listener added then never hears it - the guard in
 * uploadImportFile has to look at the signal itself, or the whole file goes anyway.
 */
const controller = new AbortController()

vi.mock('@/api/client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/api/client')>()),
  api: {
    // The preflight: answered, but the operator pressed Cancel while it was in flight.
    post: async () => {
      controller.abort()
      return { source: 'SQM', fileName: 'a.csv', sizeBytes: 3, message: '' }
    },
  },
}))

const { uploadImportFile } = await import('@/api/imports')

afterEach(() => vi.restoreAllMocks())

describe('an upload cancelled during its preflight', () => {
  it('sends no bytes and ends as cancelled', async () => {
    const send = vi.spyOn(XMLHttpRequest.prototype, 'send').mockImplementation(() => {})

    const outcome = uploadImportFile('SQM', new File(['abc'], 'a.csv'), () => {}, controller.signal)

    await expect(outcome).rejects.toMatchObject({ name: 'AbortError' })
    expect(send).not.toHaveBeenCalled()
  })
})
