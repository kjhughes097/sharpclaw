import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, call } from '../../api/client';
import { errorMessage } from '../../api/errors';
import { keys } from '../../api/queryKeys';
import { useToast } from '../../app/toast';

/** `PATCH /api/threads/{id}` (rename or archive), refreshing the thread and the list. */
export function useUpdateThread(
  threadId: string,
  successMessage?: (archived: boolean | null) => string | null,
) {
  const queryClient = useQueryClient();
  const toast = useToast();
  return useMutation({
    mutationFn: (body: { title: string | null; archived: boolean | null }) =>
      call(() => api.PATCH('/api/threads/{id}', { params: { path: { id: threadId } }, body })),
    onSuccess: (_, body) => {
      void queryClient.invalidateQueries({ queryKey: keys.thread(threadId) });
      void queryClient.invalidateQueries({ queryKey: keys.threads });
      const message = successMessage?.(body.archived);
      if (message) toast(message, 'success');
    },
    onError: (error) => toast(errorMessage(error), 'error'),
  });
}
