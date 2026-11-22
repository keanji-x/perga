import { onlineManager, useMutation, type UseMutationOptions, type UseMutationResult } from '@tanstack/react-query';
import { NotSentError } from '../../../../core/domain/failure-class.ts';

export function useOnlineMutation<TData, TError = Error, TVariables = void, TContext = unknown>(
  options: Omit<UseMutationOptions<TData, TError, TVariables, TContext>, 'mutationFn' | 'networkMode' | 'retry'> &
    Required<Pick<UseMutationOptions<TData, TError, TVariables, TContext>, 'mutationFn'>> & {
      /** Local intent resources are held until the mutation settles. */
      acquireLocal?: (variables: TVariables) => () => void;
    },
): UseMutationResult<TData, TError, TVariables, TContext> {
  const { acquireLocal, mutationFn, ...queryOptions } = options;
  const mutation = useMutation<TData, TError, TVariables, TContext>({
    ...queryOptions,
    // Reject an offline write at dispatch so it cannot replay on reconnect.
    networkMode: 'always',
    retry: false,
    mutationFn: (variables, context) => {
      if (!onlineManager.isOnline()) throw new NotSentError();
      return mutationFn(variables, context);
    },
  });
  const mutateAsync: UseMutationResult<TData, TError, TVariables, TContext>['mutateAsync'] = async (variables, callbacks) => {
    const release = acquireLocal?.(variables);
    try {
      return await mutation.mutateAsync(variables, callbacks);
    } finally {
      release?.();
    }
  };
  return { ...mutation, mutateAsync, mutate: (variables, callbacks) => { void mutateAsync(variables, callbacks).catch(() => undefined); } };
}
