#!/usr/bin/env python3
import json
import os
import sys
import threading
import time

if "--version" in sys.argv:
    sys.stdout.write("acp-mock 1.4.2\n")
    sys.stdout.flush()
    sys.exit(0)

if os.environ.get("MOCK_ACP_PID_FILE"):
    with open(os.environ["MOCK_ACP_PID_FILE"], "w") as pid_file:
        pid_file.write(str(os.getpid()))

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

    if prompt_text == "/compact":
        usage_mode = os.environ.get("MOCK_ACP_COMPACT_USAGE", "notification")
        usage = {"used": 2048, "size": 1048576}
        if usage_mode == "notification":
            send({
                "jsonrpc": "2.0",
                "method": "session/update",
                "params": {
                    "sessionId": state["session_id"],
                    "update": {"sessionUpdate": "usage_update", **usage}
                }
            })
        result = {"stopReason": "end_turn"}
        if usage_mode == "response":
            result.update(usage)
        send({"jsonrpc": "2.0", "id": req_id, "result": result})
        return

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

    if "REQUIRE_PERMISSION_AFTER_CANCEL" in prompt_text:
        state["perm_event"].clear()
        state["perm_result"] = None
        send({
            "jsonrpc": "2.0",
            "method": "session/update",
            "params": {
                "sessionId": state["session_id"],
                "update": {"sessionUpdate": "agent_message_chunk", "content": {"type": "text", "text": "waiting-before-perm"}}
            }
        })
        state["cancel_event"].wait(timeout=5.0)
        send({
            "jsonrpc": "2.0",
            "id": "acp_perm_after_cancel_1",
            "method": "session/request_permission",
            "params": {
                "sessionId": state["session_id"],
                "toolCall": {
                    "toolCallId": "tc_perm_after_cancel_1",
                    "name": "run_command",
                    "rawInput": {"CommandLine": "sleep 10"}
                },
                "options": [
                    {"optionId": "opt_allow_once", "kind": "allow_once", "name": "Allow"},
                    {"optionId": "opt_reject_once", "kind": "reject_once", "name": "Deny"}
                ]
            }
        })
        responded = state["perm_event"].wait(timeout=5.0)
        res = state["perm_result"] or {}
        outcome_kind = (res.get("outcome") or {}).get("outcome", "none")
        if responded and outcome_kind == "cancelled":
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {"stopReason": "cancelled"}
            })
        else:
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {"error": {"code": "missing_post_cancel_outcome", "message": f"responded={responded};outcome={outcome_kind}"}}
            })
        return

    if "REQUIRE_PERMISSION_CANCEL" in prompt_text:
        state["perm_event"].clear()
        state["perm_result"] = None
        send({
            "jsonrpc": "2.0",
            "id": "acp_perm_cancel_1",
            "method": "session/request_permission",
            "params": {
                "sessionId": state["session_id"],
                "toolCall": {
                    "toolCallId": "tc_perm_cancel_1",
                    "name": "run_command",
                    "rawInput": {"CommandLine": "sleep 10"}
                },
                "options": [
                    {"optionId": "opt_allow_once", "kind": "allow_once", "name": "Allow"},
                    {"optionId": "opt_reject_once", "kind": "reject_once", "name": "Deny"}
                ]
            }
        })
        responded = state["perm_event"].wait(timeout=5.0)
        res = state["perm_result"] or {}
        outcome_kind = (res.get("outcome") or {}).get("outcome", "none")
        if responded and outcome_kind == "cancelled":
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {"stopReason": "cancelled"}
            })
        else:
            send({
                "jsonrpc": "2.0",
                "id": req_id,
                "result": {"error": {"code": "missing_cancel_outcome", "message": f"responded={responded};outcome={outcome_kind}"}}
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
                "options": json.loads(prompt_text.split("OPTIONS:", 1)[1]) if "OPTIONS:" in prompt_text else [
                    {"optionId": "opt_allow_once", "kind": "allow_once", "name": "Allow once"},
                    {"optionId": "opt_allow_always", "kind": "allow_always", "name": "Always allow"},
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
                    "content": {"type": "text", "text": f"acp-selected:{selected}" if selected != "none" else f"acp-outcome:{(res.get('outcome') or {}).get('outcome')}"}
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
    if prompt_text == "ACP_NO_SIZE_USAGE":
        send({
            "jsonrpc": "2.0",
            "id": req_id,
            "result": {
                "stopReason": "end_turn",
                "usage": {"inputTokens": 300, "outputTokens": 50}
            }
        })
        return
    if prompt_text.startswith("CONTEXT_ACP"):
        update = {"sessionUpdate": "usage_update", "used": 1234, "size": int(os.environ.get("MOCK_ACP_PROMPT_WINDOW", "1048576"))}
        send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": state["session_id"], "update": update}})
        if prompt_text == "CONTEXT_ACP_STANDARD":
            update = {"sessionUpdate": "usage_update", "usage": {"inputTokens": 200, "outputTokens": 75, "cachedReadTokens": 40}}
            send({"jsonrpc": "2.0", "method": "session/update", "params": {"sessionId": state["session_id"], "update": update}})
        result = {"stopReason": "end_turn"}
        if prompt_text != "CONTEXT_ACP_USED":
            result["usage"] = {"inputTokens": 210, "outputTokens": 80, "cachedReadTokens": 50}
        send({"jsonrpc": "2.0", "id": req_id, "result": result})
        return
    mcp_names = ",".join(sorted(s.get("name", "") for s in state["mcp_servers"]))
    worker_effort = f"|effort={state.get('worker_effort', 'normal')}" if os.environ.get("MOCK_ACP_WORKER_OPTIONS") == "1" else ""
    summary = f"acp[{state['session_method']}|{state['session_id']}|sub={state['subcommand_acp']}|model={state['model']}|via={state['model_set_method']}|mcp={mcp_names}{worker_effort}]:{prompt_text}"
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

def worker_config_options():
    models = [{"value": "large", "name": "Large"}]
    if state["session_method"] == "session/new":
        models.insert(0, {"value": "small", "name": "Small"})
    return [
        {"id": "worker-model", "category": "model", "type": "select", "currentValue": state["model"],
         "options": models},
        {"id": "worker-thought", "category": "thought_level", "type": "select", "currentValue": state.get("worker_effort", "normal"),
         "options": [{"value": "normal", "name": "Normal"}, {"value": "deep", "name": "Deep"}]}
    ]

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
    if os.environ.get("MOCK_ACP_LOG"):
        with open(os.environ["MOCK_ACP_LOG"], "a") as log:
            log.write(json.dumps(msg) + "\n")
    if method in ("session/set_model", "session/prompt") and os.environ.get("MOCK_ACP_PERMISSION_UPDATE"):
        send({"jsonrpc": "2.0", "method": "session/update", "params": {
            "sessionId": os.environ.get("MOCK_ACP_PERMISSION_UPDATE_SESSION", state["session_id"]),
            "update": {"sessionUpdate": "config_option_update", **json.loads(os.environ["MOCK_ACP_PERMISSION_UPDATE"])}}})
    if method == "initialize":
        mcp_caps = {"http": True} if os.environ.get("MOCK_ACP_HTTP_MCP") == "1" else {}
        if os.environ.get("MOCK_ACP_LOG") and os.environ.get("MOCK_ACP_LOG_ARGV") == "1":
            with open(os.environ["MOCK_ACP_LOG"], "a") as log:
                log.write(json.dumps({"argv": sys.argv[1:]}) + "\n")
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
            elif os.environ.get("MOCK_ACP_WORKER_OPTIONS") == "1":
                state["model"] = "large"
                send({"jsonrpc": "2.0", "id": msg_id, "result": {"configOptions": worker_config_options()}})
            elif "MOCK_ACP_MODELS" in os.environ:
                send({"jsonrpc": "2.0", "id": msg_id, "result": json.loads(os.environ["MOCK_ACP_MODELS"])})
            else:
                send({"jsonrpc": "2.0", "id": msg_id, "result": {}})
        else:
            state["session_id"] = "acp_sess_001"
            if os.environ.get("MOCK_ACP_PROJECT_MODES") == "1":
                mode_file = os.path.join(params["cwd"], "permission-modes.json")
                modes = json.load(open(mode_file)) if os.path.isfile(mode_file) else {}
                send({"jsonrpc": "2.0", "id": msg_id, "result": {"sessionId": state["session_id"], **modes}})
            elif os.environ.get("MOCK_ACP_WORKER_OPTIONS") == "1":
                state["model"] = "small"
                send({"jsonrpc": "2.0", "id": msg_id, "result": {"sessionId": state["session_id"], "configOptions": worker_config_options()}})
            elif "MOCK_ACP_MODELS" in os.environ:
                send({"jsonrpc": "2.0", "id": msg_id, "result": {"sessionId": state["session_id"], **json.loads(os.environ["MOCK_ACP_MODELS"])}})
            elif os.environ.get("MOCK_ACP_CONFIG_OPTIONS_MODELS") == "1":
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
        advertise_mode = os.environ.get("MOCK_ACP_ADVERTISE_COMPACT", "")
        post_delay_ms = int(os.environ.get("MOCK_ACP_POST_SESSION_DELAY_MS", "0"))
        post_usage_raw = os.environ.get("MOCK_ACP_POST_SESSION_USAGE")
        if advertise_mode == "empty_then_compact":
            send({
                "jsonrpc": "2.0",
                "method": "session/update",
                "params": {
                    "sessionId": state["session_id"],
                    "update": {"sessionUpdate": "available_commands_update", "availableCommands": []}
                }
            })

        def send_post_session_updates(sid=state["session_id"]):
            if post_delay_ms > 0:
                time.sleep(post_delay_ms / 1000.0)
            if advertise_mode in ("1", "empty_then_compact"):
                send({
                    "jsonrpc": "2.0",
                    "method": "session/update",
                    "params": {
                        "sessionId": sid,
                        "update": {
                            "sessionUpdate": "available_commands_update",
                            "availableCommands": [
                                {"name": "compact", "description": "Compact the conversation"}
                            ]
                        }
                    }
                })
            elif advertise_mode == "no_compact":
                send({
                    "jsonrpc": "2.0",
                    "method": "session/update",
                    "params": {
                        "sessionId": sid,
                        "update": {
                            "sessionUpdate": "available_commands_update",
                            "availableCommands": [
                                {"name": "help", "description": "Show help"}
                            ]
                        }
                    }
                })
            if post_usage_raw:
                parsed_usage = json.loads(post_usage_raw)
                usage_items = parsed_usage if isinstance(parsed_usage, list) else [parsed_usage]
                for usage_payload in usage_items:
                    send({
                        "jsonrpc": "2.0",
                        "method": "session/update",
                        "params": {
                            "sessionId": sid,
                            "update": {"sessionUpdate": "usage_update", **usage_payload}
                        }
                    })

        if advertise_mode in ("1", "empty_then_compact", "no_compact") or post_usage_raw:
            if post_delay_ms > 0:
                threading.Thread(target=send_post_session_updates, daemon=True).start()
            else:
                send_post_session_updates()
        exit_after_ms = int(os.environ.get("MOCK_ACP_EXIT_AFTER_SESSION_MS", "0"))
        if exit_after_ms > 0:
            def exit_soon():
                time.sleep(exit_after_ms / 1000.0)
                os._exit(0)
            threading.Thread(target=exit_soon, daemon=True).start()
    elif method == "session/close":
        if params.get("sessionId") == state["session_id"]:
            result = {"success": False} if os.environ.get("MOCK_ACP_INVALID_CLOSE") == "1" else {}
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})
        else:
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32602, "message": "wrong session"}})
    elif method == "session/set_mode":
        code = int(os.environ.get("MOCK_ACP_MODE_ERROR", "0"))
        if code:
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": "mode switch failed"}})
        else:
            send({"jsonrpc": "2.0", "id": msg_id, "result": json.loads(os.environ.get("MOCK_ACP_PERMISSION_RESPONSE", "{}"))})
    elif method == "session/set_model":
        if os.environ.get("MOCK_ACP_STRICT_MODELS") == "1":
            advertised = json.loads(os.environ["MOCK_ACP_MODELS"])["models"]["availableModels"]
            if params.get("modelId") not in [m["modelId"] for m in advertised]:
                send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32602, "message": "unadvertised model"}})
                continue
        code = int(os.environ.get("MOCK_ACP_MODEL_ERROR", "0"))
        if code:
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": "model switch failed"}})
        elif os.environ.get("MOCK_ACP_NO_SET_MODEL") == "1":
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": "Method not found"}})
        else:
            state["model"] = params.get("modelId")
            state["model_set_method"] = "session/set_model"
            if os.environ.get("MOCK_ACP_RESET_MODEL_EFFORT") == "1":
                state["worker_effort"] = "normal"
            result = {"configOptions": worker_config_options()} if os.environ.get("MOCK_ACP_WORKER_OPTIONS") == "1" and os.environ.get("MOCK_ACP_MODEL_RESPONSE_OPTIONS") != "0" else {}
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})
            switch_usage_raw = os.environ.get("MOCK_ACP_MODEL_SWITCH_USAGE")
            if switch_usage_raw:
                send({
                    "jsonrpc": "2.0",
                    "method": "session/update",
                    "params": {
                        "sessionId": state["session_id"],
                        "update": {"sessionUpdate": "usage_update", **json.loads(switch_usage_raw)}
                    }
                })
    elif method == "session/set_config_option":
        mode_error = int(os.environ.get("MOCK_ACP_MODE_ERROR", "0"))
        if params.get("configId") == "permission-policy" and mode_error:
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": mode_error, "message": "mode switch failed"}})
            continue
        if params.get("configId") == "worker-thought" and os.environ.get("MOCK_ACP_THOUGHT_DELAY_MS"):
            time.sleep(int(os.environ["MOCK_ACP_THOUGHT_DELAY_MS"]) / 1000)
        if params.get("configId") == "worker-thought" and os.environ.get("MOCK_ACP_THOUGHT_RESULT"):
            send({"jsonrpc": "2.0", "id": msg_id, "result": json.loads(os.environ["MOCK_ACP_THOUGHT_RESULT"])})
            continue
        code = int(os.environ.get("MOCK_ACP_MODEL_ERROR", "0"))
        if code and params.get("configId") == "model":
            send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": code, "message": "model switch failed"}})
            continue
        if os.environ.get("MOCK_ACP_WORKER_OPTIONS") == "1":
            if params.get("configId") in ("worker-model", "model"):
                state["model"] = params.get("value")
                if os.environ.get("MOCK_ACP_RESET_MODEL_EFFORT") == "1":
                    state["worker_effort"] = "normal"
            if params.get("configId") == "worker-thought" and os.environ.get("MOCK_ACP_REJECT_WORKER_EFFORT") != "1":
                state["worker_effort"] = params.get("value")
            result = {} if params.get("configId") == "model" and os.environ.get("MOCK_ACP_MODEL_RESPONSE_OPTIONS") == "0" else {"configOptions": worker_config_options()}
            send({"jsonrpc": "2.0", "id": msg_id, "result": result})
            continue
        if params.get("configId") == "model":
            state["model"] = params.get("value")
            state["model_set_method"] = "session/set_config_option"
        send({"jsonrpc": "2.0", "id": msg_id, "result": json.loads(os.environ.get("MOCK_ACP_PERMISSION_RESPONSE", "{}"))})
    elif method == "session/prompt":
        state["cancel_event"].clear()
        t = threading.Thread(target=handle_prompt, args=(msg_id, params), daemon=True)
        t.start()
    elif method == "session/cancel":
        state["cancel_event"].set()
    elif msg_id is not None:
        send({"jsonrpc": "2.0", "id": msg_id, "error": {"code": -32601, "message": f"Method not found: {method}"}})
