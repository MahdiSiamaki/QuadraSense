// Imported from vue-query, NOT from query-core.
//
// query-core is present only as a transitive dependency, and importing QueryClient from it yields
// a different class than the plugin expects. The plugin then never mounts the instance, so every
// useQuery sits in 'pending' forever without issuing a single request - nothing throws, no error
// appears, and the whole app renders skeletons. Worth the sentence: the symptom points nowhere
// near the cause.
import { QueryClient } from '@tanstack/vue-query'

/**
 * The one query cache, created here rather than by the Vue plugin.
 *
 * The navigation guard has to resolve the signed-in user before any component exists, and it must
 * put the answer in the same cache the components then read - otherwise every first render
 * refetches what the guard just fetched, and a hard refresh shows a flash of loading state over
 * data that is already in memory.
 *
 * Creating it in a module and handing it to the plugin is the only arrangement where the router
 * and the app share one instance.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Dashboard aggregates take seconds against 126M rows. Refetching them every time the
      // window regains focus would make the app feel slower without making it fresher - the
      // source data changes once a day.
      refetchOnWindowFocus: false,
      retry: 1,
      staleTime: 60_000,
    },
  },
})
