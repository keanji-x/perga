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
if os.environ.get("MOCK_CLAUDE_NO_EFFORT") != "1":
    parser.add_argument("--effort", choices=["low", "medium", "high", "xhigh", "max"], default=None)
parser.add_argument("--permission-mode", default=None)
parser.add_argument("--append-system-prompt", dest="system_prompt", default=None)
args = parser.parse_args()
if os.environ.get("MOCK_CLAUDE_ARGS_LOG"):
    with open(os.environ["MOCK_CLAUDE_ARGS_LOG"], "w") as log:
        json.dump(sys.argv[1:], log)

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
        emit({"type": "system", "subtype": "init", "session_id": session_id, "slash_commands": ["compact", "clear"]})

    if content == "/compact":
        emit({
            "type": "assistant",
            "session_id": session_id,
            "message": {"role": "assistant", "content": [{"type": "text", "text": "Compacted."}]}
        })
        emit({
            "type": "result",
            "subtype": "success",
            "local_command": "compact",
            "is_error": False,
            "session_id": session_id,
            "modelUsage": {"claude-sonnet": {"contextWindow": 200000}},
            "usage": {"input_tokens": 500, "output_tokens": 0, "cache_read_input_tokens": 0}
        })
        return

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

    if "REQUIRE_APPROVAL_CANCEL" in content:
        approval_event.clear()
        emit({
            "type": "control_request",
            "request_id": "claude_cancel_perm_1",
            "session_id": session_id,
            "request": {
                "subtype": "can_use_tool",
                "tool_name": "Bash",
                "input": {"command": "echo cancel"}
            }
        })
        responded = approval_event.wait(timeout=5.0)
        resp_body = ((approval_payload.get("response") or {}).get("response") or {})
        behavior = resp_body.get("behavior", "none")
        emit({
            "type": "result",
            "subtype": "interrupted" if responded and behavior == "deny" else "error",
            "is_error": not (responded and behavior == "deny"),
            "session_id": session_id,
            "error": "" if responded and behavior == "deny" else f"responded={responded};behavior={behavior}"
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

    if content.startswith("WINDOW_UPDATE_"):
        source = content.removeprefix("WINDOW_UPDATE_")
        if source != "RESULT":
            usage = {"input_tokens": 200, "output_tokens": 20}
            if source == "ASSISTANT":
                usage["context_window_tokens"] = 1000000
            emit({"type": "assistant", "message": {"role": "assistant", "content": [], "usage": usage}})
        result = {"type": "result", "subtype": "success", "usage": {"input_tokens": 9000, "output_tokens": 1000}}
        if source == "MODEL":
            result["modelUsage"] = {"claude-sonnet-4-6": {"contextWindow": 1000000}}
        elif source.startswith("RESULT"):
            result["usage"]["context_window_tokens"] = 1000000
        emit(result)
        return

    if content.startswith("CONTEXT_CLAUDE"):
        if content != "CONTEXT_CLAUDE_FALLBACK":
            for count in (100, 200):
                emit({"type": "assistant", "session_id": session_id, "message": {"role": "assistant", "content": [], "usage": {"input_tokens": count, "output_tokens": 20, "cache_creation_input_tokens": 300, "cache_read_input_tokens": 400}}})
        result = {"type": "result", "subtype": "success", "usage": {"input_tokens": 9000, "output_tokens": 1000, "cache_creation_input_tokens": 2000, "cache_read_input_tokens": 3000}}
        if content != "CONTEXT_CLAUDE_CACHED_WINDOW":
            result["modelUsage"] = {"claude-sonnet-4-6": {"contextWindow": 200000}}
        emit(result)
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
    elif ftype == "control_request":
        emit({"type": "control_response", **({"session_id": session_id} if args.resume else {}), "response": {"subtype": "success", "request_id": frame.get("request_id"), "response": {}}})
    elif ftype == "user":
        t = threading.Thread(target=process_user_turn, args=(frame,), daemon=True)
        t.start()
