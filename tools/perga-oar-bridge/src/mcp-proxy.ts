import crypto from "node:crypto";
import http from "node:http";
import https from "node:https";
import type { AddressInfo } from "node:net";
import type { McpServer } from "@botiverse/oar/browser";

export interface RawHttpMcpServer {
  readonly name: string;
  readonly type: "http";
  readonly url: string;
  readonly headers?: Readonly<Record<string, string>>;
}

export interface RawStdioMcpServer {
  readonly name: string;
  readonly type?: "stdio";
  readonly command: string;
  readonly args?: readonly string[];
  readonly env?: Readonly<Record<string, string>>;
}

export type RawMcpServerInput = RawHttpMcpServer | RawStdioMcpServer;

export interface PreparedMcpServers {
  readonly servers: readonly McpServer[];
  readonly close: () => Promise<void>;
}

interface ProxyTarget {
  readonly tokenBuffer: Buffer;
  readonly upstreamUrl: URL;
  readonly headers: Readonly<Record<string, string>>;
}

function findTarget(targets: readonly ProxyTarget[], candidateToken: string): ProxyTarget | null {
  const candidateBuf = Buffer.from(candidateToken, "utf8");
  for (const target of targets) {
    if (
      candidateBuf.length === target.tokenBuffer.length &&
      crypto.timingSafeEqual(candidateBuf, target.tokenBuffer)
    ) {
      return target;
    }
  }
  return null;
}

/**
 * Converts Perga MCP server definitions to OAR McpServer entries.
 * When `stripHttpCredentials` is true (e.g. Antigravity, where OAR rejects
 * `mcpServerCredentials` because agy persists headers to disk), starts an
 * ephemeral 127.0.0.1:0 HTTP proxy gated by a 256-bit random capability path
 * token per server and injects upstream headers in flight.
 */
export async function prepareMcpServers(
  inputs: readonly RawMcpServerInput[] | undefined,
  options: { readonly stripHttpCredentials: boolean },
): Promise<PreparedMcpServers> {
  if (!inputs || inputs.length === 0) {
    return { servers: [], close: async () => {} };
  }

  const targets: ProxyTarget[] = [];
  const pendingEntries: Array<
    | { readonly kind: "direct"; readonly server: McpServer }
    | { readonly kind: "proxied"; readonly name: string; readonly routeToken: string }
  > = [];

  for (const entry of inputs) {
    if (entry.type === "http") {
      const headers = entry.headers && Object.keys(entry.headers).length > 0 ? entry.headers : undefined;
      if (options.stripHttpCredentials && headers) {
        const routeToken = crypto.randomBytes(32).toString("base64url");
        targets.push({
          tokenBuffer: Buffer.from(routeToken, "utf8"),
          upstreamUrl: new URL(entry.url),
          headers,
        });
        pendingEntries.push({ kind: "proxied", name: entry.name, routeToken });
      } else {
        pendingEntries.push({
          kind: "direct",
          server: headers
            ? { name: entry.name, type: "http", url: entry.url, headers }
            : { name: entry.name, type: "http", url: entry.url },
        });
      }
    } else {
      const args = entry.args && entry.args.length > 0 ? entry.args : undefined;
      const env = entry.env && Object.keys(entry.env).length > 0 ? entry.env : undefined;
      pendingEntries.push({
        kind: "direct",
        server: {
          name: entry.name,
          command: entry.command,
          ...(args ? { args } : {}),
          ...(!options.stripHttpCredentials && env ? { env } : {}),
        },
      });
    }
  }

  if (targets.length === 0) {
    return {
      servers: pendingEntries.map((item) => (item as { readonly server: McpServer }).server),
      close: async () => {},
    };
  }

  const activeCleanups = new Set<() => void>();

  const server = http.createServer((req, res) => {
    const reqUrl = req.url ?? "/";
    const match = /^\/mcp-proxy\/([^/?]+)(\/[^?]*)?(\?.*)?$/u.exec(reqUrl);
    if (!match) {
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("Not found");
      return;
    }

    const target = findTarget(targets, match[1]!);
    if (!target) {
      res.writeHead(404, { "content-type": "text/plain" });
      res.end("Not found");
      return;
    }

    const suffixPath = match[2] ?? "";
    const suffixQuery = match[3] ?? "";
    const upstream = target.upstreamUrl;
    const basePath = upstream.pathname.endsWith("/") && suffixPath.startsWith("/")
      ? upstream.pathname.slice(0, -1)
      : upstream.pathname;
    const query = suffixQuery
      ? (upstream.search ? `${upstream.search}&${suffixQuery.slice(1)}` : suffixQuery)
      : upstream.search;

    const forwardedHeaders: http.OutgoingHttpHeaders = {};
    for (const [k, v] of Object.entries(req.headers)) {
      if (k.toLowerCase() === "host" || k.toLowerCase() === "connection") {
        continue;
      }
      if (v !== undefined) {
        forwardedHeaders[k] = v;
      }
    }
    for (const [k, v] of Object.entries(target.headers)) {
      forwardedHeaders[k] = v;
    }
    forwardedHeaders.host = upstream.host;

    let upstreamResRef: http.IncomingMessage | null = null;
    let tornDown = false;

    const transport = upstream.protocol === "https:" ? https : http;
    const upstreamReq = transport.request(
      {
        protocol: upstream.protocol,
        hostname: upstream.hostname,
        port: upstream.port || (upstream.protocol === "https:" ? 443 : 80),
        path: `${basePath}${suffixPath}${query}`,
        method: req.method ?? "POST",
        headers: forwardedHeaders,
        agent: false,
      },
      (upstreamRes) => {
        upstreamResRef = upstreamRes;
        if (tornDown) {
          upstreamRes.destroy();
          return;
        }
        res.writeHead(upstreamRes.statusCode ?? 502, upstreamRes.headers);
        upstreamRes.on("error", () => {
          teardown();
          if (!res.destroyed) {
            res.destroy();
          }
        });
        upstreamRes.on("end", () => {
          activeCleanups.delete(teardown);
        });
        upstreamRes.pipe(res);
      },
    );

    const teardown = (): void => {
      if (tornDown) {
        return;
      }
      tornDown = true;
      activeCleanups.delete(teardown);
      if (upstreamResRef && !upstreamResRef.destroyed) {
        upstreamResRef.destroy();
      }
      if (!upstreamReq.destroyed) {
        upstreamReq.destroy();
      }
    };

    activeCleanups.add(teardown);

    upstreamReq.on("error", (err) => {
      teardown();
      if (!res.headersSent && !res.destroyed) {
        res.writeHead(502, { "content-type": "text/plain" });
        res.end(`MCP proxy upstream error: ${err.message}`);
      } else if (!res.destroyed) {
        res.destroy();
      }
    });

    req.on("aborted", teardown);
    res.on("close", () => {
      if (!res.writableEnded) {
        teardown();
      } else {
        activeCleanups.delete(teardown);
      }
    });

    req.pipe(upstreamReq);
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      server.removeListener("error", reject);
      resolve();
    });
  });

  const address = server.address() as AddressInfo | null;
  if (!address || typeof address.port !== "number") {
    server.close();
    throw new Error("Failed to bind ephemeral loopback port for MCP header proxy.");
  }

  const port = address.port;
  const servers: McpServer[] = pendingEntries.map((item) => {
    if (item.kind === "direct") {
      return item.server;
    }
    return {
      name: item.name,
      type: "http",
      url: `http://127.0.0.1:${port}/mcp-proxy/${item.routeToken}`,
    };
  });

  let closed = false;
  const close = async (): Promise<void> => {
    if (closed) {
      return;
    }
    closed = true;
    for (const cleanup of [...activeCleanups]) {
      cleanup();
    }
    activeCleanups.clear();
    server.closeAllConnections?.();
    await new Promise<void>((resolve) => {
      server.close(() => resolve());
    });
  };

  return { servers, close };
}
