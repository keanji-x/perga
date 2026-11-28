import type { RowModuleView } from '../../../../../core/view/panel.ts';
import { Icon } from '../../../ui/icon/public.tsx';
import type { EmptyStateSchema } from '../../../ui/empty-state/public.tsx';

/** Content for the inventory's closed set of module kinds. */
export function inventoryEmptyContent(key: RowModuleView['key'], title: string): EmptyStateSchema {
  switch (key) {
    case 'work':
    case 'tasks':
      return { icon: <Icon name="tasks" />, title, description: 'Tasks scheduled in Flow appear here.' };
    case 'temporary':
      return { icon: <Icon name="terminal" />, title, description: 'Temporary terminals appear here.' };
    case 'cards':
      return { icon: <Icon name="tools" />, title, description: 'Opened workspace tools appear here.' };
  }
}
