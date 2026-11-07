import { beforeEach, describe, expect, it, vi } from 'vitest'

/*
  A 401 while the first navigation is still under way.

  The shell asks for the current user on every load, and a visitor who is signed out gets a 401. If
  that answer lands before the first navigation has finished, `currentRoute` is still the start
  location, which is not public, and a person opening /recover directly was sent to the sign-in page
  with no way to tell why.
*/
describe('a 401 before the first navigation has finished', () => {
  beforeEach(() => vi.resetModules())

  async function routerAt(path: string) {
    window.history.replaceState({}, '', path)
    const { router } = await import('@/router')
    const replace = vi.spyOn(router, 'replace').mockResolvedValue(undefined)
    return { replace }
  }

  it('leaves a person alone on a page that needs no account', async () => {
    const { replace } = await routerAt('/recover')

    const { notifyUnauthenticated: notify } = await import('@/api/client')
    notify()

    expect(replace).not.toHaveBeenCalled()
  })

  it('still sends them to sign in from a page that does, and keeps where they were', async () => {
    const { replace } = await routerAt('/devices?vendor=Samsung')

    const { notifyUnauthenticated: notify } = await import('@/api/client')
    notify()

    expect(replace).toHaveBeenCalledWith({
      name: 'login',
      query: { next: '/devices?vendor=Samsung' },
    })
  })
})
