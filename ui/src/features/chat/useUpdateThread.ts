import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import { useToast } from '../../app/toast';

/** `PATCH /api/threads/{id}` (rename, archive or model), refreshing the thread and the list. */
export function useUpdateThread(
  threadId: string,
  successMessage?: (archived: boolean | null) => string | null,
) {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (body: { title?: string | null; archived?: boolean | null; model?: string | null }) =>
      call(() =>
        api.PATCH('/api/threads/{id}', {
          params: { path: { id: threadId } },
          // null leaves a field unchanged; model "" clears the override.
          body: { title: body.title ?? null, archived: body.archived ?? null, model: body.model ?? null },
        }),
      ),
    onSuccess: (_, body) => {
      void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
      void queryClient.invalidateQueries({ queryKey: keys.threads });
      const message = successMessage?.(body.archived ?? null);
      if (message) toast(message, 'success');
    },
    onError: (error) => toast(errorMessage(error), 'error'),
  });
}
