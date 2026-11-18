using System.Text;

namespace Perga.AgentKit.Tests;

public sealed class MockCliFixture : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public string RootDir { get; }
    public string WorkspaceDir { get; }
    public string MockCodexPath { get; }
    public string MockClaudePath { get; }
    public string MockAcpPath { get; }
    public string MockBwrapPath { get; }
    public string BwrapCapturedArgsPath { get; }
    public string MockSlowVersionPath { get; }
    public string MockFailingVersionPath { get; }

    public MockCliFixture()
    {
        RootDir = Path.Combine(Path.GetTempPath(), $"perga-agentkit-tests-{Guid.NewGuid():N}");
        WorkspaceDir = Path.Combine(RootDir, "workspace");
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(WorkspaceDir);

        MockCodexPath = WriteExecutable("mock_codex.py", CodexMockPythonScript);
        MockClaudePath = WriteExecutable("mock_claude.py", ClaudeMockPythonScript);
        MockAcpPath = WriteExecutable("mock_acp.py", AcpMockPythonScript);
        BwrapCapturedArgsPath = Path.Combine(RootDir, "bwrap_captured_args.json");
        MockBwrapPath = WriteExecutable("mock_bwrap.py", BwrapPassthroughPythonScript);
        MockSlowVersionPath = WriteExecutable("mock_slow.py", SlowVersionPythonScript);
        MockFailingVersionPath = WriteExecutable("mock_fail.py", FailingVersionPythonScript);
    }

    public static IReadOnlyDictionary<string, McpServerConfig> CreateSampleMcpServers() =>
        new Dictionary<string, McpServerConfig>(StringComparer.Ordinal)
        {
            ["perga"] = new McpServerConfig.HttpMcpServer(
                "http://127.0.0.1:43111/mcp/kernel",
                new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer pg_test_token_123"
                }),
            ["local-tools"] = new McpServerConfig.StdioMcpServer(
                "/usr/bin/env",
                ["python3", "-m", "mcp_local"],
                new Dictionary<string, string>
                {
                    ["MCP_MODE"] = "test"
                })
        };

    public string WriteExecutable(string fileName, string content)
    {
        var fullPath = Path.Combine(RootDir, fileName);
        File.WriteAllText(fullPath, content.Replace("\r\n", "\n", StringComparison.Ordinal), Utf8NoBom);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                fullPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDir))
            {
                Directory.Delete(RootDir, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort temp directory cleanup failure.
        }
    }

    private const string CodexMockPythonScript = """
#!/usr/bin/env python3
import json
import os
import sys
import threading
import time

if "--version" in sys.argv:
    sys.stdout.write("codex-cli 0.99.0-mock\n")
    sys.stdout.flush()
    sys.exit(0)

if "app-server" not in sys.argv:
    sys.stderr.write(f"Expected 'app-server' subcommand, got: {sys.argv}\n")
    sys.stderr.flush()
    sys.exit(2)

if os.getenv("PERGA_TEST_CODEX_REQUIRE_PRIVATE_STATE") == "1":
    with open(os.path.join(os.environ["CODEX_HOME"], "mock-state"), "w") as output:
        output.write("started")

write_lock = threading.Lock()
state = {
    "thread_id": None,
    "thread_method": None,
    "model": None,
    "cwd": None,
    "base_instructions": None,
    "mcp_servers": {},
    "turn_counter": 0,
    "active_turn_id": None,
    "approval_event": threading.Event(),
    "approval_result": None,
    "steer_event": threading.Event(),
    "steer_text": None,
    "interrupt_event": threading.Event(),
}

def send(obj):
    with write_lock:
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

def run_turn(turn_id, prompt, correlation_id, turn_model=None):
    effective_model = turn_model if turn_model is not None else state["model"]
    send({
        "jsonrpc": "2.0",
        "method": "turn/started",
        "params": {"threadId": state["thread_id"], "turn": {"id": turn_id, "status": "inProgress"}}
    })

    stale_default = os.environ.get("MOCK_CODEX_STALE_DEFAULT_MODEL")
    if stale_default and not effective_model:
        nested_err = json.dumps({
            "type": "error",
            "status": 400,
            "error": {
                "type": "invalid_request_error",
                "message": f"The '{stale_default}' model is not supported when using Codex with a ChatGPT account.",
                "code": "model_not_found"
            }
        })
        send({
            "jsonrpc": "2.0",
            "method": "error",
            "params": {
                "threadId": state["thread_id"],
                "turnId": turn_id,
                "willRetry": False,
                "error": {"message": nested_err}
            }
        })
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {
                    "id": turn_id,
                    "status": "failed",
                    "error": {"message": nested_err}
                }
            }
        })
        return

    if os.environ.get("MOCK_CODEX_REJECT_ALL_MODELS") == "1":
        nested_err = json.dumps({
            "type": "error",
            "status": 400,
            "error": {
                "type": "invalid_request_error",
                "message": f"The '{effective_model or 'gpt-6.1-sol'}' model is not supported when using Codex with a ChatGPT account.",
                "code": "model_not_found"
            }
        })
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {
                    "id": turn_id,
                    "status": "failed",
                    "error": {"message": nested_err}
                }
            }
        })
        return

    if "TRIGGER_ERROR" in prompt:
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {
                    "id": turn_id,
                    "status": "failed",
                    "error": {
                        "code": "rate_limit_exceeded",
                        "message": "Mock Codex quota exceeded",
                        "isRetryable": True
                    }
                }
            }
        })
        return

    if "WAIT_FOR_INTERRUPT" in prompt:
        send({
            "jsonrpc": "2.0",
            "method": "item/agentMessage/delta",
            "params": {"delta": "working before interrupt..."}
        })
        state["interrupt_event"].wait(timeout=10.0)
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {"id": turn_id, "status": "interrupted"}
            }
        })
        return

    if "WAIT_FOR_STEER" in prompt:
        send({
            "jsonrpc": "2.0",
            "method": "item/agentMessage/delta",
            "params": {"delta": "initial-plan;"}
        })
        state["steer_event"].wait(timeout=10.0)
        steered = state["steer_text"] or "none"
        send({
            "jsonrpc": "2.0",
            "method": "item/agentMessage/delta",
            "params": {"delta": f"steered:{steered}"}
        })
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {"id": turn_id, "status": "completed"},
                "usage": {"inputTokens": 50, "outputTokens": 25, "cachedInputTokens": 10, "contextWindowTokens": 200000}
            }
        })
        return

    if "REQUIRE_APPROVAL" in prompt:
        state["approval_event"].clear()
        state["approval_result"] = None
        req_id = 701 if "NUMERIC_ID" in prompt else "codex_req_1"
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "method": "item/commandExecution/requestApproval",
            "params": {
                "itemId": "cmd_1",
                "toolName": "commandExecution",
                "arguments": {"command": "git status"}
            }
        })
        state["approval_event"].wait(timeout=10.0)
        res = state["approval_result"] or {}
        decision = res.get("decision", "unknown")
        reason = res.get("reason") or ""
        is_declined = decision != "accept"
        send({
            "jsonrpc": "2.0",
            "method": "item/completed",
            "params": {
                "item": {
                    "id": "cmd_1",
                    "type": "commandExecution",
                    "status": "declined" if is_declined else "completed",
                    "aggregatedOutput": f"decision={decision};reason={reason}"
                }
            }
        })
        send({
            "jsonrpc": "2.0",
            "method": "item/agentMessage/delta",
            "params": {"delta": f"approval-outcome:{decision}"}
        })
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {"id": turn_id, "status": "completed"},
                "usage": {"inputTokens": 40, "outputTokens": 20, "cachedInputTokens": 5, "contextWindowTokens": 128000}
            }
        })
        return

    send({
        "jsonrpc": "2.0",
        "method": "item/reasoning/summaryTextDelta",
        "params": {"delta": f"thinking({correlation_id})"}
    })
    send({
        "jsonrpc": "2.0",
        "method": "item/started",
        "params": {
            "item": {
                "id": f"call_{turn_id}",
                "type": "mcpToolCall",
                "tool": "perga_report_read",
                "arguments": {"whole": True}
            }
        }
    })
    send({
        "jsonrpc": "2.0",
        "method": "item/completed",
        "params": {
            "item": {
                "id": f"call_{turn_id}",
                "type": "mcpToolCall",
                "status": "completed",
                "output": "report-ok"
            }
        }
    })
    mcp_names = ",".join(sorted(state["mcp_servers"].keys()))
    summary = f"codex[{state['thread_method']}|{state['thread_id']}|mcp={mcp_names}|model={effective_model}]:{prompt}"
    send({
        "jsonrpc": "2.0",
        "method": "item/agentMessage/delta",
        "params": {"delta": summary}
    })
    send({
        "jsonrpc": "2.0",
        "method": "thread/tokenUsage/updated",
        "params": {
            "tokenUsage": {
                "modelContextWindow": 272000,
                "last": {"inputTokens": 120, "outputTokens": 45, "cachedInputTokens": 30}
            }
        }
    })
    send({
        "jsonrpc": "2.0",
        "method": "turn/completed",
        "params": {
            "threadId": state["thread_id"],
            "turn": {"id": turn_id, "status": "completed"}
        }
    })

for raw_line in sys.stdin:
    line = raw_line.strip()
    if not line:
        continue
    msg = json.loads(line)
    method = msg.get("method")
    msg_id = msg.get("id")

    if method is None and msg_id is not None:
        state["approval_result"] = msg.get("result") or {}
        state["approval_event"].set()
        continue

    params = msg.get("params") or {}
    if method == "initialize":
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"userAgent": "codex-mock/0.99.0"}})
    elif method == "initialized":
        pass
    elif method == "account/login/start" and params.get("type") == "apiKey":
        with open(os.path.join(os.environ["CODEX_HOME"], "mock-api-login"), "w") as output:
            output.write("configured")
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"type": "apiKey"}})
    elif method == "model/list":
        custom_models = os.environ.get("MOCK_CODEX_MODELS_JSON")
        if custom_models:
            send({"jsonrpc": "2.0", "id": msg_id, "result": json.loads(custom_models)})
        else:
            send({
                "jsonrpc": "2.0",
                "id": msg_id,
                "result": {
                    "data": [
                        {"id": "gpt-6.2-sol", "model": "gpt-6.2-sol", "isDefault": True},
                        {"id": "gpt-5.4", "model": "gpt-5.4", "isDefault": False},
                        {"id": "gpt-6.1-sol", "model": "gpt-6.1-sol", "isDefault": False}
                    ]
                }
            })
    elif method in ("thread/start", "thread/resume"):
        state["thread_method"] = method
        state["model"] = params.get("model")
        state["cwd"] = params.get("cwd")
        state["base_instructions"] = params.get("baseInstructions")
        state["mcp_servers"] = params.get("mcpServers") or params.get("config", {}).get("mcp_servers") or {}
        if method == "thread/resume":
            state["thread_id"] = params.get("threadId") or "thr_resumed_default"
        else:
            state["thread_id"] = "thr_mock_001"
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"thread": {"id": state["thread_id"]}}})
    elif method == "turn/start":
        state["turn_counter"] += 1
        turn_id = f"turn_{state['turn_counter']}"
        state["active_turn_id"] = turn_id
        state["steer_event"].clear()
        state["steer_text"] = None
        state["interrupt_event"].clear()
        inputs = params.get("input") or []
        prompt_text = inputs[0].get("text", "") if inputs else ""
        corr_id = params.get("correlationId", "")
        turn_model = params.get("model")
        if prompt_text == "FAIL_BEFORE_START_REPLY" and turn_model is None:
            failed = {"id": turn_id, "status": "failed",
                      "error": {"code": "model_not_found", "message": "Model not found"}}
            send({"jsonrpc": "2.0", "method": "turn/completed", "params": {
                "threadId": state["thread_id"], "turn": failed}})
            send({"jsonrpc": "2.0", "id": msg_id, "result": {"turn": failed}})
            continue
        if prompt_text == "COMPLETE_BEFORE_START_REPLY":
            for completed_id, status in (("wrong_turn", "interrupted"), (turn_id, "completed")):
                send({"jsonrpc": "2.0", "method": "turn/completed", "params": {
                    "threadId": state["thread_id"], "turn": {"id": completed_id, "status": status}}})
            send({"jsonrpc": "2.0", "id": msg_id, "result": {"turn": {"id": turn_id, "status": "inProgress"}}})
            continue
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"turn": {"id": turn_id, "status": "inProgress"}}})
        t = threading.Thread(target=run_turn, args=(turn_id, prompt_text, corr_id, turn_model), daemon=True)
        t.start()
    elif method == "turn/steer":
        inputs = params.get("input") or []
        state["steer_text"] = inputs[0].get("text", "") if inputs else ""
        state["steer_event"].set()
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"turnId": state["active_turn_id"]}})
    elif method == "turn/interrupt":
        state["interrupt_event"].set()
        send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
    elif msg_id is not None:
        send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": f"Method not found: {method}"}})
""";

    private const string ClaudeMockPythonScript = """
#!/usr/bin/env python3
import argparse
import json
import os
import signal
import sys
import threading

if "--version" in sys.argv:
    sys.stdout.write("2.1.0 (Claude Code Mock)\n")
    sys.stdout.flush()
    sys.exit(0)

parser = argparse.ArgumentParser()
parser.add_argument("--print", dest="is_print", action="store_true")
parser.add_argument("--verbose", action="store_true")
parser.add_argument("--input-format")
parser.add_argument("--output-format")
parser.add_argument("--mcp-config")
parser.add_argument("--resume", default=None)
parser.add_argument("--model", default=None)
parser.add_argument("--append-system-prompt", dest="system_prompt", default=None)
args = parser.parse_args()

if not args.is_print or not args.verbose or args.input_format != "stream-json" or args.output_format != "stream-json":
    sys.stderr.write(f"Invalid required Claude flags: {sys.argv}\n")
    sys.stderr.flush()
    sys.exit(2)

if not args.mcp_config or not os.path.isfile(args.mcp_config):
    sys.stderr.write(f"Missing --mcp-config file: {args.mcp_config}\n")
    sys.stderr.flush()
    sys.exit(3)

with open(args.mcp_config, "r", encoding="utf-8") as f:
    mcp_payload = json.load(f)

mcp_servers = mcp_payload.get("mcpServers") or {}
session_id = args.resume or "claude_sess_mock_42"

write_lock = threading.Lock()
sigint_event = threading.Event()
approval_event = threading.Event()
approval_payload = {}
turn_index = 0

def on_sigint(signum, frame):
    sigint_event.set()

signal.signal(signal.SIGINT, on_sigint)

def emit(obj):
    with write_lock:
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

def process_user_turn(user_msg):
    global turn_index
    turn_index += 1
    content = (user_msg.get("message") or {}).get("content", "")

    if turn_index == 1:
        emit({"type": "system", "subtype": "init", "session_id": session_id})

    if "TRIGGER_ERROR" in content:
        emit({
            "type": "result",
            "subtype": "error_max_turns",
            "is_error": True,
            "session_id": session_id,
            "error": "Maximum turns reached in mock Claude"
        })
        return

    if "WAIT_FOR_SIGINT" in content:
        sigint_event.clear()
        emit({
            "type": "stream_event",
            "session_id": session_id,
            "event": {
                "type": "content_block_delta",
                "index": 0,
                "delta": {"type": "text_delta", "text": "claude-streaming-before-sigint..."}
            }
        })
        sigint_event.wait(timeout=10.0)
        emit({
            "type": "result",
            "subtype": "error_during_execution",
            "is_error": True,
            "session_id": session_id,
            "error": "Interrupted by SIGINT"
        })
        return

    if "REQUIRE_APPROVAL" in content:
        approval_event.clear()
        emit({
            "type": "control_request",
            "request_id": "claude_perm_1",
            "session_id": session_id,
            "request": {
                "subtype": "can_use_tool",
                "tool_name": "Bash",
                "input": {"command": "echo approved"}
            }
        })
        approval_event.wait(timeout=10.0)
        resp_body = ((approval_payload.get("response") or {}).get("response") or {})
        behavior = resp_body.get("behavior", "unknown")
        has_updated_input = "updatedInput" in resp_body
        deny_msg = resp_body.get("message", "")
        emit({
            "type": "stream_event",
            "session_id": session_id,
            "event": {
                "type": "content_block_delta",
                "index": 0,
                "delta": {
                    "type": "text_delta",
                    "text": f"claude-approval:{behavior}|updatedInput={has_updated_input}|msg={deny_msg}"
                }
            }
        })
        emit({
            "type": "result",
            "subtype": "success",
            "is_error": False,
            "session_id": session_id,
            "usage": {"input_tokens": 80, "output_tokens": 30, "cache_read_input_tokens": 12}
        })
        return

    emit({
        "type": "stream_event",
        "session_id": session_id,
        "event": {
            "type": "content_block_delta",
            "index": 0,
            "delta": {"type": "thinking_delta", "thinking": f"claude-think-{turn_index}"}
        }
    })
    mcp_keys = ",".join(sorted(mcp_servers.keys()))
    full_text = f"claude[{session_id}|model={args.model}|sys={args.system_prompt}|mcp={mcp_keys}]:{content}"
    emit({
        "type": "stream_event",
        "session_id": session_id,
        "event": {
            "type": "content_block_delta",
            "index": 1,
            "delta": {"type": "text_delta", "text": full_text}
        }
    })
    emit({
        "type": "assistant",
        "session_id": session_id,
        "message": {
            "role": "assistant",
            "content": [
                {"type": "thinking", "thinking": f"claude-think-{turn_index}"},
                {"type": "text", "text": full_text},
                {"type": "tool_use", "id": f"toolu_{turn_index}", "name": "perga_task_ls", "input": {"readyOnly": True}}
            ]
        }
    })
    emit({
        "type": "user",
        "session_id": session_id,
        "message": {
            "role": "user",
            "content": [
                {
                    "type": "tool_result",
                    "tool_use_id": f"toolu_{turn_index}",
                    "content": [{"type": "text", "text": "tasks-listed-ok"}],
                    "is_error": False
                }
            ]
        }
    })
    emit({
        "type": "result",
        "subtype": "success",
        "is_error": False,
        "session_id": session_id,
        "usage": {
            "input_tokens": 150,
            "output_tokens": 60,
            "cache_read_input_tokens": 25
        },
        "modelUsage": {
            "claude-sonnet-4-6": {"contextWindow": 200000}
        }
    })

while True:
    try:
        raw_line = sys.stdin.readline()
    except InterruptedError:
        continue
    if not raw_line:
        break
    line = raw_line.strip()
    if not line:
        continue
    frame = json.loads(line)
    ftype = frame.get("type")
    if ftype == "control_response":
        approval_payload.clear()
        approval_payload.update(frame)
        approval_event.set()
    elif ftype == "user":
        t = threading.Thread(target=process_user_turn, args=(frame,), daemon=True)
        t.start()
""";

    private const string AcpMockPythonScript = """
#!/usr/bin/env python3
import json
import os
import sys
import threading

if "--version" in sys.argv:
    sys.stdout.write("acp-mock 1.4.2\n")
    sys.stdout.flush()
    sys.exit(0)

write_lock = threading.Lock()
state = {
    "session_id": None,
    "session_method": None,
    "mcp_servers": [],
    "model": None,
    "model_set_method": None,
    "system_prompt": None,
    "subcommand_acp": "acp" in sys.argv[1:],
    "cancel_event": threading.Event(),
    "perm_event": threading.Event(),
    "perm_result": None,
    "turn_idx": 0,
}

def send(obj):
    with write_lock:
        sys.stdout.write(json.dumps(obj) + "\n")
        sys.stdout.flush()

def handle_prompt(req_id, params):
    state["turn_idx"] += 1
    t_idx = state["turn_idx"]
    prompt_blocks = params.get("prompt") or []
    prompt_text = prompt_blocks[0].get("text", "") if prompt_blocks else ""

    if "TRIGGER_RPC_ERROR" in prompt_text:
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "error": {"code": 429, "message": "ACP rate limit hit"}
        })
        return

    if "WAIT_FOR_CANCEL" in prompt_text:
        send({
            "jsonrpc": "2.0",
            "method": "session/update",
            "params": {
                "sessionId": state["session_id"],
                "update": {
                    "sessionUpdate": "agent_message_chunk",
                    "content": {"type": "text", "text": "acp-working..."}
                }
            }
        })
        state["cancel_event"].wait(timeout=10.0)
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {"stopReason": "cancelled"}
        })
        return

    if "REQUIRE_PERMISSION" in prompt_text:
        state["perm_event"].clear()
        state["perm_result"] = None
        send({
            "jsonrpc": "2.0",
            "id": "acp_perm_99",
            "method": "session/request_permission",
            "params": {
                "sessionId": state["session_id"],
                "toolCall": {
                    "toolCallId": "tc_perm_1",
                    "name": "write_file",
                    "rawInput": {"path": "README.md"}
                },
                "options": [
                    {"optionId": "opt_allow_once", "kind": "allow_once", "name": "Allow once"},
                    {"optionId": "opt_reject_once", "kind": "reject_once", "name": "Reject once"}
                ]
            }
        })
        state["perm_event"].wait(timeout=10.0)
        res = state["perm_result"] or {}
        selected = (res.get("outcome") or {}).get("optionId", "none")
        send({
            "jsonrpc": "2.0",
            "method": "session/update",
            "params": {
                "sessionId": state["session_id"],
                "update": {
                    "sessionUpdate": "agent_message_chunk",
                    "content": {"type": "text", "text": f"acp-selected:{selected}"}
                }
            }
        })
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "stopReason": "end_turn",
                "usage": {"inputTokens": 60, "outputTokens": 15, "cachedInputTokens": 0, "contextWindowTokens": 1000000}
            }
        })
        return

    send({
        "jsonrpc": "2.0",
        "method": "session/update",
        "params": {
            "sessionId": state["session_id"],
            "update": {
                "sessionUpdate": "agent_thought_chunk",
                "content": {"type": "text", "text": f"acp-thought-{t_idx}"}
            }
        }
    })
    send({
        "jsonrpc": "2.0",
        "method": "session/update",
        "params": {
            "sessionId": state["session_id"],
            "update": {
                "sessionUpdate": "tool_call",
                "toolCallId": f"acp_tc_{t_idx}",
                "name": "vcs_status",
                "status": "in_progress",
                "rawInput": {"cwd": "."}
            }
        }
    })
    send({
        "jsonrpc": "2.0",
        "method": "session/update",
        "params": {
            "sessionId": state["session_id"],
            "update": {
                "sessionUpdate": "tool_call_update",
                "toolCallId": f"acp_tc_{t_idx}",
                "status": "completed",
                "content": [{"type": "content", "content": {"type": "text", "text": "clean-working-copy"}}]
            }
        }
    })
    mcp_names = ",".join(sorted(s.get("name", "") for s in state["mcp_servers"]))
    summary = f"acp[{state['session_method']}|{state['session_id']}|sub={state['subcommand_acp']}|model={state['model']}|via={state['model_set_method']}|mcp={mcp_names}]:{prompt_text}"
    send({
        "jsonrpc": "2.0",
        "method": "session/update",
        "params": {
            "sessionId": state["session_id"],
            "update": {
                "sessionUpdate": "agent_message_chunk",
                "content": {"type": "text", "text": summary}
            }
        }
    })
    send({
        "jsonrpc": "2.0",
        "id": req_id,
        "result": {
            "stopReason": "end_turn",
            "usage": {"inputTokens": 200, "outputTokens": 75, "cachedInputTokens": 40, "contextWindowTokens": 1048576}
        }
    })

for raw_line in sys.stdin:
    line = raw_line.strip()
    if not line:
        continue
    msg = json.loads(line)
    method = msg.get("method")
    msg_id = msg.get("id")

    if method is None and msg_id is not None:
        state["perm_result"] = msg.get("result") or {}
        state["perm_event"].set()
        continue

    params = msg.get("params") or {}
    if method == "initialize":
        mcp_caps = {"http": True} if os.environ.get("MOCK_ACP_HTTP_MCP") == "1" else {}
        send({
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {
                "protocolVersion": 1,
                "agentCapabilities": {
                    "loadSession": os.environ.get("MOCK_ACP_DISABLE_LOAD") != "1",
                    "mcpCapabilities": mcp_caps,
                    "sessionCapabilities": {"resume": {}, "close": {}} if os.environ.get("MOCK_ACP_RESUME") == "1" else {}
                }
            }
        })
    elif method in ("session/new", "session/load", "session/resume"):
        state["session_method"] = method
        state["mcp_servers"] = params.get("mcpServers") or []
        state["model"] = params.get("model")
        state["system_prompt"] = params.get("systemPrompt")
        if method in ("session/load", "session/resume"):
            state["session_id"] = params.get("sessionId")
            if os.environ.get("MOCK_ACP_INVALID_RESUME") == "1":
                send({"jsonrpc": "2.0", "id": msg_id})
            else:
                send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
        else:
            state["session_id"] = "acp_sess_001"
            if os.environ.get("MOCK_ACP_CONFIG_OPTIONS_MODELS") == "1":
                send({
                    "jsonrpc": "2.0",
                    "id": msg_id,
                    "result": {
                        "sessionId": state["session_id"],
                        "configOptions": [
                            {
                                "id": "model",
                                "category": "model",
                                "currentValue": "opencode-sonnet-4-6",
                                "options": [
                                    {"value": "opencode-sonnet-4-6", "name": "Sonnet 4.6"},
                                    {"group": "More", "options": [{"value": "opencode-gpt-5.4", "name": "GPT 5.4"}]}
                                ]
                            }
                        ]
                    }
                })
            else:
                send({
                    "jsonrpc": "2.0",
                    "id": msg_id,
                    "result": {
                        "sessionId": state["session_id"],
                        "models": {
                            "currentModelId": "gemini-4-carbon-medium",
                            "availableModels": [
                                {"modelId": "gemini-4-carbon-medium", "name": "Gemini 4 Carbon Medium"},
                                {"modelId": "gemini-4-carbon-high", "name": "Gemini 4 Carbon High"},
                                {"modelId": "claude-sonnet-5-5-medium", "name": "Claude Sonnet 5.5 Medium"}
                            ]
                        }
                    }
                })
    elif method == "session/close":
        if params.get("sessionId") == state["session_id"]:
            result = {"success": False} if os.environ.get("MOCK_ACP_INVALID_CLOSE") == "1" else {}
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})
        else:
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32602, "message": "wrong session"}})
    elif method == "session/set_model":
        if os.environ.get("MOCK_ACP_NO_SET_MODEL") == "1":
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": "Method not found"}})
        else:
            state["model"] = params.get("modelId")
            state["model_set_method"] = "session/set_model"
            send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
    elif method == "session/set_config_option":
        if params.get("configId") == "model":
            state["model"] = params.get("value")
            state["model_set_method"] = "session/set_config_option"
        send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
    elif method == "session/prompt":
        state["cancel_event"].clear()
        t = threading.Thread(target=handle_prompt, args=(msg_id, params), daemon=True)
        t.start()
    elif method == "session/cancel":
        state["cancel_event"].set()
    elif msg_id is not None:
        send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": f"Method not found: {method}"}})
""";

    private const string BwrapPassthroughPythonScript = """
#!/usr/bin/env python3
import json
import os
import sys

if "--version" in sys.argv:
    sys.stdout.write("bubblewrap 0.8.0\n")
    sys.stdout.flush()
    sys.exit(0)

capture_path = os.environ.get("PERGA_TEST_BWRAP_CAPTURE")
if capture_path:
    with open(capture_path, "w", encoding="utf-8") as f:
        json.dump(sys.argv[1:], f)

sep_idx = sys.argv.index("--")
chdir_val = None
for i in range(1, sep_idx - 1):
    if sys.argv[i] == "--chdir":
        chdir_val = sys.argv[i + 1]

if chdir_val:
    os.chdir(chdir_val)

inner_cmd = sys.argv[sep_idx + 1:]
os.execvp(inner_cmd[0], inner_cmd)
""";

    private const string SlowVersionPythonScript = """
#!/usr/bin/env python3
import time
time.sleep(30)
""";

    private const string FailingVersionPythonScript = """
#!/usr/bin/env python3
import sys
sys.stderr.write("fatal configuration error in mock binary\n")
sys.stderr.flush()
sys.exit(17)
""";
}
