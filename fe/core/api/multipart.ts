/** The File fields used by multipart requests in browsers and Node. */
export type MultipartFile = Readonly<{
  name: string;
  size: number;
  type: string;
  arrayBuffer(): Promise<ArrayBuffer>;
}>;

/** Use the shared Web Standard constructor without adding DOM or Node globals to core. */
declare const FormData: new () => {
  append(name: string, value: string | MultipartFile): void;
};

export function isMultipartFormData(body: unknown): boolean {
  return typeof FormData !== 'undefined' && body instanceof FormData;
}

export function imageMessageBody(payload: object, attachments: readonly MultipartFile[]): object {
  if (attachments.length === 0) return payload;
  const body = new FormData();
  body.append('payload', JSON.stringify(payload));
  for (const file of attachments) body.append('attachments', file);
  return body;
}
