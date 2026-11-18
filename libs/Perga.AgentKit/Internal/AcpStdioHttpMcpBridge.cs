using System.Text.Json;

namespace Perga.AgentKit.Internal;

internal static class AcpStdioHttpMcpBridge
{
    internal const string BridgeScript = """
const http = require('node:http');
const https = require('node:https');
const readline = require('node:readline');

const rawUrl = process.env.PERGA_MCP_URL;
if (!rawUrl) {
  process.stderr.write('Missing PERGA_MCP_URL\n');
  process.exit(1);
}
const targetUrl = new URL(rawUrl);
let extraHeaders = {};
try {
  extraHeaders = JSON.parse(process.env.PERGA_MCP_HEADERS || '{}') || {};
} catch {
  extraHeaders = {};
}
const transport = targetUrl.protocol === 'https:' ? https : http;
let mcpSessionId = null;

function writeJsonLine(str) {
  process.stdout.write(str + '\n');
}

function sendRpcError(id, code, message) {
  if (id === undefined || id === null) return;
  writeJsonLine(JSON.stringify({ jsonrpc: '2.0', id, error: { code, message } }));
}

function emitSsePayloads(rawText) {
  const normalized = rawText.replace(/\r\n/g, '\n');
  const blocks = normalized.split('\n\n');
  for (const block of blocks) {
    if (!block.trim()) continue;
    const dataLines = [];
    for (const line of block.split('\n')) {
      if (line.startsWith('data:')) {
        dataLines.push(line.slice(5).trimStart());
      }
    }
    if (dataLines.length === 0) continue;
    const joined = dataLines.join('\n').trim();
    if (!joined) continue;
    try {
      const parsed = JSON.parse(joined);
      writeJsonLine(JSON.stringify(parsed));
    } catch {
      // Ignore keepalive comments or non-JSON frames.
    }
  }
}

function postJsonRpc(rawLine) {
  return new Promise((resolve) => {
    let parsedReq;
    try {
      parsedReq = JSON.parse(rawLine);
    } catch {
      resolve();
      return;
    }
    const reqId = parsedReq && typeof parsedReq === 'object' && !Array.isArray(parsedReq) ? parsedReq.id : undefined;
    const bodyBuf = Buffer.from(rawLine, 'utf8');
    const headers = Object.assign({}, extraHeaders, {
      'Content-Type': 'application/json',
      'Accept': 'application/json, text/event-stream',
      'Content-Length': bodyBuf.length
    });
    if (mcpSessionId) {
      headers['Mcp-Session-Id'] = mcpSessionId;
    }
    const req = transport.request({
      protocol: targetUrl.protocol,
      hostname: targetUrl.hostname,
      port: targetUrl.port || (targetUrl.protocol === 'https:' ? 443 : 80),
      path: targetUrl.pathname + targetUrl.search,
      method: 'POST',
      headers,
      agent: false
    }, (res) => {
      const sid = res.headers['mcp-session-id'];
      if (typeof sid === 'string' && sid.trim()) {
        mcpSessionId = sid.trim();
      } else if (Array.isArray(sid) && sid[0]) {
        mcpSessionId = sid[0].trim();
      }
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const rawBody = Buffer.concat(chunks).toString('utf8');
        const status = res.statusCode || 0;
        if (status === 202 || status === 204 || !rawBody.trim()) {
          if (status >= 400 && reqId !== undefined && reqId !== null) {
            sendRpcError(reqId, -32000, `MCP HTTP ${status}`);
          }
          resolve();
          return;
        }
        const ct = String(res.headers['content-type'] || '').toLowerCase();
        if (ct.includes('text/event-stream')) {
          emitSsePayloads(rawBody);
          resolve();
          return;
        }
        try {
          const parsedResp = JSON.parse(rawBody);
          writeJsonLine(JSON.stringify(parsedResp));
        } catch {
          if (reqId !== undefined && reqId !== null) {
            sendRpcError(reqId, -32000, `MCP HTTP ${status}: ${rawBody.slice(0, 200)}`);
          }
        }
        resolve();
      });
    });
    req.on('error', (err) => {
      sendRpcError(reqId, -32000, `MCP HTTP transport error: ${err.message}`);
      resolve();
    });
    req.write(bodyBuf);
    req.end();
  });
}

let chain = Promise.resolve();
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
rl.on('line', (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;
  chain = chain.then(() => postJsonRpc(trimmed));
});
rl.on('close', () => {
  chain.finally(() => process.exit(0));
});
""";

    public static bool SupportsHttpMcp(JsonElement initResult)
    {
        if (initResult.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!initResult.TryGetProperty("agentCapabilities", out var agentCaps) ||
            agentCaps.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!agentCaps.TryGetProperty("mcpCapabilities", out var mcpCaps) ||
            mcpCaps.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return mcpCaps.TryGetProperty("http", out var httpCap) &&
               httpCap.ValueKind == JsonValueKind.True;
    }

    public static string ResolveNodeExecutable(IReadOnlyDictionary<string, string>? env)
    {
        var fromPath = AgentRuntime.FindExecutableOnPath("node", env);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        string? home = null;
        if (env is not null)
        {
            env.TryGetValue("HOME", out home);
        }

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var nvmVersionsDir = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvmVersionsDir))
            {
                try
                {
                    var candidates = Directory.GetDirectories(nvmVersionsDir)
                        .OrderByDescending(static d => d, StringComparer.OrdinalIgnoreCase);
                    foreach (var versionDir in candidates)
                    {
                        var nodeBin = Path.Combine(versionDir, "bin", OperatingSystem.IsWindows() ? "node.exe" : "node");
                        if (File.Exists(nodeBin))
                        {
                            return nodeBin;
                        }
                    }
                }
                catch
                {
                    // Ignore inaccessible nvm directory.
                }
            }
        }

        foreach (var stdPath in new[] { "/usr/local/bin/node", "/usr/bin/node" })
        {
            if (File.Exists(stdPath))
            {
                return stdPath;
            }
        }

        return "node";
    }
}
