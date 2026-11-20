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

    if prompt in ("RECONNECT_THEN_COMPLETE", "RECONNECT_THEN_FAIL"):
        send({
            "jsonrpc": "2.0",
            "method": "error",
            "params": {
                "threadId": state["thread_id"],
                "turnId": turn_id,
                "willRetry": True,
                "error": {"code": "stream_disconnected", "message": "Reconnecting... 2/5", "isRetryable": False}
            }
        })
        if prompt == "RECONNECT_THEN_FAIL":
            send({
                "jsonrpc": "2.0",
                "method": "error",
                "params": {
                    "threadId": state["thread_id"],
                    "turnId": turn_id,
                    "willRetry": False,
                    "error": {"code": "stream_disconnected", "message": "Reconnect attempts exhausted", "isRetryable": True}
                }
            })
            return

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

    if "REQUIRE_APPROVAL_CANCEL" in prompt:
        state["approval_event"].clear()
        state["approval_result"] = None
        send({
            "jsonrpc": "2.0",
            "id": "codex_cancel_req_1",
            "method": "item/commandExecution/requestApproval",
            "params": {
                "itemId": "cmd_cancel_1",
                "toolName": "commandExecution",
                "arguments": {"command": "git status"}
            }
        })
        responded = state["approval_event"].wait(timeout=5.0)
        res = state["approval_result"] or {}
        decision = res.get("decision", "none")
        send({
            "jsonrpc": "2.0",
            "method": "turn/completed",
            "params": {
                "threadId": state["thread_id"],
                "turn": {
                    "id": turn_id,
                    "status": "interrupted" if responded and decision == "decline" else "failed",
                    "error": None if responded and decision == "decline" else {"code": "missing_decline", "message": f"responded={responded};decision={decision}"}
                }
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
    if prompt.startswith("CONTEXT_CODEX"):
        token_usage = {"modelContextWindow": 272000, "total": {"inputTokens": 99000, "outputTokens": 5000}}
        if prompt != "CONTEXT_CODEX_TOTAL_ONLY":
            token_usage["last"] = {"inputTokens": 120, "outputTokens": 45, "cachedInputTokens": 30, "totalTokens": 165}
        send({"jsonrpc": "2.0", "method": "thread/tokenUsage/updated", "params": {"threadId": state["thread_id"], "tokenUsage": token_usage}})
        if prompt == "CONTEXT_CODEX_MERGE":
            send({"jsonrpc": "2.0", "method": "turn/usage", "params": {"usage": {"inputTokens": 130, "outputTokens": 50}}})
        turn = {"id": turn_id, "status": "completed"}
        if prompt == "CONTEXT_CODEX_MERGE":
            turn["usage"] = {"inputTokens": 140, "outputTokens": 55}
        send({"jsonrpc": "2.0", "method": "turn/completed", "params": {"threadId": state["thread_id"], "turn": turn}})
        return
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
    if os.environ.get("MOCK_CODEX_LOG"):
        with open(os.environ["MOCK_CODEX_LOG"], "a", encoding="utf-8") as log:
            log.write(json.dumps(msg) + "\n")
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
    elif method == "config/read" and os.environ.get("MOCK_CODEX_CONFIG_JSON"):
        send({"jsonrpc": "2.0", "id": msg_id, "result": {"config": json.loads(os.environ["MOCK_CODEX_CONFIG_JSON"])}})
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
        if prompt_text in ("REJECT_WORKER_CONFIGURATION", "REJECT_WORKER_CONFIGURATION_EARLY_EVENT"):
            failed = {"id": turn_id, "status": "failed",
                      "error": {"code": "model_not_found", "message": "Model not found"}}
            if prompt_text == "REJECT_WORKER_CONFIGURATION_EARLY_EVENT":
                send({"jsonrpc": "2.0", "method": "turn/completed",
                      "params": {"threadId": state["thread_id"], "turn": failed}})
                send({"jsonrpc": "2.0", "id": msg_id, "result": {"turn": {"id": turn_id, "status": "inProgress"}}})
                continue
            send({"jsonrpc": "2.0", "id": msg_id, "result": {"turn": failed}})
            continue
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
    elif method == "thread/compact/start":
        compact_turn_id = f"turn_compact_{state['turn_counter'] + 1}"
        state["turn_counter"] += 1
        send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
        send({"jsonrpc": "2.0", "method": "turn/started", "params": {
            "threadId": state["thread_id"],
            "turn": {"id": compact_turn_id, "status": "inProgress"}
        }})
        if os.environ.get("MOCK_CODEX_HANG_COMPACT") == "1":
            continue
        delay_ms = int(os.environ.get("MOCK_CODEX_DELAY_COMPACT_MS", "0"))
        def finish_compact(tid=compact_turn_id, d=delay_ms):
            if d > 0:
                time.sleep(d / 1000.0)
            send({"jsonrpc": "2.0", "method": "thread/tokenUsage/updated", "params": {
                "threadId": state["thread_id"],
                "turnId": tid,
                "tokenUsage": {
                    "last": {"inputTokens": 1200, "outputTokens": 0, "cachedInputTokens": 0},
                    "modelContextWindow": 100000
                }
            }})
            send({"jsonrpc": "2.0", "method": "turn/completed", "params": {
                "threadId": state["thread_id"],
                "turn": {"id": tid, "status": "completed"}
            }})
        if delay_ms > 0:
            threading.Thread(target=finish_compact, daemon=True).start()
        else:
            finish_compact()
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
