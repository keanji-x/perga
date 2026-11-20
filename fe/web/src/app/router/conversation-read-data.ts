export function conversationCardId(path:string):string {
  const match=/^\/api\/(?:v1\/sessions|cards)\/([^/]+)\//.exec(new URL(path,'http://fixture').pathname);
  if (match === null) throw new Error(`Not a conversation fixture request: ${path}`);
  return decodeURIComponent(match[1]);
}
