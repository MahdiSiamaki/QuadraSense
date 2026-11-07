import { afterEach, describe, expect, it, vi } from 'vitest'
import RecoverPage from '@/features/auth/RecoverPage.vue'
import { fakeApi, mountPage, OPTIONS, settle } from './harness'

const OPTIONS_PATH = '/api/v1/auth/options'
const START = '/api/v1/auth/recovery/start'
const COMPLETE = '/api/v1/auth/recovery/complete'

const GOOD_PASSWORD = 'a few unrelated words'

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
  document.body.replaceChildren()
})

type Page = Awaited<ReturnType<typeof mountPage>>['wrapper']

async function askForCode(w: Page, who = 'a.person') {
  await w.find('input[autocomplete="username"]').setValue(who)
  await w.find('form').trigger('submit')
  await settle()
}

async function fillIn(w: Page, code = '123456', password = GOOD_PASSWORD) {
  await w.find('input[autocomplete="one-time-code"]').setValue(code)
  const fields = w.findAll('input[autocomplete="new-password"]')
  await fields[0]!.setValue(password)
  await fields[1]!.setValue(password)
}

describe('asking for a code', () => {
  it('says plainly when this system cannot send one', async () => {
    fakeApi({
      [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, recoverByEmail: false, recoverBySms: false } },
    })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    expect(wrapper.find('form').exists()).toBe(false)
    expect(wrapper.text()).toContain('An administrator can reset your password')
  })

  it('offers a choice only when there is one', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS } })
    const both = await mountPage(RecoverPage, '/recover')

    expect(both.wrapper.findAll('[role="radio"]').map((r) => r.text())).toEqual([
      'Email',
      'Text message',
    ])

    fakeApi({ [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, recoverBySms: false } } })
    const one = await mountPage(RecoverPage, '/recover')

    expect(one.wrapper.findAll('[role="radio"]')).toHaveLength(0)
  })

  it('sends the only channel there is', async () => {
    const api = fakeApi({
      [OPTIONS_PATH]: { status: 200, body: { ...OPTIONS, recoverByEmail: false } },
      [START]: { status: 202 },
    })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    await askForCode(wrapper)

    expect(api.bodyOf(START)).toEqual({ identifier: 'a.person', channel: 'Sms' })
  })

  it('reads an empty answer, which is what the server sends on purpose', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [START]: { status: 202 } })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    await askForCode(wrapper)

    expect(wrapper.find('h1').text()).toBe('Enter your code')
  })

  it('never says whether the account exists, because it cannot know', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [START]: { status: 202 } })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    await askForCode(wrapper, 'nobody.at.all')

    expect(wrapper.find('header p').text()).toBe(
      'If that account has a verified email address, a code is on its way. It works for 10 minutes.',
    )
  })

  it('says when the limiter has refused, and stays on the form', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [START]: { status: 429 } })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    await askForCode(wrapper)

    expect(wrapper.find('h1').text()).toBe('Forgot your password?')
    expect(wrapper.find('[role="alert"]').text()).toContain('Too many attempts')
  })

  it('makes a person wait before asking again', async () => {
    fakeApi({ [OPTIONS_PATH]: { status: 200, body: OPTIONS }, [START]: { status: 202 } })
    const { wrapper } = await mountPage(RecoverPage, '/recover')

    await askForCode(wrapper)

    const again = wrapper.findAll('button').find((b) => b.text().startsWith('Send another code'))!

    expect(again.text()).toBe('Send another code in 60s')
    expect(again.attributes('disabled')).toBeDefined()
  })
})

describe('setting the new password', () => {
  async function atCodeStage(extra: Record<string, unknown> = {}) {
    const api = fakeApi({
      [OPTIONS_PATH]: { status: 200, body: OPTIONS },
      [START]: { status: 202 },
      ...extra,
    })
    const page = await mountPage(RecoverPage, '/recover')
    await askForCode(page.wrapper)
    return { api, ...page }
  }

  function submit(w: Page) {
    return w.findAll('button').find((b) => b.text() === 'Set new password')!
  }

  it('waits for a whole code and a password that passes', async () => {
    const { wrapper } = await atCodeStage()

    expect(submit(wrapper).attributes('disabled')).toBeDefined()

    await fillIn(wrapper, '123', GOOD_PASSWORD)
    expect(submit(wrapper).attributes('disabled')).toBeDefined()

    await fillIn(wrapper, '123456', 'short')
    expect(submit(wrapper).attributes('disabled')).toBeDefined()

    await fillIn(wrapper)
    expect(submit(wrapper).attributes('disabled')).toBeUndefined()
  })

  it('catches two passwords that differ before the server has to', async () => {
    const { wrapper } = await atCodeStage()

    await fillIn(wrapper)
    await wrapper.findAll('input[autocomplete="new-password"]')[1]!.setValue('something else entirely')

    expect(wrapper.text()).toContain('The two passwords differ.')
    expect(submit(wrapper).attributes('disabled')).toBeDefined()
  })

  it('sends the identifier with the code', async () => {
    const { api, wrapper } = await atCodeStage({ [COMPLETE]: { status: 204 } })

    await fillIn(wrapper)
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(api.bodyOf(COMPLETE)).toEqual({
      identifier: 'a.person',
      code: '123456',
      newPassword: GOOD_PASSWORD,
    })
    expect(wrapper.find('h1').text()).toBe('Password changed')
  })

  it('forgets the password once it is set', async () => {
    const { wrapper } = await atCodeStage({ [COMPLETE]: { status: 204 } })

    await fillIn(wrapper)
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(wrapper.find('input').exists()).toBe(false)
  })

  it('puts the server\'s word about the code on the code', async () => {
    const { wrapper } = await atCodeStage({
      [COMPLETE]: { status: 400, body: { errors: { code: ['That code is not right.'] } } },
    })

    await fillIn(wrapper)
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(wrapper.find('h1').text()).toBe('Enter your code')
    expect(wrapper.find('[role="alert"]').text()).toBe('That code is not right.')
  })

  it('puts the server\'s word about the password on the password', async () => {
    const { wrapper } = await atCodeStage({
      [COMPLETE]: {
        status: 400,
        body: { errors: { newPassword: ['Do not include your name.'] } },
      },
    })

    await fillIn(wrapper)
    await wrapper.find('form').trigger('submit')
    await settle()

    expect(wrapper.find('[role="alert"]').text()).toBe('Do not include your name.')
  })
})
