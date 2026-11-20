import readline from "node:readline";
import { OarBridgeServer, type JsonRpcRequest } from "./bridge.ts";

const nodeMajor = Number.parseInt(process.versions.node.split(".")[0] ?? "0", 10);
if (Number.isNaN(nodeMajor) || nodeMajor < 24) {
  process.stderr.write(
    `perga-oar-bridge requires Node.js >= 24.0.0 (detected v${process.versions.node}).\n`,
  );
  process.exit(1);
}

function writeJsonLine(payload: Record<string, unknown>): void {
  process.stdout.write(`${JSON.stringify(payload)}\n`);
}

const server = new OarBridgeServer({ send: writeJsonLine });

const rl = readline.createInterface({
  input: process.stdin,
  crlfDelay: Infinity,
});

const inFlight = new Set<Promise<void>>();

rl.on("line", (line) => {
  const trimmed = line.trim();
  if (!trimmed) {
    return;
  }
  let parsed: JsonRpcRequest;
  try {
    parsed = JSON.parse(trimmed) as JsonRpcRequest;
  } catch {
    return;
  }
  const task = server.handleMessage(parsed).finally(() => {
    inFlight.delete(task);
  });
  inFlight.add(task);
});

let shuttingDown = false;
async function shutdown(exitCode: number): Promise<void> {
  if (shuttingDown) {
    return;
  }
  shuttingDown = true;
  rl.close();
  await Promise.allSettled([...inFlight]);
  await server.dispose();
  process.exit(exitCode);
}

rl.on("close", () => {
  void shutdown(0);
});

process.on("SIGINT", () => {
  void shutdown(0);
});

process.on("SIGTERM", () => {
  void shutdown(0);
});
