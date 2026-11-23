import { MESSAGES } from '../../../../../core/domain/messages.ts';
import type { RowModuleView } from '../../../../../core/view/panel.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import type { EmptyStateSchema } from '../../../ui/empty-state/public.tsx';

/** Content for the inventory's closed set of module kinds. */
export function inventoryEmptyContent(key: RowModuleView['key'], title: string): EmptyStateSchema {
  switch (key) {
    case 'work':
    case 'tasks':
      return { icon: <Icon name="tasks" />, title, description: MESSAGES.track.noTasksDescription };
    case 'temporary':
      return { icon: <Icon name="terminal" />, title, description: 'Temporary terminals appear here.' };
    case 'cards':
      return { icon: <Icon name="tools" />, title, description: MESSAGES.track.noToolsDescription };
  }
}
