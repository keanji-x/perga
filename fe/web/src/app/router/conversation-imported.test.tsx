import { cleanup, screen, render } from '@testing-library/react';
import { afterEach, expect, it } from 'vitest';
import { importedItems } from './conversation-imported-fixture.tsx';
import { buildConversationTranscript } from '../../../../core/domain/conversation-display.ts';

import { ChatThread } from '../../features/chat/thread/public.tsx';

afterEach(()=>{cleanup();document.getElementById('root')?.remove();});
it.each(['failed','interrupted','completed'])('u2_imported_outcome_is_history_without_current_controls_%s',status=>{
  const items=importedItems().filter(item=>item.source.kind !== 'entry' || item.source.entry.author !== 'turn'
    || item.source.entry.turnId === `old-${status}`);
  render(<ChatThread conversation={{id:'daily-planner',trackId:'daily',provider:'codex',kind:'codex',title:null,state:'idle',updatedAt:1}}
    turns={buildConversationTranscript(items,'jian')} cards={{}} hasPendingMessages={false} stalled={false} canContinue
    regenerateMessage={()=>Promise.reject(new Error('historical action'))} editMessage={()=>{throw new Error('historical action');}} />);
  expect(screen.getByText(`Imported history · ${status[0].toUpperCase()+status.slice(1)}`,{exact:true})).toBeTruthy();
  expect(screen.queryByLabelText('Current response status')).toBeNull();
  expect(screen.queryByRole('button',{name:'Edit message'})).toBeNull();
  expect(screen.queryByRole('button',{name:'重新生成'})).toBeNull();
  expect(screen.queryByText('Send a message to continue.')).toBeNull();
  expect(screen.getByText(/Imported history · System/)).toBeTruthy();
  expect(screen.getByText('Imported history · Ran',{exact:true})).toBeTruthy();
});
